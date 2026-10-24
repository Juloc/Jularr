using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;

namespace Jularr.Backend.Service.Core
{
    // AniBridge's published mappings are a read-only crosswalk; Jularr keeps its own canonical identities.
    internal sealed class AniBridgeMappings(HttpClient http, IMemoryCache cache)
    {
        private const string SourceUrl = "https://github.com/anibridge/anibridge-mappings/releases/download/v3/mappings.min.json";
        private const string CacheKey = "metadata.anibridge.v3";
        private static readonly SemaphoreSlim DownloadGate = new(1, 1);

        internal sealed record Link(int? TvdbSeriesId, int? TvdbSeasonNumber, int? TmdbSeriesId, int? TmdbMovieId);

        private sealed record Snapshot(DateTimeOffset RefreshAfter, IReadOnlyDictionary<int, Link> Links);

        public async Task<Link?> ReadAsync(int aniListId, CancellationToken ct)
        {
            cache.TryGetValue(CacheKey, out Snapshot? snapshot);
            if (snapshot is not null && snapshot.RefreshAfter > DateTimeOffset.UtcNow)
            {
                return snapshot.Links.GetValueOrDefault(aniListId);
            }

            await DownloadGate.WaitAsync(ct);
            try
            {
                cache.TryGetValue(CacheKey, out snapshot);
                if (snapshot is null || snapshot.RefreshAfter <= DateTimeOffset.UtcNow)
                {
                    try
                    {
                        using var response = await http.GetAsync(SourceUrl, ct);
                        response.EnsureSuccessStatusCode();
                        await using var stream = await response.Content.ReadAsStreamAsync(ct);
                        using var document = await JsonDocument.ParseAsync(stream, new JsonDocumentOptions { MaxDepth = 32 }, ct);
                        snapshot = new Snapshot(DateTimeOffset.UtcNow.AddDays(1), Parse(document.RootElement));
                        cache.Set(CacheKey, snapshot, new MemoryCacheEntryOptions { Size = 100 });
                    }
                    catch (Exception exception) when (!ct.IsCancellationRequested
                        && exception is HttpRequestException or JsonException or IOException or TaskCanceledException or InvalidOperationException)
                    {
                        // Retain the previous mappings and retry later instead of blocking every Discover request.
                        snapshot = snapshot is null
                            ? new Snapshot(DateTimeOffset.UtcNow.AddHours(1), new Dictionary<int, Link>())
                            : snapshot with { RefreshAfter = DateTimeOffset.UtcNow.AddHours(1) };
                        cache.Set(CacheKey, snapshot, new MemoryCacheEntryOptions { Size = 100 });
                    }
                }

                return snapshot.Links.GetValueOrDefault(aniListId);
            }
            finally
            {
                DownloadGate.Release();
            }
        }

        internal static IReadOnlyDictionary<int, Link> Parse(JsonElement root)
        {
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("$meta", out var metadata)
                || !metadata.TryGetProperty("schema_version", out var schema) || schema.GetString() is not { } version || !version.StartsWith("3.", StringComparison.Ordinal))
            {
                throw new JsonException("Unsupported AniBridge mappings schema.");
            }

            var links = new Dictionary<int, Link>();
            foreach (var source in root.EnumerateObject())
            {
                if (!source.Name.StartsWith("anilist:", StringComparison.Ordinal)
                    || !int.TryParse(source.Name.AsSpan("anilist:".Length), NumberStyles.None, CultureInfo.InvariantCulture, out var aniListId)
                    || source.Value.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var tvdb = new HashSet<(int Id, int Season)>();
                var tmdb = new HashSet<int>();
                var films = new HashSet<int>();
                foreach (var target in source.Value.EnumerateObject())
                {
                    if (target.Value.ValueKind != JsonValueKind.Object || !target.Value.EnumerateObject().Any(range => range.Value.ValueKind == JsonValueKind.String))
                    {
                        continue;
                    }

                    var parts = target.Name.Split(':');
                    if (parts.Length == 3 && parts[2].Length > 1 && parts[2][0] == 's'
                        && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var seriesId)
                        && int.TryParse(parts[2].AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var seasonNumber))
                    {
                        if (parts[0] == "tvdb_show") tvdb.Add((seriesId, seasonNumber));
                        if (parts[0] == "tmdb_show") tmdb.Add(seriesId);
                    }
                    else if (parts.Length == 2 && parts[0] == "tmdb_movie"
                        && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var filmId))
                    {
                        films.Add(filmId);
                    }
                }

                // Ambiguous provider IDs or seasons are not automatically chosen.
                if (tvdb.Count > 0 || tmdb.Count > 0 || films.Count > 0)
                {
                    var onlyTvdb = tvdb.Count == 1 ? tvdb.Single() : ((int Id, int Season)?)null;
                    links[aniListId] = new Link(onlyTvdb?.Id, onlyTvdb?.Season, tmdb.Count == 1 ? tmdb.Single() : null, films.Count == 1 ? films.Single() : null);
                }
            }

            return links;
        }
    }
}