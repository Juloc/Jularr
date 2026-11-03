using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Jularr.Integrations.Data.Common;
using Jularr.Integrations.Data.Tmdb;

namespace Jularr.Integrations.Logic.Tmdb
{
    // TMDB API v3 reads; the base address is configured by the host.
    internal sealed class TmdbClient(HttpClient httpClient)
    {
        public async Task<IReadOnlyList<TmdbSearchResult>> SearchAsync(TmdbConnection connection, TmdbMediaKind kind, string query, int? year, CancellationToken ct, int page = 1)
        {
            var parameters = new List<KeyValuePair<string, string>>
            {
                new("query", query),
                new("include_adult", "false"),
                new("page", page.ToString(CultureInfo.InvariantCulture))
            };
            if (year is { } value)
            {
                parameters.Add(new(kind == TmdbMediaKind.Movie ? "year" : "first_air_date_year", value.ToString(CultureInfo.InvariantCulture)));
            }

            using var document = await ReadAsync(connection, kind == TmdbMediaKind.Movie ? "search/movie" : "search/tv", parameters, ct);
            return document.RootElement.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array
                ? results.EnumerateArray().Select(item => ParseResult(kind, item)).OfType<TmdbSearchResult>().Take(20).ToArray()
                : [];
        }

        // An external identity must map to exactly one title of the requested media type.
        public async Task<int?> FindExternalAsync(TmdbConnection connection, TmdbMediaKind kind, string @namespace, string externalId, CancellationToken ct)
        {
            var source = @namespace switch
            {
                "imdb" => "imdb_id",
                "tvdb" when kind == TmdbMediaKind.Series => "tvdb_id",
                _ => throw new ArgumentOutOfRangeException(nameof(@namespace))
            };

            using var document = await ReadAsync(connection, "find/" + Uri.EscapeDataString(externalId), [new("external_source", source)], ct);
            var key = kind == TmdbMediaKind.Movie ? "movie_results" : "tv_results";
            if (!document.RootElement.TryGetProperty(key, out var results) || results.ValueKind != JsonValueKind.Array)
            {
                throw new TmdbException(TmdbFailure.InvalidResponse, "TMDB did not provide external identity results.");
            }

            var ids = results.EnumerateArray().Select(item => Int(item, "id")).OfType<int>().Where(id => id > 0).Distinct().Take(2).ToArray();
            return ids.Length == 1 ? ids[0] : null;
        }

