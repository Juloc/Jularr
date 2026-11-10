using System.Collections.Concurrent;
using Jularr.Backend.Data.Admin.Translation.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Logic.Playback;
using Jularr.Backend.Service.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Jularr.Backend.Service.User.Playback.V1
{
    // Translates a subtitle with the local model, in the background and one batch of cues at a time, into a WebVTT file of the playback cache.
    // The model is shared with the books' translation: each batch waits for its provider's lane like a reader (before the book job) and gives up
    // after LaneWait, so a reader is never held up for long; stopping Jularr ends the translation.
    internal sealed class SubtitleTranslations(TranslationProviders providers, TranslationLanes lanes, IOptions<MediaOptions> media, IHostApplicationLifetime lifetime,
        ILogger<SubtitleTranslations> logger)
    {
        public const string Queued = "queued", Running = "running", Done = "done", Failed = "failed", None = "none";

        // How long a batch waits for the lane: a translation runs eight minutes at most, so a held lane is free again within it.
        internal TimeSpan LaneWait { get; init; } = TimeSpan.FromMinutes(10);

        private sealed class Job
        {
            public string State = Running;
            public int Done;
            public int Total;
        }

        private readonly ConcurrentDictionary<string, Job> _jobs = new();

        public string Path(string cacheKey) => global::System.IO.Path.Combine(media.Value.TranscodeCachePath, "subtitles", "translated", cacheKey + ".vtt");

        // The model that translates (the Owner's default, else the largest installed TranslateGemma); none means the runtime is not set up.
        public async Task<TranslationMachine> ModelAsync(TranslationSetupV1 setup, CancellationToken ct) =>
            await providers.ChooseAsync(setup, ct) ?? throw new ServiceUnavailableException("translation_local_unavailable");

        public (string State, int Done, int Total) Status(string cacheKey)
        {
            if (File.Exists(Path(cacheKey)))
            {
                return (Done, 1, 1);
            }

            return _jobs.TryGetValue(cacheKey, out var job) ? (job.State, job.Done, job.Total) : (None, 0, 0);
        }

        // Starts the translation unless it runs or is done; a failed one may be started again.
        public (string State, int Done, int Total) Start(string cacheKey, string vtt, string sourceLanguage, string targetLanguage, TranslationMachine machine)
        {
            if (File.Exists(Path(cacheKey)))
            {
                return (Done, 1, 1);
            }

            var job = new Job();
            var current = _jobs.AddOrUpdate(cacheKey, job, (_, existing) => existing.State == Failed ? job : existing);
            if (ReferenceEquals(current, job))
            {
                _ = Task.Run(() => RunAsync(cacheKey, job, vtt, sourceLanguage, targetLanguage, machine));
            }

            return (current.State, current.Done, current.Total);
        }

        // A dropped connection to the runtime is tried once more before the translation gives up.
        private static async Task<string> TranslateAsync(TranslationMachine machine, string source, string target, string text, CancellationToken ct)
        {
            try
            {
                return await machine.Provider.TranslateAsync(machine, source, target, text, ct);
            }
            catch (HttpRequestException)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
                return await machine.Provider.TranslateAsync(machine, source, target, text, ct);
            }
        }

        private async Task RunAsync(string cacheKey, Job job, string vtt, string source, string target, TranslationMachine machine)
        {
            var stopping = lifetime.ApplicationStopping;
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
                    using (await lanes.TryEnterAsync(machine.Provider.Key, LaneWait, stopping) ?? throw new TimeoutException("The translation lane stayed busy."))
                    {
                        var answer = await TranslateAsync(machine, source, target, SubtitleTranslationLogic.BatchText(cues, batch), stopping);
                        var parsed = SubtitleTranslationLogic.ParseBatch(answer, batch.Count);
                        for (var number = 0; number < batch.Count; number++)
                        {
                            // A batch that came back with missing numbers is translated cue by cue.
                            translated[batch[number]] = parsed?[number]
                                ?? await TranslateAsync(machine, source, target, SubtitleTranslationLogic.PlainText(cues[batch[number]].Text), stopping);
                        }
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
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
                job.State = Failed;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "A subtitle translation failed.");
                job.State = Failed;
            }
        }
    }
}
