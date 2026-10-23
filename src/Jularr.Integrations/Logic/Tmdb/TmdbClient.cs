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

        // TMDB's daily list of every id with its popularity: public, gzip, one JSON object per line; published around 08:00 UTC.
        private static readonly Uri ExportHost = new("https://files.tmdb.org/p/exports/");

        public async IAsyncEnumerable<TmdbExportEntry> ReadIdExportAsync(TmdbMediaKind kind, DateOnly day, [EnumeratorCancellation] CancellationToken ct)
        {
            var file = (kind == TmdbMediaKind.Movie ? "movie" : "tv_series") + "_ids_" + day.ToString("MM_dd_yyyy", CultureInfo.InvariantCulture) + ".json.gz";
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
                    if (ParseExport(kind, line) is { } entry) yield return entry;
                }
            }
        }

        // The ids changed between two days (at most fourteen apart), one page of a hundred at a time.
        public async Task<(IReadOnlyList<int> Ids, int Pages)> ReadChangesAsync(TmdbConnection connection, TmdbMediaKind kind, DateOnly from, DateOnly to, int page, CancellationToken ct)
        {
            using var document = await ReadAsync(connection, (kind == TmdbMediaKind.Movie ? "movie" : "tv") + "/changes", [
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
                document = await ReadAsync(connection, (movie ? "movie/" : "tv/") + id.ToString(CultureInfo.InvariantCulture), [new("append_to_response", "external_ids")], ct);
            }
            catch (TmdbException exception) when (exception.Failure == TmdbFailure.NotFound)
            {
                return null;
            }

            using (document)
            {
                var item = document.RootElement;
                if (Text(item, movie ? "title" : "name") is not { } title) return null;
                var imdb = Text(item, "imdb_id") ?? (item.TryGetProperty("external_ids", out var external) ? Text(external, "imdb_id") : null);
                return new TmdbCatalogEntry(id, title, Text(item, movie ? "original_title" : "original_name"), Date(item, movie ? "release_date" : "first_air_date"), Text(item, "overview"),
                    ImagePath(Text(item, "poster_path")), ImagePath(Text(item, "backdrop_path")), Number(item, "popularity"), Number(item, "vote_average"), Int(item, "vote_count"), imdb,
                    item.TryGetProperty("adult", out var adult) && adult.ValueKind == JsonValueKind.True);
            }
        }

        private static TmdbExportEntry? ParseExport(TmdbMediaKind kind, string line)
        {
            if (line.Length == 0) return null;
            try
            {
                using var document = JsonDocument.Parse(line);
                var item = document.RootElement;
                return Int(item, "id") is { } id and > 0 && Text(item, kind == TmdbMediaKind.Movie ? "original_title" : "original_name") is { } title
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
