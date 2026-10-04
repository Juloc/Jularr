using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jularr.Web.Data;
using Jularr.Web.Features.MediaCore;
using Jularr.Web.Features.Providers;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Web.Features.Discovery;

public enum TmdbDiscoveryMediaType
{
    Movie,
    Series
}

public sealed record TmdbDiscoveryCandidate(
    TmdbDiscoveryMediaType MediaType,
    string ExternalId,
    string Title,
    string? OriginalTitle,
    string? Description,
    string? CoverImageUrl,
    int? Year,
    IReadOnlyList<int> GenreIds,
    double? Rating)
{
    public string Category => MediaType == TmdbDiscoveryMediaType.Movie ? "movie" : "tv";
    public string DetailsUrl => MediaType == TmdbDiscoveryMediaType.Movie
        ? $"https://www.themoviedb.org/movie/{ExternalId}"
        : $"https://www.themoviedb.org/tv/{ExternalId}";
}

/// <summary>
/// TMDB metadata/discovery adapter for Movie/Series (#809). Every remote call goes through the shared
/// provider executor and response cache. Browse/search candidates remain bounded runtime cache entries;
/// durable state is created only by <see cref="EnsureCanonicalWorkAsync"/>.
/// </summary>
public sealed class TmdbDiscoveryProvider(
    HttpClient client,
    IConfiguration configuration,
    ProviderExecutor executor,
    ProviderResponseCache cache,
    WorkService works,
    WorkStructureService structure,
    AppDbContext db,
    LegacyWorkBridge legacyBridge)
{
    public const string ProviderKey = ProviderKeys.Tmdb;
    private const int MaxMaterializedSeasons = 100;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static readonly IReadOnlyDictionary<string, int> MovieGenres =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["Action"] = 28,
            ["Adventure"] = 12,
            ["Comedy"] = 35,
            ["Drama"] = 18,
            ["Fantasy"] = 14,
            ["Horror"] = 27,
            ["Mystery"] = 9648,
            ["Romance"] = 10749,
            ["Sci-Fi"] = 878
        };

    private static readonly IReadOnlyDictionary<string, int> TvGenres =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["Action"] = 10759,
            ["Adventure"] = 10759,
            ["Comedy"] = 35,
            ["Drama"] = 18,
            ["Fantasy"] = 10765,
            ["Horror"] = 9648,
            ["Mystery"] = 9648,
            ["Romance"] = 18,
            ["Sci-Fi"] = 10765
        };

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(configuration["Providers:Tmdb:ReadAccessToken"])
        || !string.IsNullOrWhiteSpace(configuration["Providers:Tmdb:ApiKey"]);

    public static bool TryNormalizeExternalId(string? value, out string normalized)
    {
        normalized = "";
        return int.TryParse(value?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var id)
               && id > 0
               && (normalized = id.ToString(CultureInfo.InvariantCulture)).Length > 0;
    }

    public async Task<IReadOnlyList<TmdbDiscoveryCandidate>> SearchAsync(
        TmdbDiscoveryMediaType mediaType,
        string query,
        int limit,
        string genre,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        var locale = CurrentLocale();
        var path = mediaType == TmdbDiscoveryMediaType.Movie ? "search/movie" : "search/tv";
        var key = $"tmdb:search:{mediaType}:{locale}:{genre}:{query.Trim().ToLowerInvariant()}";
        var page = await cache.GetOrFetchAsync(
            key,
            TimeSpan.FromMinutes(2),
            ct => GetPageAsync(
                path,
                [
                    ("query", query.Trim()),
                    ("include_adult", "false"),
                    ("language", locale),
                    ("page", "1")
                ],
                mediaType,
                ct),
            cancellationToken);

        return FilterGenre(page.Results, mediaType, genre)
            .Take(Math.Clamp(limit, 1, 40))
            .ToArray();
    }

    public async Task<IReadOnlyList<TmdbDiscoveryCandidate>> BrowseAsync(
        TmdbDiscoveryMediaType mediaType,
        DiscoveryMode mode,
        int limit,
        string genre,
        CancellationToken cancellationToken)
    {
        var locale = CurrentLocale();
        var path = BrowsePath(mediaType, mode);
        var query = BrowseQuery(mediaType, mode, locale, genre);
        var key = $"tmdb:browse:{mediaType}:{mode}:{locale}:{genre}:{string.Join('&', query.Select(x => x.Value))}";
        var page = await cache.GetOrFetchAsync(
            key,
            TimeSpan.FromMinutes(10),
            ct => GetPageAsync(path, query, mediaType, ct),
            cancellationToken);

        return FilterGenre(page.Results, mediaType, genre)
            .Take(Math.Clamp(limit, 1, 40))
            .ToArray();
    }

    /// <summary>
    /// Promotes one provider candidate into the canonical media core. The TMDB identity is the only
    /// automatic match key; title similarity never creates/merges a Work. Repeated calls are idempotent.
    /// TV requests additionally materialize the provider's current season/episode structure.
    /// </summary>
    public async Task<Work> EnsureCanonicalWorkAsync(
        TmdbDiscoveryMediaType mediaType,
        string externalId,
        CancellationToken cancellationToken)
    {
        if (!TryNormalizeExternalId(externalId, out var canonicalExternalId)
            || !int.TryParse(canonicalExternalId, NumberStyles.None, CultureInfo.InvariantCulture, out var tmdbId))
        {
            throw new ArgumentException("A positive TMDB id is required.", nameof(externalId));
        }

        var locale = CurrentLocale();
        var details = await GetDetailsAsync(mediaType, tmdbId, locale, cancellationToken);
        var workType = mediaType == TmdbDiscoveryMediaType.Movie
            ? WorkMediaType.Movie
            : WorkMediaType.Series;
        var title = details.DisplayTitle(mediaType);
        var year = Year(details.ReleaseDate(mediaType));

        var legacyMovie = mediaType == TmdbDiscoveryMediaType.Movie
            ? await db.Movies
                .Where(x => x.TmdbId == canonicalExternalId)
                .Take(2)
                .ToListAsync(cancellationToken)
            : new List<Jularr.Web.Features.Movies.Movie>();
        var legacySeries = mediaType == TmdbDiscoveryMediaType.Series
            ? await db.TvSeries
                .Where(x => x.TmdbId == canonicalExternalId)
                .Take(2)
                .ToListAsync(cancellationToken)
            : new List<Jularr.Web.Features.Tv.TvSeries>();

        if (legacyMovie.Count > 1 || legacySeries.Count > 1)
        {
            throw new InvalidOperationException(
                $"TMDB {mediaType} id {canonicalExternalId} resolves to multiple legacy records and requires review.");
        }

        var existingWorkId = await db.WorkExternalIdentities
            .Where(x =>
                x.Provider == ProviderKey
                && x.MediaType == workType
                && x.ExternalId == canonicalExternalId)
            .Select(x => x.WorkId)
            .SingleOrDefaultAsync(cancellationToken);

        Work work;
        if (existingWorkId != Guid.Empty)
        {
            work = await db.Works.SingleAsync(x => x.Id == existingWorkId, cancellationToken);
            if (legacyMovie.SingleOrDefault() is { } movie)
            {
                await works.LinkSourceAsync(work.Id, WorkSourceKind.Movie, movie.Id, cancellationToken);
            }
            else if (legacySeries.SingleOrDefault() is { } series)
            {
                await works.LinkSourceAsync(work.Id, WorkSourceKind.Series, series.Id, cancellationToken);
            }
        }
        else if (legacyMovie.SingleOrDefault() is { } movie)
        {
            var workId = await legacyBridge.EnsureWorkForMovieAsync(movie, cancellationToken);
            work = await db.Works.SingleAsync(x => x.Id == workId, cancellationToken);
        }
        else if (legacySeries.SingleOrDefault() is { } series)
        {
            var workId = await legacyBridge.EnsureWorkForSeriesAsync(series, cancellationToken);
            work = await db.Works.SingleAsync(x => x.Id == workId, cancellationToken);
        }
        else
        {
            work = await works.EnsureWorkByExternalIdentityAsync(
                workType,
                ProviderKey,
                canonicalExternalId,
                title,
                year,
                cancellationToken);
        }

        await works.AddOrUpdateTitleAsync(
            work.Id,
            WorkTitleType.Primary,
            locale,
            title,
            ProviderKey,
            isPrimary: true,
            cancellationToken);

        var original = details.OriginalDisplayTitle(mediaType);
        if (!string.IsNullOrWhiteSpace(original)
            && !string.Equals(original, title, StringComparison.Ordinal))
        {
            await works.AddOrUpdateTitleAsync(
                work.Id,
                WorkTitleType.Original,
                details.OriginalLanguage ?? "und",
                original,
                ProviderKey,
                isPrimary: false,
                cancellationToken);
        }

        await works.SetFieldProvenanceAsync(
            work.Id,
            "title",
            ProviderKey,
            externalId,
            confidence: 1.0,
            isManualOverride: false,
            preferredProvider: ProviderKey,
            cancellationToken);
        if (year is not null)
        {
            await works.SetFieldProvenanceAsync(
                work.Id,
                "year",
                ProviderKey,
                externalId,
                confidence: 1.0,
                isManualOverride: false,
                preferredProvider: ProviderKey,
                cancellationToken);
        }

        if (!string.IsNullOrWhiteSpace(details.Overview))
        {
            await works.SetFieldProvenanceAsync(
                work.Id,
                "description",
                ProviderKey,
                externalId,
                confidence: 1.0,
                isManualOverride: false,
                preferredProvider: ProviderKey,
                cancellationToken);
        }

        if (!string.IsNullOrWhiteSpace(details.PosterPath))
        {
            await works.SetFieldProvenanceAsync(
                work.Id,
                "cover",
                ProviderKey,
                externalId,
                confidence: 1.0,
                isManualOverride: false,
                preferredProvider: ProviderKey,
                cancellationToken);
        }

        var imdbId = details.ImdbId ?? details.ExternalIds?.ImdbId;
        if (!string.IsNullOrWhiteSpace(imdbId))
        {
            await works.LinkExternalIdentityAsync(
                work.Id,
                workType,
                ProviderKeys.Imdb,
                imdbId,
                confidence: 1.0,
                evidence: "TMDB external_ids",
                isPrimary: true,
                isManualOverride: false,
                MappingReviewState.Confirmed,
                cancellationToken);
        }

        if (mediaType == TmdbDiscoveryMediaType.Series
            && details.ExternalIds?.TvdbId is int tvdbId
            && tvdbId > 0)
        {
            await works.LinkExternalIdentityAsync(
                work.Id,
                workType,
                ProviderKeys.Tvdb,
                tvdbId.ToString(CultureInfo.InvariantCulture),
                confidence: 1.0,
                evidence: "TMDB external_ids",
                isPrimary: true,
                isManualOverride: false,
                MappingReviewState.Confirmed,
                cancellationToken);
        }

        if (mediaType == TmdbDiscoveryMediaType.Series)
        {
            await MaterializeSeriesStructureAsync(work.Id, tmdbId, details, locale, cancellationToken);
        }

        return work;
    }

    private async Task MaterializeSeriesStructureAsync(
        Guid workId,
        int tmdbId,
        TmdbDetails details,
        string locale,
        CancellationToken cancellationToken)
    {
        foreach (var seasonSummary in (details.Seasons ?? [])
                     .Where(x => x.SeasonNumber >= 0)
                     .OrderBy(x => x.SeasonNumber)
                     .Take(MaxMaterializedSeasons))
        {
            var season = await structure.AddOrUpdateSeasonAsync(
                workId,
                seasonSummary.SeasonNumber,
                seasonSummary.Name,
                cancellationToken);

            var seasonDetails = await GetSeasonAsync(
                tmdbId,
                seasonSummary.SeasonNumber,
                locale,
                cancellationToken);

            foreach (var episode in seasonDetails.Episodes ?? [])
            {
                if (episode.EpisodeNumber <= 0)
                {
                    continue;
                }

                await structure.AddOrUpdateEpisodeAsync(
                    workId,
                    seasonSummary.SeasonNumber,
                    episode.EpisodeNumber,
                    absoluteNumber: null,
                    isSpecial: seasonSummary.SeasonNumber == 0,
                    episode.Name,
                    ParseDate(episode.AirDate),
                    season.Id,
                    cancellationToken);
            }
        }
    }

    private async Task<TmdbDetails> GetDetailsAsync(
        TmdbDiscoveryMediaType mediaType,
        int id,
        string locale,
        CancellationToken cancellationToken)
    {
        var typePath = mediaType == TmdbDiscoveryMediaType.Movie ? "movie" : "tv";
        var key = $"tmdb:details:{mediaType}:{id}:{locale}";
        return await cache.GetOrFetchAsync(
            key,
            TimeSpan.FromHours(6),
            ct => GetJsonAsync<TmdbDetails>(
                $"{typePath}/{id}",
                [
                    ("language", locale),
                    ("append_to_response", "external_ids")
                ],
                ct),
            cancellationToken);
    }

    private async Task<TmdbSeasonDetails> GetSeasonAsync(
        int seriesId,
        int seasonNumber,
        string locale,
        CancellationToken cancellationToken)
    {
        var key = $"tmdb:season:{seriesId}:{seasonNumber}:{locale}";
        return await cache.GetOrFetchAsync(
            key,
            TimeSpan.FromHours(6),
            ct => GetJsonAsync<TmdbSeasonDetails>(
                $"tv/{seriesId}/season/{seasonNumber}",
                [("language", locale)],
                ct),
            cancellationToken);
    }

    private async Task<TmdbPage> GetPageAsync(
        string path,
        IReadOnlyList<(string Key, string Value)> query,
        TmdbDiscoveryMediaType mediaType,
        CancellationToken cancellationToken)
    {
        var page = await GetJsonAsync<TmdbPageDto>(path, query, cancellationToken);
        return new TmdbPage(
            (page.Results ?? [])
                .Where(x => x.Id > 0)
                .Select(x => new TmdbDiscoveryCandidate(
                    mediaType,
                    x.Id.ToString(CultureInfo.InvariantCulture),
                    x.Title ?? x.Name ?? x.OriginalTitle ?? x.OriginalName ?? $"TMDB {x.Id}",
                    x.OriginalTitle ?? x.OriginalName,
                    x.Overview,
                    CoverUrl(x.PosterPath),
                    Year(x.ReleaseDate ?? x.FirstAirDate),
                    x.GenreIds ?? [],
                    x.VoteAverage))
                .ToArray());
    }

    private async Task<T> GetJsonAsync<T>(
        string path,
        IReadOnlyList<(string Key, string Value)> query,
        CancellationToken cancellationToken)
    {
        EnsureConfigured();
        using var response = await executor.SendAsync(
            ProviderKey,
            client,
            () =>
            {
                var request = new HttpRequestMessage(HttpMethod.Get, BuildUri(path, query));
                var token = configuration["Providers:Tmdb:ReadAccessToken"];
                if (!string.IsNullOrWhiteSpace(token))
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Trim());
                }

                return request;
            },
            cancellationToken: cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken)
            ?? throw new InvalidDataException("TMDB returned an empty JSON response.");
    }

    private string BuildUri(string path, IReadOnlyList<(string Key, string Value)> query)
    {
        var values = new List<(string Key, string Value)>(query);
        var apiKey = configuration["Providers:Tmdb:ApiKey"];
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            values.Add(("api_key", apiKey.Trim()));
        }

        var encoded = string.Join(
            '&',
            values
                .Where(x => !string.IsNullOrWhiteSpace(x.Value))
                .Select(x => $"{Uri.EscapeDataString(x.Key)}={Uri.EscapeDataString(x.Value)}"));
        return encoded.Length == 0 ? path : $"{path}?{encoded}";
    }

    private void EnsureConfigured()
    {
        if (!IsConfigured)
        {
            throw new InvalidOperationException(
                "TMDB is not configured. Set Providers:Tmdb:ReadAccessToken or Providers:Tmdb:ApiKey.");
        }
    }

    private static string BrowsePath(TmdbDiscoveryMediaType mediaType, DiscoveryMode mode)
    {
        var type = mediaType == TmdbDiscoveryMediaType.Movie ? "movie" : "tv";
        return mode switch
        {
            DiscoveryMode.Top => $"{type}/popular",
            DiscoveryMode.New when mediaType == TmdbDiscoveryMediaType.Movie => "movie/now_playing",
            DiscoveryMode.New => "discover/tv",
            DiscoveryMode.Upcoming when mediaType == TmdbDiscoveryMediaType.Movie => "movie/upcoming",
            DiscoveryMode.Upcoming => "discover/tv",
            _ => $"trending/{type}/day"
        };
    }

    private static IReadOnlyList<(string Key, string Value)> BrowseQuery(
        TmdbDiscoveryMediaType mediaType,
        DiscoveryMode mode,
        string locale,
        string genre)
    {
        var query = new List<(string Key, string Value)>
        {
            ("language", locale),
            ("page", "1")
        };

        if (mediaType == TmdbDiscoveryMediaType.Series && mode is DiscoveryMode.New or DiscoveryMode.Upcoming)
        {
            var today = DateTime.UtcNow.Date;
            if (mode == DiscoveryMode.New)
            {
                query.Add(("first_air_date.gte", today.AddDays(-30).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
                query.Add(("first_air_date.lte", today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
                query.Add(("sort_by", "first_air_date.desc"));
            }
            else
            {
                query.Add(("first_air_date.gte", today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
                query.Add(("sort_by", "first_air_date.asc"));
                query.Add(("include_null_first_air_dates", "false"));
            }
        }

        if (GenreId(mediaType, genre) is { } genreId)
        {
            query.Add(("with_genres", genreId.ToString(CultureInfo.InvariantCulture)));
        }

        return query;
    }

    private static int? GenreId(TmdbDiscoveryMediaType mediaType, string genre)
    {
        if (string.IsNullOrWhiteSpace(genre))
        {
            return null;
        }

        var map = mediaType == TmdbDiscoveryMediaType.Movie ? MovieGenres : TvGenres;
        return map.TryGetValue(genre.Trim(), out var id) ? id : null;
    }

    private static IEnumerable<TmdbDiscoveryCandidate> FilterGenre(
        IEnumerable<TmdbDiscoveryCandidate> rows,
        TmdbDiscoveryMediaType mediaType,
        string genre)
    {
        if (string.IsNullOrWhiteSpace(genre))
        {
            return rows;
        }

        var map = mediaType == TmdbDiscoveryMediaType.Movie ? MovieGenres : TvGenres;
        return map.TryGetValue(genre.Trim(), out var id)
            ? rows.Where(x => x.GenreIds.Contains(id))
            : [];
    }

    private static string CurrentLocale()
    {
        var locale = CultureInfo.CurrentUICulture.Name;
        return string.IsNullOrWhiteSpace(locale) ? "en-US" : locale;
    }

    private static string? CoverUrl(string? path) =>
        string.IsNullOrWhiteSpace(path) ? null : $"https://image.tmdb.org/t/p/w500{path}";

    private static int? Year(string? value) =>
        DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date.Year
            : null;

    private static DateTime? ParseDate(string? value) =>
        DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date)
            ? DateTime.SpecifyKind(date.Date, DateTimeKind.Utc)
            : null;

    private sealed record TmdbPage(IReadOnlyList<TmdbDiscoveryCandidate> Results);

    private sealed class TmdbPageDto
    {
        [JsonPropertyName("results")]
        public List<TmdbListItem>? Results { get; set; }
    }

    private sealed class TmdbListItem
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("original_title")]
        public string? OriginalTitle { get; set; }

        [JsonPropertyName("original_name")]
        public string? OriginalName { get; set; }

        [JsonPropertyName("overview")]
        public string? Overview { get; set; }

        [JsonPropertyName("poster_path")]
        public string? PosterPath { get; set; }

        [JsonPropertyName("release_date")]
        public string? ReleaseDate { get; set; }

        [JsonPropertyName("first_air_date")]
        public string? FirstAirDate { get; set; }

        [JsonPropertyName("genre_ids")]
        public List<int>? GenreIds { get; set; }

        [JsonPropertyName("vote_average")]
        public double? VoteAverage { get; set; }
    }

    private sealed class TmdbDetails
    {
        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("original_title")]
        public string? OriginalTitle { get; set; }

        [JsonPropertyName("original_name")]
        public string? OriginalName { get; set; }

        [JsonPropertyName("original_language")]
        public string? OriginalLanguage { get; set; }

        [JsonPropertyName("overview")]
        public string? Overview { get; set; }

        [JsonPropertyName("poster_path")]
        public string? PosterPath { get; set; }

        [JsonPropertyName("release_date")]
        public string? ReleaseDateValue { get; set; }

        [JsonPropertyName("first_air_date")]
        public string? FirstAirDate { get; set; }

        [JsonPropertyName("imdb_id")]
        public string? ImdbId { get; set; }

        [JsonPropertyName("external_ids")]
        public TmdbExternalIds? ExternalIds { get; set; }

        [JsonPropertyName("seasons")]
        public List<TmdbSeasonSummary>? Seasons { get; set; }

        public string DisplayTitle(TmdbDiscoveryMediaType mediaType) =>
            (mediaType == TmdbDiscoveryMediaType.Movie ? Title : Name)
            ?? OriginalDisplayTitle(mediaType)
            ?? "Untitled";

        public string? OriginalDisplayTitle(TmdbDiscoveryMediaType mediaType) =>
            mediaType == TmdbDiscoveryMediaType.Movie ? OriginalTitle : OriginalName;

        public string? ReleaseDate(TmdbDiscoveryMediaType mediaType) =>
            mediaType == TmdbDiscoveryMediaType.Movie ? ReleaseDateValue : FirstAirDate;
    }

    private sealed class TmdbExternalIds
    {
        [JsonPropertyName("imdb_id")]
        public string? ImdbId { get; set; }

        [JsonPropertyName("tvdb_id")]
        public int? TvdbId { get; set; }
    }

    private sealed class TmdbSeasonSummary
    {
        [JsonPropertyName("season_number")]
        public int SeasonNumber { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }
    }

    private sealed class TmdbSeasonDetails
    {
        [JsonPropertyName("episodes")]
        public List<TmdbEpisode>? Episodes { get; set; }
    }

    private sealed class TmdbEpisode
    {
        [JsonPropertyName("episode_number")]
        public int EpisodeNumber { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("air_date")]
        public string? AirDate { get; set; }
    }
}
