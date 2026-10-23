using System.Security.Cryptography;
using System.Text.Json;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Logic.Speech;
using Jularr.Integrations.Logic.Speech;
using Microsoft.Extensions.Options;

namespace Jularr.Backend.Service.Core
{
    internal sealed class SpeechFiles(IOptions<MediaOptions> options, SpeechModelClient client, SpeechCatalogClient catalogClient)
    {
        private readonly SemaphoreSlim _download = new(1, 1);
        private readonly SemaphoreSlim _discovery = new(1, 1);
        private DateTimeOffset _nextRefresh;
        private IReadOnlyList<SpeechModel>? _lastDiscovery;
        private static readonly JsonSerializerOptions CatalogJson = new(JsonSerializerDefaults.Web);

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

                    var configured = SpeechCatalog.Parse(await File.ReadAllTextAsync(path, ct));
                    return Merge(configured, await DiscoveredAsync(ct));
                }

                using var source = new StreamReader(typeof(SpeechFiles).Assembly.GetManifestResourceStream("Jularr.Speech.models.json")!);
                return Merge(SpeechCatalog.Parse(await source.ReadToEndAsync(ct)), await DiscoveredAsync(ct));
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or JsonException)
            {
                throw new ServiceUnavailableException("speech_catalog_unavailable", exception);
            }
        }

        // Explicit metadata refresh only; no archive is fetched here. A short shared cooldown
        // bounds repeated clicks and source outages, without delaying reads of the saved catalog.
        internal async Task<IReadOnlyList<SpeechModel>> DiscoverAsync(CancellationToken ct)
        {
            await _discovery.WaitAsync(ct);
            try
            {
                if (DateTimeOffset.UtcNow < _nextRefresh)
                {
                    return _lastDiscovery ?? throw new ServiceUnavailableException("speech_discovery_failed");
                }

                _nextRefresh = DateTimeOffset.UtcNow.AddMinutes(1);
                _lastDiscovery = null;
                var packages = await catalogClient.ReadAsync(ct);
                var candidates = packages.Select(package => new SpeechModel("piper-" + package.Key, package.Sha256[..12], package.Name, package.Language, "0",
                    "Review model card", new Uri(package.ModelCard), new Uri(package.Source), package.SizeBytes, package.Sha256, true, new Uri(package.SampleUrl))).ToArray();
                // Source locale aliases are not guessed: apply the canonical field/catalog rules
                // before merging, so one unsupported locale cannot reject every valid package.
                var verified = SpeechCatalog.Parse(JsonSerializer.Serialize(new { models = candidates }, CatalogJson));
                if (verified.Count == 0) throw new InvalidDataException("No compatible speech packages were found.");
                _lastDiscovery = verified;
                return _lastDiscovery;
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or HttpRequestException or JsonException or InvalidOperationException
                || exception is OperationCanceledException && !ct.IsCancellationRequested)
            {
                throw new ServiceUnavailableException("speech_discovery_failed", exception);
            }
            finally { _discovery.Release(); }
        }

        internal async Task SaveDiscoveredAsync(IReadOnlyList<SpeechModel> discovered, CancellationToken ct)
        {
            var merged = Merge(await CatalogAsync(ct), discovered);
            var json = JsonSerializer.Serialize(new { models = merged }, CatalogJson);
            // The same strict reader validates persisted and externally supplied metadata.
            if (SpeechCatalog.Parse(json).Count != merged.Count) throw new ServiceUnavailableException("speech_discovery_failed");
            var path = Path.Combine(options.Value.SpeechPath, "discovered.json");
            var partial = path + "." + Guid.NewGuid().ToString("N") + ".partial";
            try
            {
                Directory.CreateDirectory(options.Value.SpeechPath);
                await File.WriteAllTextAsync(partial, json, ct);
                File.Move(partial, path, overwrite: true);
            }
            catch (IOException exception) { throw new ServiceUnavailableException("speech_discovery_failed", exception); }
            finally { File.Delete(partial); }
        }

        private async Task<IReadOnlyList<SpeechModel>> DiscoveredAsync(CancellationToken ct)
        {
            var path = Path.Combine(options.Value.SpeechPath, "discovered.json");
            if (!File.Exists(path)) return [];
            if (new FileInfo(path).Length > SpeechCatalog.MaximumCatalogBytes) throw new InvalidDataException("The speech catalog is too large.");
            return SpeechCatalog.Parse(await File.ReadAllTextAsync(path, ct));
        }

        private static IReadOnlyList<SpeechModel> Merge(IReadOnlyList<SpeechModel> pinned, IReadOnlyList<SpeechModel> discovered)
        {
            var result = pinned.ToList();
            var ids = result.Select(model => model.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var model in discovered)
            {
                var existing = result.FindIndex(item => item.Sha256 == model.Sha256);
                if (existing >= 0)
                {
                    // Keep reviewed license, identity and profile choices; discovery may add a sample.
                    result[existing] = result[existing] with { SampleUrl = result[existing].SampleUrl ?? model.SampleUrl };
                }
                else if (!ids.Contains(model.Id))
                {
                    ids.Add(model.Id);
                    result.Add(model);
                }
            }

            if (result.Count > SpeechCatalog.MaximumEntries) throw new InvalidDataException("The speech catalog is full.");
            return result;
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