        public async Task<IReadOnlyList<TmdbSearchResult>> ReadFeedAsync(TmdbConnection connection, TmdbMediaKind kind, TmdbFeed feed, string? region, int page, CancellationToken ct)
        {
            var type = kind == TmdbMediaKind.Movie ? "movie" : "tv";
            var path = feed switch
            {
                TmdbFeed.Trending => "trending/" + type + "/week",
                TmdbFeed.Top => type + "/top_rated",
                TmdbFeed.New => "discover/" + type,
                TmdbFeed.Upcoming when kind == TmdbMediaKind.Movie => "movie/upcoming",
                _ => throw new ArgumentOutOfRangeException(nameof(feed))
            };

            // Regional lists only use a real region.
            var parameters = new List<KeyValuePair<string, string>> { new("page", Math.Clamp(page, 1, 500).ToString(CultureInfo.InvariantCulture)) };
            if (region is { Length: 2 } && feed == TmdbFeed.Upcoming)
            {
                parameters.Add(new("region", region));
            }

            // New means first release.
            if (feed == TmdbFeed.New)
            {
                var dateField = kind == TmdbMediaKind.Movie ? "primary_release_date" : "first_air_date";
                parameters.Add(new("sort_by", dateField + ".desc"));
                parameters.Add(new(dateField + ".lte", DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
                parameters.Add(new("include_adult", "false"));
                if (kind == TmdbMediaKind.Series) parameters.Add(new("include_null_first_air_dates", "false"));
            }

            using var document = await ReadAsync(connection, path, parameters, ct);
            return document.RootElement.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array
                ? results.EnumerateArray().Select(item => ParseResult(kind, item)).OfType<TmdbSearchResult>().ToArray()
                : [];
        }

        // A discover list narrowed by Discover's filters; a rating order counts only titles with enough votes.
        public async Task<IReadOnlyList<TmdbSearchResult>> DiscoverAsync(TmdbConnection connection, TmdbMediaKind kind, TmdbDiscoverQuery query, int page, CancellationToken ct)
        {
            var movie = kind == TmdbMediaKind.Movie;
            var dateField = movie ? "primary_release_date" : "first_air_date";
            var parameters = new List<KeyValuePair<string, string>>
            {
                new("page", Math.Clamp(page, 1, 500).ToString(CultureInfo.InvariantCulture)),
                new("include_adult", query.IncludesAdult ? "true" : "false"),
                new("sort_by", query.Sort switch
                {
                    TmdbDiscoverSort.Rating => "vote_average.desc",
                    TmdbDiscoverSort.Released => dateField + ".desc",
                    TmdbDiscoverSort.Title => movie ? "title.asc" : "name.asc",
                    _ => "popularity.desc"
                })
            };
            if (query.Sort == TmdbDiscoverSort.Rating) parameters.Add(new("vote_count.gte", "300"));
            if (query.Sort == TmdbDiscoverSort.Released && query.YearTo is null)
            {
                parameters.Add(new(dateField + ".lte", DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
            }
            if (query.GenreIds.Count > 0) parameters.Add(new("with_genres", string.Join(',', query.GenreIds)));
            if (query.YearFrom is { } from) parameters.Add(new(dateField + ".gte", from.ToString(CultureInfo.InvariantCulture) + "-01-01"));
            if (query.YearTo is { } to) parameters.Add(new(dateField + ".lte", to.ToString(CultureInfo.InvariantCulture) + "-12-31"));
            if (query.RuntimeFrom is { } shortest) parameters.Add(new("with_runtime.gte", shortest.ToString(CultureInfo.InvariantCulture)));
            if (query.RuntimeTo is { } longest) parameters.Add(new("with_runtime.lte", longest.ToString(CultureInfo.InvariantCulture)));
            if (query.Country is { Length: 2 } country) parameters.Add(new("with_origin_country", country));
            if (query.ProviderIds.Count > 0 && query.Region is { Length: 2 } region)
            {
                parameters.Add(new("with_watch_providers", string.Join('|', query.ProviderIds)));
                parameters.Add(new("watch_region", region));
                parameters.Add(new("with_watch_monetization_types", "flatrate"));
            }

            if (!movie && query.SeriesStatuses.Count > 0) parameters.Add(new("with_status", string.Join('|', query.SeriesStatuses)));
            using var document = await ReadAsync(connection, "discover/" + (movie ? "movie" : "tv"), parameters, ct);
            return document.RootElement.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array
                ? results.EnumerateArray().Select(item => ParseResult(kind, item)).OfType<TmdbSearchResult>().ToArray()
                : [];
        }

        // The flat-rate services TMDB lists for a region, most watched first.
        public async Task<IReadOnlyList<TmdbProvider>> ReadProvidersAsync(TmdbConnection connection, TmdbMediaKind kind, string region, CancellationToken ct)
        {
            using var document = await ReadAsync(connection, "watch/providers/" + (kind == TmdbMediaKind.Movie ? "movie" : "tv"), [new("watch_region", region)], ct);
            return [.. Items(document.RootElement, "results")
                .Select(item => (Id: Int(item, "provider_id"), Name: Text(item, "provider_name"),
                    Priority: item.TryGetProperty("display_priorities", out var priorities) && priorities.ValueKind == JsonValueKind.Object && priorities.TryGetProperty(region, out var place)
                        && place.TryGetInt32(out var rank) ? rank : int.MaxValue))
                .Where(item => item.Id is > 0 && item.Name is not null)
                .OrderBy(item => item.Priority)
                .Select(item => new TmdbProvider(item.Id!.Value, item.Name!))];
        }

        public async Task<IReadOnlyList<TmdbSearchResult>> ReadRecommendationsAsync(TmdbConnection connection, TmdbMediaKind kind, int id, CancellationToken ct)
        {
            using var document = await ReadAsync(connection, (kind == TmdbMediaKind.Movie ? "movie/" : "tv/") + id.ToString(CultureInfo.InvariantCulture) + "/recommendations", [], ct);
            return document.RootElement.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array
                ? results.EnumerateArray().Select(item => ParseResult(kind, item)).OfType<TmdbSearchResult>().Take(20).ToArray()
                : [];
        }

        public async Task<TmdbWork> ReadWorkAsync(TmdbConnection connection, TmdbMediaKind kind, int id, CancellationToken ct)
        {
            var path = (kind == TmdbMediaKind.Movie ? "movie/" : "tv/") + id.ToString(CultureInfo.InvariantCulture);
            using var document = await ReadAsync(connection, path,
                [new("append_to_response", "external_ids,credits,videos,translations"), new("include_video_language", connection.Language[..Math.Min(2, connection.Language.Length)] + ",en,null")], ct);
            var root = document.RootElement;
            var result = ParseResult(kind, root) ?? throw new TmdbException(TmdbFailure.InvalidResponse, "TMDB returned a work without a title.");
            var externalIds = root.TryGetProperty("external_ids", out var ids) ? ids : default;

            // Specials keep season 0; the acquisition decides whether to want them.
            var seasons = root.TryGetProperty("seasons", out var seasonList) && seasonList.ValueKind == JsonValueKind.Array
                ? seasonList.EnumerateArray()
                    .Where(season => Int(season, "season_number") is not null)
                    .Select(season => new TmdbSeason(Int(season, "season_number")!.Value, Text(season, "name"), Date(season, "air_date"), Int(season, "episode_count") ?? 0, Text(season, "poster_path")))
                    .ToArray()
                : [];
            var genres = root.TryGetProperty("genres", out var genreList) && genreList.ValueKind == JsonValueKind.Array
                ? genreList.EnumerateArray().Select(genre => Int(genre, "id")).OfType<int>().ToArray()
                : [];
            var countries = root.TryGetProperty("origin_country", out var countryList) && countryList.ValueKind == JsonValueKind.Array
                ? countryList.EnumerateArray().Where(country => country.ValueKind == JsonValueKind.String).Select(country => country.GetString()!).ToArray()
                : [];
            var collection = root.TryGetProperty("belongs_to_collection", out var series) && series.ValueKind == JsonValueKind.Object ? series : default;
            return new TmdbWork(kind, result.Id, result.Title, result.OriginalTitle, result.Year, result.Overview, result.PosterPath, result.BackdropPath,
                Text(externalIds, "imdb_id") ?? Text(root, "imdb_id"), Int(externalIds, "tvdb_id"), seasons, genres, Text(root, "original_language"), countries, Int(collection, "id"),
                Text(collection, "name"), Date(root, kind == TmdbMediaKind.Movie ? "release_date" : "first_air_date"), ParseDetails(kind, root))
            {
                Translations = [.. Items(root.TryGetProperty("translations", out var translations) ? translations : default, "translations").Select(translation =>
                    Text(translation, "iso_639_1") is { Length: 2 } language && translation.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object
                        ? new TmdbTranslation(language.ToLowerInvariant(), Text(translation, "iso_3166_1"), Text(data, kind == TmdbMediaKind.Movie ? "title" : "name"), Text(data, "overview"))
                        : null).OfType<TmdbTranslation>().Take(200)]
            };
        }

        // Key crew only: who directed and wrote a movie and its music; a series names its creators.
        private static readonly Dictionary<string, string> CrewJobs = new(StringComparer.Ordinal)
        {
            ["Director"] = ProviderPerson.Director, ["Screenplay"] = ProviderPerson.Writer, ["Writer"] = ProviderPerson.Writer, ["Novel"] = ProviderPerson.Writer,
            ["Original Music Composer"] = ProviderPerson.Composer
        };

        private static TmdbDetails ParseDetails(TmdbMediaKind kind, JsonElement root)
        {
            var genres = Items(root, "genres").Select(genre => Text(genre, "name")).OfType<string>().ToArray();
            var runtime = kind == TmdbMediaKind.Movie ? Int(root, "runtime") : null;
            var studios = Items(root, kind == TmdbMediaKind.Movie ? "production_companies" : "networks").Select(company => Text(company, "name")).OfType<string>().Take(3).ToArray();

            // An official YouTube trailer first; only an id of YouTube's shape is kept.
            var trailer = Items(root.TryGetProperty("videos", out var videos) ? videos : default, "results")
                .Where(video => Text(video, "site") == "YouTube" && Text(video, "type") == "Trailer" && IsYouTubeKey(Text(video, "key")))
                .OrderByDescending(video => video.TryGetProperty("official", out var official) && official.ValueKind == JsonValueKind.True)
                .Select(video => Text(video, "key")).FirstOrDefault();
            var credits = root.TryGetProperty("credits", out var creditList) ? creditList : default;
            var cast = Items(credits, "cast").Select(person => Person(person, ProviderPerson.Actor, Text(person, "character"))).OfType<ProviderPerson>().Take(15);
            var crew = Items(root, "created_by").Select(person => Person(person, ProviderPerson.Creator, null)).Concat(Crew(credits)).OfType<ProviderPerson>()
                .DistinctBy(person => (person.Id ?? person.Name, person.Role)).Take(10);
            return new TmdbDetails(genres, runtime is > 0 and <= 1440 ? runtime : null, studios, trailer, [.. cast, .. crew]);
        }

        private static IEnumerable<ProviderPerson?> Crew(JsonElement credits) =>
            Items(credits, "crew").Select(person => Text(person, "job") is { } job && CrewJobs.TryGetValue(job, out var role) ? Person(person, role, job) : null);

        private static ProviderPerson? Person(JsonElement person, string role, string? credit) =>
            Text(person, "name") is { } name ? new ProviderPerson(Int(person, "id") is int id and > 0 ? id.ToString(CultureInfo.InvariantCulture) : null, name, role,
                string.IsNullOrWhiteSpace(credit) ? null : credit, ProfileUrl(ImagePath(Text(person, "profile_path")))) : null;

        private static IEnumerable<JsonElement> Items(JsonElement element, string name) =>
            element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var list) && list.ValueKind == JsonValueKind.Array ? list.EnumerateArray() : [];

        private static bool IsYouTubeKey(string? value) => value is { Length: 11 } && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

        // TMDB's daily list of every film id with its popularity: public, gzip, one JSON object per line; published around 08:00 UTC.
        private static readonly Uri ExportHost = new("https://files.tmdb.org/p/exports/");

        public async IAsyncEnumerable<TmdbExportEntry> ReadMovieIdExportAsync(DateOnly day, [EnumeratorCancellation] CancellationToken ct)
        {
            var file = "movie_ids_" + day.ToString("MM_dd_yyyy", CultureInfo.InvariantCulture) + ".json.gz";
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(ExportHost, file));
            HttpResponseMessage response;
            try
            {
                response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            }
            catch (Exception exception) when (exception is HttpRequestException || exception is OperationCanceledException && !ct.IsCancellationRequested)
            {
                throw new TmdbException(TmdbFailure.Unavailable, "TMDB's id list could not be reached.", exception);
            }

            using (response)
            {
                if (response.StatusCode == HttpStatusCode.NotFound) throw new TmdbException(TmdbFailure.NotFound, "TMDB has not published this id list.");
                if (!response.IsSuccessStatusCode)
                {
                    throw new TmdbException(TmdbFailure.Unavailable, "TMDB's id list answered HTTP " + ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture) + ".");
                }

                await using var stream = await response.Content.ReadAsStreamAsync(ct);
                await using var unpacked = new GZipStream(stream, CompressionMode.Decompress);
                using var reader = new StreamReader(unpacked);
                while (await reader.ReadLineAsync(ct) is { } line)
                {
                    if (ParseExport(line) is { } entry) yield return entry;
                }
            }
        }

        // The film ids changed between two days (at most fourteen apart), one page of a hundred at a time.
        public async Task<(IReadOnlyList<int> Ids, int Pages)> ReadMovieChangesAsync(TmdbConnection connection, DateOnly from, DateOnly to, int page, CancellationToken ct)
        {
            using var document = await ReadAsync(connection, "movie/changes", [
                new("start_date", from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
                new("end_date", to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
                new("page", page.ToString(CultureInfo.InvariantCulture))], ct);
            var ids = document.RootElement.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array
                ? results.EnumerateArray().Select(item => Int(item, "id")).OfType<int>().Where(id => id > 0).ToArray()
                : [];
            return (ids, Int(document.RootElement, "total_pages") ?? 1);
        }

        // One title for the provider catalog, without the heavy details of an import; null when TMDB no longer has it.
        public async Task<TmdbCatalogEntry?> ReadCatalogEntryAsync(TmdbConnection connection, TmdbMediaKind kind, int id, CancellationToken ct)
        {
            var movie = kind == TmdbMediaKind.Movie;
            JsonDocument document;
            try
            {
                document = await ReadAsync(connection, (movie ? "movie/" : "tv/") + id.ToString(CultureInfo.InvariantCulture), [new("append_to_response", "external_ids,watch/providers")], ct);
            }
            catch (TmdbException exception) when (exception.Failure == TmdbFailure.NotFound)
            {
                return null;
            }

            using (document)
            {
                var item = document.RootElement;
                if (Text(item, movie ? "title" : "name") is not { } title) return null;
                var external = item.TryGetProperty("external_ids", out var ids) ? ids : default;
                var imdb = Text(item, "imdb_id") ?? (external.ValueKind == JsonValueKind.Object ? Text(external, "imdb_id") : null);
                return new TmdbCatalogEntry(id, title, Text(item, movie ? "original_title" : "original_name"), Date(item, movie ? "release_date" : "first_air_date"), Text(item, "overview"),
                    ImagePath(Text(item, "poster_path")), ImagePath(Text(item, "backdrop_path")), Number(item, "popularity"), Number(item, "vote_average"), Int(item, "vote_count"), imdb,
                    item.TryGetProperty("adult", out var adult) && adult.ValueKind == JsonValueKind.True)
                {
                    TvdbId = !movie && external.ValueKind == JsonValueKind.Object && Int(external, "tvdb_id") is > 0 and var tvdb ? tvdb : null,
                    GenreIds = [.. Items(item, "genres").Select(genre => Int(genre, "id")).OfType<int>()],
                    Runtime = movie ? Int(item, "runtime") : Items(item, "episode_run_time").Where(value => value.ValueKind == JsonValueKind.Number).Select(value => value.GetInt32()).FirstOrDefault() is > 0 and var each ? each : null,
                    Episodes = movie ? null : Int(item, "number_of_episodes"),
                    Status = Text(item, "status"),
                    Country = Items(item, "origin_country").Where(value => value.ValueKind == JsonValueKind.String).Select(value => value.GetString()).FirstOrDefault()
                        ?? Items(item, "production_countries").Select(country => Text(country, "iso_3166_1")).FirstOrDefault(),
                    ProviderIds = WatchRegion(connection) is { } region && item.TryGetProperty("watch/providers", out var providers) && providers.TryGetProperty("results", out var regions)
                        && regions.ValueKind == JsonValueKind.Object && regions.TryGetProperty(region, out var available)
                        ? [.. Items(available, "flatrate").Select(provider => Int(provider, "provider_id")).OfType<int>().Distinct()]
                        : []
                };
            }
        }

        private static TmdbExportEntry? ParseExport(string line)
        {
            if (line.Length == 0) return null;
            try
            {
                using var document = JsonDocument.Parse(line);
                var item = document.RootElement;
                return Int(item, "id") is { } id and > 0 && Text(item, "original_title") is { } title
                    ? new TmdbExportEntry(id, title, Number(item, "popularity") ?? 0, item.TryGetProperty("adult", out var adult) && adult.ValueKind == JsonValueKind.True,
                        item.TryGetProperty("video", out var video) && video.ValueKind == JsonValueKind.True)
                    : null;
            }
            catch (JsonException)
            {
                // A damaged line is skipped; the next day's list has it again.
                return null;
            }
        }

        private async Task<JsonDocument> ReadAsync(TmdbConnection connection, string path, IReadOnlyList<KeyValuePair<string, string>> parameters, CancellationToken ct)
        {
            // Read tokens are JWTs; anything else is a v3 key.
            var credential = connection.Credential.Trim();
            var bearer = credential.StartsWith("eyJ", StringComparison.Ordinal);
            var query = parameters.Append(new("language", connection.Language));
            if (!bearer)
            {
                query = query.Append(new("api_key", credential));
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, path + "?" + string.Join('&', query.Select(pair => Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value))));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            if (bearer)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
            }

            HttpResponseMessage response;
            try
            {
                response = await httpClient.SendAsync(request, ct);
            }
            catch (Exception exception) when (exception is HttpRequestException || exception is OperationCanceledException && !ct.IsCancellationRequested)
            {
                throw new TmdbException(TmdbFailure.Unavailable, "TMDB could not be reached.", exception);
            }

            using (response)
            {
                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    throw new TmdbException(TmdbFailure.AuthenticationFailed, "TMDB rejected the credential.");
                }

                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    throw new TmdbException(TmdbFailure.RateLimited, "TMDB limits requests right now.");
                }

                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    throw new TmdbException(TmdbFailure.NotFound, "TMDB does not know this work.");
                }

