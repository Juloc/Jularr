using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Jularr.Backend.Data.Logic.Media;

namespace Jularr.Backend.Service.Core
{
    // AniBridge's published mappings are a read-only crosswalk; Jularr keeps its own canonical identities.
    internal sealed class AniBridgeMappings(HttpClient http, IMemoryCache cache)
    {
        private const string SourceUrl = "https://github.com/anibridge/anibridge-mappings/releases/download/v3/mappings.min.json";
        private const string CacheKey = "metadata.anibridge.v3";
        private static readonly SemaphoreSlim DownloadGate = new(1, 1);

        internal sealed record Link(int? TvdbSeriesId, int? TvdbSeasonNumber, int? TmdbSeriesId, int? TmdbMovieId);

        private sealed record TvdbScope(int SeasonNumber, JsonElement Targets);
        private sealed record Snapshot(DateTimeOffset RefreshAfter, IReadOnlyDictionary<int, Link> Links, IReadOnlyDictionary<int, IReadOnlyList<TvdbScope>> TvdbScopes);
        internal sealed record SeasonMapping(bool RequiresReview, IReadOnlyList<(int Season, int AniListId, int Offset)> Seasons, IReadOnlyList<int> CandidateIds);

        public async Task<Link?> ReadAsync(int aniListId, CancellationToken ct) =>
            (await GetSnapshotAsync(ct)).Links.GetValueOrDefault(aniListId);

        // TVDB episode order is only translated when every range fits the current season-offset model.
        public async Task<SeasonMapping?> ReadTvdbAsync(int tvdbId, IReadOnlyList<SeasonEpisodesData> seasons, CancellationToken ct) =>
            AlignTvdb((await GetSnapshotAsync(ct)).TvdbScopes, tvdbId, seasons);

        private async Task<Snapshot> GetSnapshotAsync(CancellationToken ct)
        {
            cache.TryGetValue(CacheKey, out Snapshot? snapshot);
            if (snapshot is not null && snapshot.RefreshAfter > DateTimeOffset.UtcNow)
            {
                return snapshot;
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
                        snapshot = new Snapshot(DateTimeOffset.UtcNow.AddDays(1), Parse(document.RootElement), ParseTvdbScopes(document.RootElement));
                        cache.Set(CacheKey, snapshot, new MemoryCacheEntryOptions { Size = 100 });
                    }
                    catch (Exception exception) when (!ct.IsCancellationRequested
                        && exception is HttpRequestException or JsonException or IOException or TaskCanceledException or InvalidOperationException)
                    {
                        // An unavailable release never discards the previously verified mappings.
                        snapshot = snapshot is null
                            ? new Snapshot(DateTimeOffset.UtcNow.AddHours(1), new Dictionary<int, Link>(), new Dictionary<int, IReadOnlyList<TvdbScope>>())
                            : snapshot with { RefreshAfter = DateTimeOffset.UtcNow.AddHours(1) };
                        cache.Set(CacheKey, snapshot, new MemoryCacheEntryOptions { Size = 100 });
                    }
                }

