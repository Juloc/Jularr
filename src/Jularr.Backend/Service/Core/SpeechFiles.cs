using System.Security.Cryptography;
using System.Text.Json;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Logic.Speech;
using Jularr.Integrations.Logic.Speech;
using Microsoft.Extensions.Options;

namespace Jularr.Backend.Service.Core
{
    internal sealed class SpeechFiles(IOptions<MediaOptions> options, SpeechModelClient client)
    {
        private readonly SemaphoreSlim _download = new(1, 1);

        // The ordered file replaces the shipped recommendations; it contains metadata, never spoken text.
        internal async Task<IReadOnlyList<SpeechModel>> CatalogAsync(CancellationToken ct)
        {
            var path = Path.Combine(options.Value.SpeechPath, "catalog.json");
            try
            {
                if (File.Exists(path))
                {
                    if (new FileInfo(path).Length > SpeechCatalog.MaximumCatalogBytes)
                    {
                        throw new InvalidDataException("The speech catalog is too large.");
                    }

                    return SpeechCatalog.Parse(await File.ReadAllTextAsync(path, ct));
                }

                using var source = new StreamReader(typeof(SpeechFiles).Assembly.GetManifestResourceStream("Jularr.Speech.models.json")!);
                return SpeechCatalog.Parse(await source.ReadToEndAsync(ct));
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or JsonException)
            {
                throw new ServiceUnavailableException("speech_catalog_unavailable", exception);
            }
        }

        internal string PathFor(string hash)
        {
            if (!SpeechCatalog.Hash().IsMatch(hash)) throw new KeyNotFoundException("Unknown speech package.");
            return Path.Combine(options.Value.SpeechPath, hash + ".tar.bz2");
        }

        // No GET, page load or startup can trigger a model download.
        internal async Task InstallAsync(SpeechModel model, CancellationToken ct)
        {
            await _download.WaitAsync(ct);
            try
            {
                Directory.CreateDirectory(options.Value.SpeechPath);
                var path = PathFor(model.Sha256);
                if (File.Exists(path) && new FileInfo(path).Length == model.SizeBytes)
                {
                    await using var existing = File.OpenRead(path);
                    if (Convert.ToHexStringLower(await SHA256.HashDataAsync(existing, ct)) == model.Sha256) return;
                }

                await client.DownloadAsync(model.Source, model.SizeBytes, model.Sha256, path, ct);
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or HttpRequestException || exception is OperationCanceledException && !ct.IsCancellationRequested)
            {
                throw new ServiceUnavailableException("speech_download_failed", exception);
            }
            finally { _download.Release(); }
        }
    }
}
