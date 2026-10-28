using System.Collections.Concurrent;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Logic.Playback;
using Jularr.Backend.Service.Core;
using Jularr.Integrations.Logic.Translation;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Jularr.Backend.Service.User.Playback.V1
{
    // Translates a subtitle with the local model, in the background and one batch of cues at a time, into a WebVTT file of the playback cache.
    // The model is shared with the books' translation: each batch waits for its turn at the gate, so a reader is never held up for long.
    internal sealed class SubtitleTranslations(OllamaLocalModels models, IOptions<LocalTranslationOptions> translation, LocalTranslationGate gate, IOptions<MediaOptions> media,
        ILogger<SubtitleTranslations> logger)
    {
        public const string Queued = "queued", Running = "running", Done = "done", Failed = "failed", None = "none";

        private sealed class Job
        {
            public string State = Running;
            public int Done;
            public int Total;
        }

        private readonly ConcurrentDictionary<string, Job> _jobs = new();

        public string Path(string cacheKey) => global::System.IO.Path.Combine(media.Value.TranscodeCachePath, "subtitles", "translated", cacheKey + ".vtt");

        // The installed model that translates; none means the local runtime is not set up.
        public async Task<(Uri Server, string Model)> ModelAsync(CancellationToken ct)
        {
            var server = OllamaLocalModels.Endpoint(translation.Value.Endpoint) ?? throw new ServiceUnavailableException("translation_local_unavailable");
            try
            {
                var installed = await models.InstalledAsync(server, ct);
                return (server, OllamaLocalModels.Candidates.FirstOrDefault(candidate => installed.Contains(candidate, StringComparer.Ordinal)) ?? throw new ServiceUnavailableException("translation_local_unavailable"));
            }
            catch (HttpRequestException)
            {
                throw new ServiceUnavailableException("translation_local_unavailable");
            }
        }

        public (string State, int Done, int Total) Status(string cacheKey)
        {
            if (File.Exists(Path(cacheKey)))
            {
                return (Done, 1, 1);
            }

            return _jobs.TryGetValue(cacheKey, out var job) ? (job.State, job.Done, job.Total) : (None, 0, 0);
        }

        // Starts the translation unless it runs or is done; a failed one may be started again.
        public (string State, int Done, int Total) Start(string cacheKey, string vtt, string sourceLanguage, string targetLanguage, Uri server, string model)
        {
            if (File.Exists(Path(cacheKey)))
            {
                return (Done, 1, 1);
            }

            var job = new Job();
            var current = _jobs.AddOrUpdate(cacheKey, job, (_, existing) => existing.State == Failed ? job : existing);
            if (ReferenceEquals(current, job))
            {
                _ = Task.Run(() => RunAsync(cacheKey, job, vtt, sourceLanguage, targetLanguage, server, model));
            }

            return (current.State, current.Done, current.Total);
        }

        // A dropped connection to the runtime is tried once more before the translation gives up.
        private async Task<(string Text, double Seconds, double TokensPerSecond)> TranslateAsync(Uri server, string model, string source, string target, string text)
        {
            try
            {
                return await models.TranslateAsync(server, model, source, target, text, CancellationToken.None);
            }
            catch (HttpRequestException)
            {
                await Task.Delay(TimeSpan.FromSeconds(2));
                return await models.TranslateAsync(server, model, source, target, text, CancellationToken.None);
            }
        }

        private async Task RunAsync(string cacheKey, Job job, string vtt, string source, string target, Uri server, string model)
        {
            try
            {
                var cues = SubtitleTranslationLogic.ParseCues(vtt);
                if (cues.Count == 0)
                {
                    throw new InvalidDataException("The subtitle has no cues.");
                }

                var batches = SubtitleTranslationLogic.Batches(cues);
                job.Total = batches.Sum(batch => batch.Count);
                var translated = new string?[cues.Count];
                foreach (var batch in batches)
                {
                    await gate.HeavyWork.WaitAsync();
                    try
                    {
                        var answer = await TranslateAsync(server, model, source, target, SubtitleTranslationLogic.BatchText(cues, batch));
                        var parsed = SubtitleTranslationLogic.ParseBatch(answer.Text, batch.Count);
                        for (var number = 0; number < batch.Count; number++)
                        {
                            // A batch that came back with missing numbers is translated cue by cue.
                            translated[batch[number]] = parsed?[number]
                                ?? (await TranslateAsync(server, model, source, target, SubtitleTranslationLogic.PlainText(cues[batch[number]].Text))).Text;
                        }
                    }
                    finally
                    {
                        gate.HeavyWork.Release();
                    }

                    job.Done += batch.Count;
                }

                var path = Path(cacheKey);
                Directory.CreateDirectory(global::System.IO.Path.GetDirectoryName(path)!);
                var pending = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                await File.WriteAllTextAsync(pending, SubtitleTranslationLogic.Compose(cues, translated));
                File.Move(pending, path, true);
                job.State = Done;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "A subtitle translation failed.");
                job.State = Failed;
            }
        }
    }
}