                return snapshot;
            }
            finally
            {
                DownloadGate.Release();
            }
        }

        internal static SeasonMapping? AlignTvdb(JsonElement root, int tvdbId, IReadOnlyList<SeasonEpisodesData> seasons) =>
            AlignTvdb(ParseTvdbScopes(root), tvdbId, seasons);

        private static SeasonMapping? AlignTvdb(IReadOnlyDictionary<int, IReadOnlyList<TvdbScope>> indexes, int tvdbId, IReadOnlyList<SeasonEpisodesData> seasons)
        {
            if (!indexes.TryGetValue(tvdbId, out var scopes)) return null;

            var lengths = seasons.Where(season => season.SeasonNumber > 0 && season.Episodes > 0)
                .ToDictionary(season => season.SeasonNumber, season => season.Episodes);
            var ranges = new List<(int Season, int AniListId, int First, int Last, int TargetFirst, int TargetLast)>();
            var candidateIds = new HashSet<int>();
            var requiresReview = false;

            foreach (var scope in scopes)
            {
                if (!lengths.TryGetValue(scope.SeasonNumber, out var episodeCount)) continue;
                foreach (var target in scope.Targets.EnumerateObject())
                {
                    if (!target.Name.StartsWith("anilist:", StringComparison.Ordinal)
                        || !int.TryParse(target.Name.AsSpan("anilist:".Length), NumberStyles.None, CultureInfo.InvariantCulture, out var aniListId))
                    {
                        continue;
                    }

                    candidateIds.Add(aniListId);
                    if (target.Value.ValueKind != JsonValueKind.Object ||
                        target.Value.EnumerateObject().Any() && !target.Value.EnumerateObject().Any(range => range.Value.ValueKind == JsonValueKind.String))
                    {
                        requiresReview = true;
                        continue;
                    }

                    foreach (var episodeRange in target.Value.EnumerateObject())
                    {
                        if (episodeRange.Value.ValueKind != JsonValueKind.String
                            || !SourceRange(episodeRange.Name, episodeCount, out var first, out var last)
                            || !TargetRange(episodeRange.Value.GetString()!, last - first + 1, out var targetFirst, out var targetLast))
                        {
                            requiresReview = true;
                            continue;
                        }

                        ranges.Add((scope.SeasonNumber, aniListId, first, last, targetFirst, targetLast));
                    }
                }
            }

            if (candidateIds.Count == 0) return null;
            var entries = new List<(int Season, int AniListId, int Offset)>();
            foreach (var season in ranges.GroupBy(range => range.Season))
            {
                var nextSource = 1;
                foreach (var range in season.OrderBy(range => range.First))
                {
                    if (range.First != nextSource) requiresReview = true;
                    nextSource = range.Last + 1;
                }

                if (nextSource != lengths[season.Key] + 1) requiresReview = true;
                foreach (var mapping in season.GroupBy(range => range.AniListId))
                {
                    var ordered = mapping.OrderBy(range => range.First).ToArray();
                    var offset = ordered[0].First - 1;
                    var nextTarget = 1;
                    foreach (var range in ordered)
                    {
                        if (range.TargetFirst != nextTarget || range.First != range.TargetFirst + offset) requiresReview = true;
                        nextTarget = range.TargetLast + 1;
                    }

                    entries.Add((season.Key, mapping.Key, offset));
                }
            }

            if (entries.GroupBy(entry => entry.AniListId).Any(group => group.Count() != 1)) requiresReview = true;
            return new SeasonMapping(requiresReview || ranges.Count == 0, requiresReview ? [] : entries, [.. candidateIds.Order()]);
        }

        private static bool SourceRange(string value, int maximum, out int start, out int end)
        {
            start = 0;
            end = 0;
            var parts = value.Split('-');
            if (parts.Length is < 1 or > 2 || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out start)) return false;
            if (parts.Length == 1) end = start;
            else if (parts[1].Length == 0) end = maximum;
            else if (!int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out end)) return false;
            return start >= 1 && end >= start && end <= maximum;
        }

        private static bool TargetRange(string value, int expectedCount, out int start, out int end)
        {
            start = 0;
            end = 0;
            var parts = value.Split('-');
            if (parts.Length is < 1 or > 2 || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out start)) return false;
            if (parts.Length == 1) end = start;
            else if (parts[1].Length == 0) end = start + expectedCount - 1;
            else if (!int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out end)) return false;
            return start >= 1 && end >= start && end - start + 1 == expectedCount;
        }

        private static IReadOnlyDictionary<int, IReadOnlyList<TvdbScope>> ParseTvdbScopes(JsonElement root)
        {
            var scopes = new Dictionary<int, List<TvdbScope>>();
            foreach (var source in root.EnumerateObject())
            {
                var parts = source.Name.Split(':');
                if (parts.Length != 3 || parts[0] != "tvdb_show" || parts[2].Length < 2 || parts[2][0] != 's'
                    || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var tvdbId)
                    || !int.TryParse(parts[2].AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var seasonNumber)
                    || source.Value.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (!scopes.TryGetValue(tvdbId, out var list)) scopes[tvdbId] = list = [];
                list.Add(new TvdbScope(seasonNumber, source.Value.Clone()));
            }

            return scopes.ToDictionary(item => item.Key, item => (IReadOnlyList<TvdbScope>)item.Value);
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
                    if (target.Value.ValueKind != JsonValueKind.Object)
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
                    var seriesIds = tvdb.Select(value => value.Id).Distinct().ToArray();
                    var tvdbId = seriesIds.Length == 1 ? seriesIds[0] : (int?)null;
                    var firstSeason = tvdbId is null ? (int?)null : tvdb.Min(value => value.Season);
                    links[aniListId] = new Link(tvdbId, firstSeason, tmdb.Count == 1 ? tmdb.Single() : null, films.Count == 1 ? films.Single() : null);
                }
            }

            return links;
        }
    }
}