                if (!response.IsSuccessStatusCode)
                {
                    throw new TmdbException(TmdbFailure.Unavailable, "TMDB answered HTTP " + ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture) + ".");
                }

                try
                {
                    return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                }
                catch (JsonException exception)
                {
                    throw new TmdbException(TmdbFailure.InvalidResponse, "TMDB returned invalid JSON.", exception);
                }
            }
        }

        private static TmdbSearchResult? ParseResult(TmdbMediaKind kind, JsonElement item)
        {
            var movie = kind == TmdbMediaKind.Movie;
            if (Int(item, "id") is not { } id || Text(item, movie ? "title" : "name") is not { } title)
            {
                return null;
            }

            return new TmdbSearchResult(id, title, Text(item, movie ? "original_title" : "original_name"), Date(item, movie ? "release_date" : "first_air_date")?.Year,
                Text(item, "overview"), ImagePath(Text(item, "poster_path")), ImagePath(Text(item, "backdrop_path")), Number(item, "vote_average"));
        }

        // Posters and backdrops are served by TMDB's image host in fixed sizes.
        public static string? PosterUrl(string? path) => path is null ? null : "https://image.tmdb.org/t/p/w500" + path;

        public static string? BackdropUrl(string? path) => path is null ? null : "https://image.tmdb.org/t/p/w1280" + path;

        // Episode stills in card size.
        public static string? StillUrl(string? path) => path is null ? null : "https://image.tmdb.org/t/p/w400" + path;

        // Portraits of cast and crew.
        public static string? ProfileUrl(string? path) => path is null ? null : "https://image.tmdb.org/t/p/w185" + path;

        // Image paths are file names below TMDB's image host.
        private static string? ImagePath(string? path) =>
            path is { Length: > 1 and <= 200 } && path[0] == '/' && path[1..].All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-') ? path : null;

        private static string? Text(JsonElement element, string name) =>
            element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                && value.GetString()?.Trim() is { Length: > 0 } text ? text : null;

        // The region of the connection's language, such as DE of de-DE.
        public static string? Region(TmdbConnection connection) => connection.Language.Split('-') is [_, { Length: 2 } country] ? country.ToUpperInvariant() : null;

        // Streaming services are listed per country: the connection's own, else the language's usual one, such as DE for de.
        public static string? WatchRegion(TmdbConnection connection)
        {
            if (Region(connection) is { } region) return region;
            try
            {
                return new RegionInfo(CultureInfo.CreateSpecificCulture(connection.Language).Name).TwoLetterISORegionName is { Length: 2 } country ? country : null;
            }
            catch (Exception exception) when (exception is CultureNotFoundException or ArgumentException)
            {
                return null;
            }
        }

        private static int? Int(JsonElement element, string name) =>
            element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
                ? number : null;

        private static double? Number(JsonElement element, string name) =>
            element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number)
                && double.IsFinite(number) ? number : null;

        private static DateOnly? Date(JsonElement element, string name) =>
            DateOnly.TryParseExact(Text(element, name), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;
    }
}
