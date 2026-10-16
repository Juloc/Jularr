using System.Collections.Concurrent;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Logic.Translation;
using Jularr.Integrations.Data.Translation;
using Jularr.Integrations.Logic.Translation;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Jularr.Backend.Service.Core
{
    // The Bergamot engine and language models in the translation folder, each fetched from Mozilla the first time a reader needs it.
    internal sealed class TranslationFiles(IServiceProvider services, IOptions<MediaOptions> options, IMemoryCache cache)
    {
        private static readonly ConcurrentDictionary<string, SemaphoreSlim> Downloads = new(StringComparer.Ordinal);
        private static readonly TimeSpan ListLifetime = TimeSpan.FromHours(12);

        public bool IsAvailable => options.Value.TranslationPath is not null;

        // The engine, then the parts of each model on the way from source to target.
        public async Task<IReadOnlyList<(int Step, TranslationFile File, string? Precision)>> RouteAsync(string source, string target, CancellationToken ct)
        {
            var (engines, models) = await ListsAsync(ct);
            var engine = TranslationModels.Engine(engines) ?? throw new ServiceUnavailableException("translation_unavailable");
            var files = new List<(int, TranslationFile, string?)> { (0, engine, null) };
            var step = 0;
            foreach (var (from, to) in TranslationModels.Route(source, target))
            {
                step++;
                var model = TranslationModels.Model(models, from, to) ?? throw new ServiceUnavailableException("translation_unavailable");
                var precision = TranslationModels.Precision(model);
                files.AddRange(model.Select(file => (step, file, (string?)precision)));
            }

            return files;
        }

        public async Task<(string[] Sources, string[] Targets)> LanguagesAsync(CancellationToken ct) => TranslationModels.Languages((await ListsAsync(ct)).Models);

        // Only files Mozilla lists are served; each is downloaded once and kept under its hash.
        public async Task<string> PathAsync(string name, CancellationToken ct)
        {
            var root = options.Value.TranslationPath ?? throw new ServiceUnavailableException("translation_unavailable");
            var (engines, models) = await ListsAsync(ct);
            var file = engines.Concat(models).FirstOrDefault(candidate => TranslationModels.FileName(candidate) == name) ?? throw new KeyNotFoundException("Unknown translation file.");
            var path = Path.Combine(root, name);
            if (File.Exists(path))
            {
                return path;
            }

            var gate = Downloads.GetOrAdd(name, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(ct);
            try
            {
                if (!File.Exists(path))
                {
                    Directory.CreateDirectory(root);
                    await services.GetRequiredService<MozillaTranslationsClient>().DownloadAsync(file, path, ct);
                }
            }
            catch (TranslationSourceException exception)
            {
                throw new ServiceUnavailableException("translation_unavailable", exception);
            }
            finally
            {
                gate.Release();
            }

            return path;
        }

        private async Task<(IReadOnlyList<TranslationFile> Engines, IReadOnlyList<TranslationFile> Models)> ListsAsync(CancellationToken ct)
        {
            if (cache.TryGetValue("translation.files", out (IReadOnlyList<TranslationFile>, IReadOnlyList<TranslationFile>) lists))
            {
                return lists;
            }

            try
            {
                var client = services.GetRequiredService<MozillaTranslationsClient>();
                lists = (await client.ReadEnginesAsync(ct), await client.ReadModelsAsync(ct));
            }
            catch (TranslationSourceException exception)
            {
                throw new ServiceUnavailableException("translation_unavailable", exception);
            }

            cache.Set("translation.files", lists, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = ListLifetime, Size = 1 });
            return lists;
        }
    }
}
