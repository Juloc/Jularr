using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jularr.Web.Data;
using Jularr.Web.Features.MediaCore;
using Jularr.Web.Features.Metadata;
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
public sealed partial class TmdbDiscoveryProvider(
    HttpClient client,
    IConfiguration configuration,
    ProviderExecutor executor,
    ProviderResponseCache cache,
    WorkService works,
    WorkStructureService structure,
    AppDbContext db,
    LegacyWorkBridge legacyBridge,
    WorkMetadataRefreshQueue metadataRefresh)
{
    public const string ProviderKey = ProviderKeys.Tmdb;

    /// <summary>The only host durable artwork is downloaded from.</summary>
    public const string ImageHost = "image.tmdb.org";

    private const int MaxMaterializedSeasons = 100;
    private const int MaxCast = 20;
    private const int MaxCrew = 10;
    private const int MaxTrailers = 3;

    private static readonly string[] KeyCrewJobs = ["Director", "Screenplay", "Writer", "Story", "Novel", "Creator"];

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

        // The Work is durable from here on: its synopsis, artwork and facts are persisted by the background metadata spool, never by
        // this request.
        await metadataRefresh.RequestMetadataRefreshAsync(work.Id, interactive: false, cancellationToken);
        return work;
    }

    /// <summary>
    /// The durable metadata of one TMDB title in one locale: details, images, credits, trailers and the regional age rating in a single
    /// provider call, normalized to <see cref="WorkMetadataSnapshot"/>. Deliberately not served from the response cache: the caller is
    /// the background spool, whose result is persisted. Provider failures propagate unchanged so the spool can classify them.
    /// </summary>
    public async Task<WorkMetadataSnapshot> GetWorkMetadataAsync(TmdbDiscoveryMediaType mediaType, string externalId, string locale, CancellationToken cancellationToken)
    {
        if (!TryNormalizeExternalId(externalId, out var tmdbId))
        {
            throw new ArgumentException("A positive TMDB id is required.", nameof(externalId));
        }

        var language = WorkMetadataLocales.BaseLanguage(locale);
        var imageLanguages = string.Join(',', new[] { language, WorkMetadataLocales.English, "null" }.Distinct(StringComparer.Ordinal));
        var movie = mediaType == TmdbDiscoveryMediaType.Movie;
        var details = await GetJsonAsync<TmdbMetadataDetails>(
            $"{(movie ? "movie" : "tv")}/{tmdbId}",
            [
                ("language", locale),
                ("append_to_response", movie ? "images,credits,videos,release_dates" : "images,credits,videos,content_ratings"),
                ("include_image_language", imageLanguages),
                ("include_video_language", language)
            ],
            cancellationToken);

        var country = WorkMetadataLocales.CertificationCountry(locale);
        var certification = movie
            ? details.ReleaseDates?.Results?.FirstOrDefault(x => x.Country == country)?.ReleaseDates?.Select(x => x.Certification?.Trim()).FirstOrDefault(x => !string.IsNullOrEmpty(x))
            : details.ContentRatings?.Results?.FirstOrDefault(x => x.Country == country)?.Rating?.Trim();
        var runtime = movie
            ? details.Runtime
            : details.EpisodeRunTime?.FirstOrDefault(x => x > 0) is int typical and > 0 ? typical : details.LastEpisodeToAir?.Runtime;
        var studios = (details.ProductionCompanies ?? []).Select(x => x.Name).Concat(movie ? [] : (details.Networks ?? []).Select(x => x.Name));
        var countries = (details.ProductionCountries ?? []).Select(x => x.Country).Concat(movie ? [] : details.OriginCountry ?? []);

        return new WorkMetadataSnapshot(
            ProviderKey,
            tmdbId,
            locale,
            Trimmed(movie ? details.Title : details.Name),
            Trimmed(details.Overview),
            Trimmed(details.Tagline),
            DistinctTexts((details.Genres ?? []).Select(x => x.Name), 10),
            TrailerKeys(details.Videos?.Results),
            Trimmed(movie ? details.OriginalTitle : details.OriginalName, 1000),
            IsLanguageCode(details.OriginalLanguage) ? details.OriginalLanguage : null,
            ParseDateOnly(movie ? details.ReleaseDate : details.FirstAirDate),
            runtime is > 0 ? runtime : null,
            details.VoteCount is > 0 && details.VoteAverage is >= 0 and <= 10 ? Math.Round(details.VoteAverage.Value, 1) : null,
            details.VoteCount is >= 0 ? details.VoteCount : null,
            string.IsNullOrEmpty(certification) || country is null ? null : Trimmed(certification, 32),
            string.IsNullOrEmpty(certification) ? null : country,
            DistinctTexts(studios, 10),
            DistinctTexts(countries.Where(x => x is { Length: 2 } && x.All(char.IsAsciiLetterUpper)), 10),
            Credits(details, movie),
            Artwork(details.Images));
    }

    /// <summary>
    /// The TMDB image CDN address of a provider file path at a fixed rendition. The path is untrusted provider data: only a single
    /// segment of letters, digits, <c>_</c> and <c>-</c> with a raster extension is accepted, so the result is always on
    /// <see cref="ImageHost"/> and can never traverse or redirect the request elsewhere.
    /// </summary>
    public static bool TryBuildImageUri(string? filePath, string size, out Uri uri)
    {
        uri = null!;
        if (filePath is null || !ImageFilePath().IsMatch(filePath) || !ImageSize().IsMatch(size))
        {
            return false;
        }

        uri = new Uri($"https://{ImageHost}/t/p/{size}{filePath}", UriKind.Absolute);
        return true;
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"^/[A-Za-z0-9_-]{1,120}\.(jpg|jpeg|png|webp)\z")]
    private static partial System.Text.RegularExpressions.Regex ImageFilePath();

    [System.Text.RegularExpressions.GeneratedRegex(@"^(w[0-9]{2,4}|original)\z")]
    private static partial System.Text.RegularExpressions.Regex ImageSize();

    [System.Text.RegularExpressions.GeneratedRegex(@"^[A-Za-z0-9_-]{6,32}\z")]
    private static partial System.Text.RegularExpressions.Regex YouTubeKey();

    private static IReadOnlyList<WorkCreditCandidate> Credits(TmdbMetadataDetails details, bool movie)
    {
        var cast = (details.Credits?.Cast ?? [])
            .Where(x => !string.IsNullOrWhiteSpace(x.Name))
            .OrderBy(x => x.Order)
            .Take(MaxCast)
            .Select(x => new WorkCreditCandidate(WorkCreditKind.Cast, Trimmed(x.Name, 300)!, Trimmed(x.Character, 300), PersonId(x.Id)));
        var creators = movie ? [] : (details.CreatedBy ?? []).Where(x => !string.IsNullOrWhiteSpace(x.Name)).Select(x => new WorkCreditCandidate(WorkCreditKind.Crew, Trimmed(x.Name, 300)!, "Creator", PersonId(x.Id)));
        var crew = (details.Credits?.Crew ?? [])
            .Where(x => !string.IsNullOrWhiteSpace(x.Name) && KeyCrewJobs.Contains(x.Job, StringComparer.Ordinal))
            .OrderBy(x => Array.IndexOf(KeyCrewJobs, x.Job))
            .Select(x => new WorkCreditCandidate(WorkCreditKind.Crew, Trimmed(x.Name, 300)!, x.Job, PersonId(x.Id)));
        return [.. cast, .. creators.Concat(crew).DistinctBy(x => (x.Name, x.Role)).Take(MaxCrew)];
    }

    private static string? PersonId(int id) => id > 0 ? id.ToString(CultureInfo.InvariantCulture) : null;

    private static IReadOnlyList<string> TrailerKeys(IEnumerable<TmdbVideo>? videos) =>
    [
        .. (videos ?? [])
            .Where(x => x.Site == "YouTube" && x.Type == "Trailer" && x.Key is not null && YouTubeKey().IsMatch(x.Key))
            .OrderByDescending(x => x.Official)
            .ThenBy(x => x.PublishedAt ?? "", StringComparer.Ordinal)
            .Select(x => x.Key!)
            .Distinct(StringComparer.Ordinal)
            .Take(MaxTrailers)
    ];

    private static IReadOnlyList<WorkArtworkCandidate> Artwork(TmdbImages? images)
    {
        var result = new List<WorkArtworkCandidate>();
        Add(WorkArtworkSlot.Poster, "w780", images?.Posters);
        Add(WorkArtworkSlot.Backdrop, "w1280", images?.Backdrops);
        Add(WorkArtworkSlot.Logo, "w500", images?.Logos);
        return result;

        void Add(WorkArtworkSlot slot, string size, IEnumerable<TmdbImage>? source)
        {
            foreach (var image in source ?? [])
            {
                // TMDB marks textless artwork with no language or "xx"; anything else that is not a two-letter code is unusable.
                var language = image.Language is null or "xx" ? "" : image.Language;
                if ((language.Length == 0 || IsLanguageCode(language)) && TryBuildImageUri(image.FilePath, size, out var uri))
                {
                    result.Add(new WorkArtworkCandidate(slot, language, image.FilePath!, uri, image.Width is > 0 ? image.Width : null, image.Height is > 0 ? image.Height : null, image.VoteAverage, image.VoteCount));
                }
            }
        }
    }

    private static IReadOnlyList<string> DistinctTexts(IEnumerable<string?> values, int limit) =>
        [.. values.Select(value => Trimmed(value, 300)).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).Take(limit)];

    private static bool IsLanguageCode(string? value) => value is { Length: 2 } && value.All(char.IsAsciiLetterLower);

    // Provider text is untrusted in length too: clip it to the column it lands in, so one oversized value cannot fail the whole refresh.
    private static string? Trimmed(string? value, int maxLength = 4000)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength].TrimEnd();
    }

    private static DateOnly? ParseDateOnly(string? value) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;

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

        // The executor reads only the headers, where HttpClient.Timeout ends; the body gets the same budget, so a stalled answer fails
        // as a transient network error instead of hanging its caller.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(client.Timeout);
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, timeout.Token)
                ?? throw new InvalidDataException("TMDB returned an empty JSON response.");
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new HttpRequestException($"The TMDB response did not finish within {client.Timeout.TotalSeconds:0} s.", new TimeoutException(exception.Message, exception));
        }
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

    private sealed class TmdbMetadataDetails
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

        [JsonPropertyName("tagline")]
        public string? Tagline { get; set; }

        [JsonPropertyName("release_date")]
        public string? ReleaseDate { get; set; }

        [JsonPropertyName("first_air_date")]
        public string? FirstAirDate { get; set; }

        [JsonPropertyName("runtime")]
        public int? Runtime { get; set; }

        [JsonPropertyName("episode_run_time")]
        public List<int>? EpisodeRunTime { get; set; }

        [JsonPropertyName("last_episode_to_air")]
        public TmdbEpisodeRuntime? LastEpisodeToAir { get; set; }

        [JsonPropertyName("vote_average")]
        public double? VoteAverage { get; set; }

        [JsonPropertyName("vote_count")]
        public int? VoteCount { get; set; }

        [JsonPropertyName("genres")]
        public List<TmdbNamed>? Genres { get; set; }

        [JsonPropertyName("production_companies")]
        public List<TmdbNamed>? ProductionCompanies { get; set; }

        [JsonPropertyName("networks")]
        public List<TmdbNamed>? Networks { get; set; }

        [JsonPropertyName("production_countries")]
        public List<TmdbCountry>? ProductionCountries { get; set; }

        [JsonPropertyName("origin_country")]
        public List<string>? OriginCountry { get; set; }

        [JsonPropertyName("created_by")]
        public List<TmdbPerson>? CreatedBy { get; set; }

        [JsonPropertyName("credits")]
        public TmdbCredits? Credits { get; set; }

        [JsonPropertyName("videos")]
        public TmdbResults<TmdbVideo>? Videos { get; set; }

        [JsonPropertyName("images")]
        public TmdbImages? Images { get; set; }

        [JsonPropertyName("release_dates")]
        public TmdbResults<TmdbCountryReleases>? ReleaseDates { get; set; }

        [JsonPropertyName("content_ratings")]
        public TmdbResults<TmdbContentRating>? ContentRatings { get; set; }
    }

    private sealed class TmdbEpisodeRuntime
    {
        [JsonPropertyName("runtime")]
        public int? Runtime { get; set; }
    }

    private sealed class TmdbNamed
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }
    }

    private sealed class TmdbCountry
    {
        [JsonPropertyName("iso_3166_1")]
        public string? Country { get; set; }
    }

    private sealed class TmdbPerson
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("character")]
        public string? Character { get; set; }

        [JsonPropertyName("job")]
        public string? Job { get; set; }

        [JsonPropertyName("order")]
        public int Order { get; set; }
    }

    private sealed class TmdbCredits
    {
        [JsonPropertyName("cast")]
        public List<TmdbPerson>? Cast { get; set; }

        [JsonPropertyName("crew")]
        public List<TmdbPerson>? Crew { get; set; }
    }

    private sealed class TmdbResults<T>
    {
        [JsonPropertyName("results")]
        public List<T>? Results { get; set; }
    }

    private sealed class TmdbVideo
    {
        [JsonPropertyName("key")]
        public string? Key { get; set; }

        [JsonPropertyName("site")]
        public string? Site { get; set; }

        [JsonPropertyName("type")]
        public string? Type { get; set; }

        [JsonPropertyName("official")]
        public bool Official { get; set; }

        [JsonPropertyName("published_at")]
        public string? PublishedAt { get; set; }
    }

    private sealed class TmdbImages
    {
        [JsonPropertyName("posters")]
        public List<TmdbImage>? Posters { get; set; }

        [JsonPropertyName("backdrops")]
        public List<TmdbImage>? Backdrops { get; set; }

        [JsonPropertyName("logos")]
        public List<TmdbImage>? Logos { get; set; }
    }

    private sealed class TmdbImage
    {
        [JsonPropertyName("file_path")]
        public string? FilePath { get; set; }

        [JsonPropertyName("iso_639_1")]
        public string? Language { get; set; }

        [JsonPropertyName("width")]
        public int? Width { get; set; }

        [JsonPropertyName("height")]
        public int? Height { get; set; }

        [JsonPropertyName("vote_average")]
        public double? VoteAverage { get; set; }

        [JsonPropertyName("vote_count")]
        public int? VoteCount { get; set; }
    }

    private sealed class TmdbCountryReleases
    {
        [JsonPropertyName("iso_3166_1")]
        public string? Country { get; set; }

        [JsonPropertyName("release_dates")]
        public List<TmdbRelease>? ReleaseDates { get; set; }
    }

    private sealed class TmdbRelease
    {
        [JsonPropertyName("certification")]
        public string? Certification { get; set; }
    }

    private sealed class TmdbContentRating
    {
        [JsonPropertyName("iso_3166_1")]
        public string? Country { get; set; }

        [JsonPropertyName("rating")]
        public string? Rating { get; set; }
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
