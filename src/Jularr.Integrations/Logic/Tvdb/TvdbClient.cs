using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Jularr.Integrations.Data.Tvdb;

namespace Jularr.Integrations.Logic.Tvdb
{
    // Read-only TVDB API v4 access. No Work, unit or progress writes occur here.
    internal sealed class TvdbClient(HttpClient httpClient)
    {
        public async Task<TvdbSession> LoginAsync(string apiKey, string? pin, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                throw new ArgumentException("A project API key is required.", nameof(apiKey));
            }

            using var document = await RequestAsync(HttpMethod.Post, "login", null, pin is null ? new { apikey = apiKey } : (object)new { apikey = apiKey, pin }, ct);
            var token = Text(Data(document.RootElement), "token");
            return token is not null ? new TvdbSession(token) : throw new TvdbException(TvdbFailure.InvalidResponse, "TVDB returned no session token.");
        }

        // Remote identity may refer to films as well; only one distinct series is safe.
        public async Task<int?> FindSeriesByImdbAsync(TvdbSession session, string imdbId, CancellationToken ct)
        {
            if (imdbId.Length < 9 || !imdbId.StartsWith("tt", StringComparison.Ordinal) || !imdbId.AsSpan(2).ToString().All(char.IsAsciiDigit))
            {
                throw new ArgumentException("A valid IMDb title identifier is required.", nameof(imdbId));
            }

            using var document = await RequestAsync(HttpMethod.Get, "search/remoteid/" + Uri.EscapeDataString(imdbId), session.Token, null, ct);
            var data = Data(document.RootElement);
            var candidates = data.ValueKind switch
            {
                JsonValueKind.Array => data.EnumerateArray().ToArray(),
                JsonValueKind.Object => [data],
                _ => throw new TvdbException(TvdbFailure.InvalidResponse, "TVDB returned an invalid remote identity response.")
            };
            var ids = candidates.Select(item => Int(Child(item, "series"), "id")).OfType<int>().Where(id => id > 0).Distinct().Take(2).ToArray();
            return ids.Length switch
            {
                0 => null,
                1 => ids[0],
                _ => throw new TvdbException(TvdbFailure.Ambiguous, "IMDb identity matches multiple TVDB series.")
            };
        }

        // Series only, a year narrows the search; a page of results starts at offset.
        public async Task<IReadOnlyList<TvdbSearchResult>> SearchSeriesAsync(TvdbSession session, string query, int? year, int offset, int limit, CancellationToken ct)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(query);
            ArgumentOutOfRangeException.ThrowIfNegative(offset);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
            var path = "search?query=" + Uri.EscapeDataString(query) + "&type=series" + (year is { } value ? "&year=" + value.ToString(CultureInfo.InvariantCulture) : "")
                + "&offset=" + offset.ToString(CultureInfo.InvariantCulture) + "&limit=" + limit.ToString(CultureInfo.InvariantCulture);
            using var document = await RequestAsync(HttpMethod.Get, path, session.Token, null, ct);
            var data = Data(document.RootElement);
            if (data.ValueKind != JsonValueKind.Array)
            {
                throw new TvdbException(TvdbFailure.InvalidResponse, "TVDB returned no search results.");
            }

            static IReadOnlyDictionary<string, string> Texts(JsonElement parent, string property) =>
                Child(parent, property) is { ValueKind: JsonValueKind.Object } texts
                    ? texts.EnumerateObject().Where(item => item.Value.ValueKind == JsonValueKind.String && item.Value.GetString()?.Trim() is { Length: > 0 })
                        .ToDictionary(item => item.Name, item => item.Value.GetString()!.Trim(), StringComparer.Ordinal)
                    : new Dictionary<string, string>();
            return [.. data.EnumerateArray()
                .Select(item => (Item: item, Id: int.TryParse(Text(item, "tvdb_id"), NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0 ? id : (int?)null, Name: Text(item, "name")))
                .Where(item => item.Id is not null && item.Name is not null && (Text(item.Item, "type") ?? "series") == "series")
                .DistinctBy(item => item.Id)
                .Select(item => new TvdbSearchResult(item.Id!.Value, item.Name!)
                {
                    Year = int.TryParse(Text(item.Item, "year"), NumberStyles.None, CultureInfo.InvariantCulture, out var started) ? started : null,
                    Overview = Text(item.Item, "overview"),
                    ImageUrl = Image(Text(item.Item, "image_url")),
                    OriginalLanguage = Text(item.Item, "primary_language"),
                    Names = Texts(item.Item, "translations"),
                    Overviews = Texts(item.Item, "overviews")
                })];
        }

        // Everything a series import needs in one read: translations, artworks, people, genres, networks, trailers, remote ids and the seasons of the aired order.
        public async Task<TvdbSeries> ReadSeriesAsync(TvdbSession session, int id, CancellationToken ct)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(id);
            using var document = await RequestAsync(HttpMethod.Get, "series/" + id.ToString(CultureInfo.InvariantCulture) + "/extended?meta=translations&short=false", session.Token, null, ct);
            var data = Data(document.RootElement);
            if (Int(data, "id") != id || Text(data, "name") is not { } name)
            {
                throw new TvdbException(TvdbFailure.InvalidResponse, "TVDB returned an invalid series.");
            }

            // Remote identities name the series at IMDb and TMDB.
            string? imdbId = null;
            int? tmdbId = null;
            foreach (var item in Items(data, "remoteIds"))
            {
                var source = Text(item, "sourceName") ?? "";
                if (imdbId is null && source.Contains("IMDb", StringComparison.OrdinalIgnoreCase))
                {
                    imdbId = Text(item, "id");
                }
                else if (tmdbId is null && source.Contains("TheMovieDB", StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(Text(item, "id"), NumberStyles.None, CultureInfo.InvariantCulture, out var tmdb) && tmdb > 0)
                {
                    tmdbId = tmdb;
                }
            }

            // Names and summaries per language; an alias is no translation.
            var translations = Child(data, "translations");
            var names = Items(translations, "nameTranslations").Where(item => Child(item, "isAlias").ValueKind != JsonValueKind.True)
                .Select(item => (Language: Text(item, "language"), Text: Text(item, "name"))).Where(item => item.Language is not null && item.Text is not null)
                .DistinctBy(item => item.Language).ToDictionary(item => item.Language!, item => item.Text, StringComparer.Ordinal);
            var overviews = Items(translations, "overviewTranslations").Select(item => (Language: Text(item, "language"), Text: Text(item, "overview")))
                .Where(item => item.Language is not null && item.Text is not null).DistinctBy(item => item.Language).ToDictionary(item => item.Language!, item => item.Text, StringComparer.Ordinal);
            var languages = names.Keys.Concat(overviews.Keys).Distinct(StringComparer.Ordinal);

            // Networks first, then the studios that made it.
            var companies = Items(data, "companies").Select(item => (Name: Text(item, "name"), Type: Text(Child(item, "companyType"), "companyTypeName"))).ToArray();
            var networks = new[] { Text(Child(data, "originalNetwork"), "name") }
                .Concat(companies.Where(company => company.Type == "Network").Select(company => company.Name))
                .Concat([Text(Child(data, "latestNetwork"), "name")])
                .Concat(companies.Where(company => company.Type == "Studio").Select(company => company.Name))
                .OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

            return new TvdbSeries(id, name, imdbId)
            {
                TmdbId = tmdbId,
                Overview = Text(data, "overview"),
                ImageUrl = Image(Text(data, "image")),
                OriginalLanguage = Text(data, "originalLanguage"),
                OriginalCountry = Text(data, "originalCountry"),
                FirstAired = Date(data, "firstAired"),
                Year = int.TryParse(Text(data, "year"), NumberStyles.None, CultureInfo.InvariantCulture, out var year) ? year : null,
                Status = Text(Child(data, "status"), "name"),
                Translations = [.. languages.Select(language => new TvdbTranslation(language, names.GetValueOrDefault(language), overviews.GetValueOrDefault(language)))],
                Aliases = [.. Items(data, "aliases").Select(item => (Language: Text(item, "language"), Name: Text(item, "name"))).Where(item => item.Language is not null && item.Name is not null)
                    .Select(item => new TvdbTranslation(item.Language!, item.Name, null))],
                Artworks = [.. Items(data, "artworks").Select(item => (Type: Int(item, "type"), Image: Image(Text(item, "image")), Language: Text(item, "language"), Score: Number(item, "score")))
                    .Where(item => item.Type is { } type && Enum.IsDefined((TvdbArtworkType)type) && item.Image is not null)
                    .Select(item => new TvdbArtwork((TvdbArtworkType)item.Type!.Value, item.Image!, item.Language, item.Score ?? 0))],
                Genres = [.. Items(data, "genres").Select(item => Text(item, "name")).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase)],
                Networks = networks,
                Trailers = [.. Items(data, "trailers").Select(item => (Key: YouTubeKey(Text(item, "url")), Language: Text(item, "language"))).Where(item => item.Key is not null)
                    .Select(item => new TvdbTrailer(item.Key!, item.Language))],
                Characters = [.. Items(data, "characters").Select(item => (Person: Text(item, "personName"), Role: Text(item, "peopleType"), Item: item))
                    .Where(item => item.Person is not null && item.Role is not null)
                    .Select(item => new TvdbCharacter(Int(item.Item, "peopleId") is > 0 and var person ? person : null, item.Person!, item.Role!, Text(item.Item, "name"),
                        Image(Text(item.Item, "personImgURL")) ?? Image(Text(item.Item, "image")), Int(item.Item, "sort") ?? int.MaxValue))],
                Seasons = [.. Items(data, "seasons").Where(item => Text(Child(item, "type"), "type") == "official")
                    .Select(item => (Number: Int(item, "number"), Image: Image(Text(item, "image")))).Where(item => item.Number is >= 0)
                    .DistinctBy(item => item.Number).Select(item => new TvdbSeason(item.Number!.Value, item.Image))]
            };
        }

        // Default is the series' TVDB episode order with names in the original language; a language (TheTVDB's code) reads its translations instead.
        // Pagination is never replaced by a remote URL.
        public async Task<IReadOnlyList<TvdbEpisode>> ReadEpisodesAsync(TvdbSession session, int seriesId, string? language, CancellationToken ct)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(seriesId);
            if (language is not null && (language.Length is < 2 or > 4 || !language.All(char.IsAsciiLetterLower)))
            {
                throw new ArgumentException("A TVDB language code is required.", nameof(language));
            }

            var episodes = new List<TvdbEpisode>();
            var known = new HashSet<int>();
            for (var page = 0; page < 100; page++)
            {
                var path = "series/" + seriesId.ToString(CultureInfo.InvariantCulture) + "/episodes/default" + (language is null ? "" : "/" + language)
                    + "?page=" + page.ToString(CultureInfo.InvariantCulture);
                using var document = await RequestAsync(HttpMethod.Get, path, session.Token, null, ct);
                var entries = Child(Data(document.RootElement), "episodes");
                if (entries.ValueKind != JsonValueKind.Array)
                {
                    throw new TvdbException(TvdbFailure.InvalidResponse, "TVDB returned no episode list.");
                }

                foreach (var entry in entries.EnumerateArray())
                {
                    if (Int(entry, "id") is not { } id || id <= 0 || Int(entry, "seasonNumber") is not { } season || season < 0
                        || Int(entry, "number") is not { } number || number <= 0 || !known.Add(id))
                    {
                        throw new TvdbException(TvdbFailure.InvalidResponse, "TVDB returned invalid or duplicate episode numbering.");
                    }

                    var absolute = Int(entry, "absoluteNumber");
                    episodes.Add(new TvdbEpisode(id, season, number, absolute is > 0 ? absolute : null, Text(entry, "name"), Date(entry, "aired"),
                        Int(entry, "runtime") is > 0 and <= 1440 ? Int(entry, "runtime") : null)
                    {
                        Overview = Text(entry, "overview"),
                        ImageUrl = Image(Text(entry, "image"))
                    });
                }

                if (Text(Child(document.RootElement, "links"), "next") is null)
                {
                    return episodes;
                }
            }

            throw new TvdbException(TvdbFailure.InvalidResponse, "TVDB episode pagination exceeded its safety limit.");
        }

        private async Task<JsonDocument> RequestAsync(HttpMethod method, string path, string? token, object? data, CancellationToken ct)
        {
            using var request = new HttpRequestMessage(method, path);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            if (token is not null)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }

            if (data is not null)
            {
                request.Content = JsonContent.Create(data);
            }

            HttpResponseMessage response;
            try
            {
                response = await httpClient.SendAsync(request, ct);
            }
            catch (Exception exception) when (exception is HttpRequestException || exception is OperationCanceledException && !ct.IsCancellationRequested)
            {
                throw new TvdbException(TvdbFailure.Unavailable, "TVDB could not be reached.", exception);
            }

            using (response)
            {
                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    throw new TvdbException(TvdbFailure.AuthenticationFailed, "TVDB rejected its credential.");
                }

                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    throw new TvdbException(TvdbFailure.RateLimited, "TVDB limits requests.");
                }

                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    throw new TvdbException(TvdbFailure.NotFound, "TVDB does not know this entry.");
                }

                if (!response.IsSuccessStatusCode)
                {
                    throw new TvdbException(TvdbFailure.Unavailable, "TVDB answered HTTP " + ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture) + ".");
                }

                try
                {
                    return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                }
                catch (JsonException exception)
                {
                    throw new TvdbException(TvdbFailure.InvalidResponse, "TVDB returned invalid JSON.", exception);
                }
            }
        }

        private static JsonElement Data(JsonElement root) =>
            Child(root, "data") is { ValueKind: not JsonValueKind.Undefined and not JsonValueKind.Null } data
                ? data : throw new TvdbException(TvdbFailure.InvalidResponse, "TVDB returned no data.");

        private static JsonElement Child(JsonElement parent, string property) =>
            parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(property, out var value) ? value : default;

        private static string? Text(JsonElement parent, string property) =>
            Child(parent, property) is { ValueKind: JsonValueKind.String } value && value.GetString()?.Trim() is { Length: > 0 } text ? text : null;

        private static int? Int(JsonElement parent, string property) =>
            Child(parent, property) is { ValueKind: JsonValueKind.Number } value && value.TryGetInt32(out var number) ? number : null;

        private static double? Number(JsonElement parent, string property) =>
            Child(parent, property) is { ValueKind: JsonValueKind.Number } value && value.TryGetDouble(out var number) ? number : null;

        private static DateOnly? Date(JsonElement parent, string property) =>
            DateOnly.TryParseExact(Text(parent, property), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;

        private static IEnumerable<JsonElement> Items(JsonElement parent, string property) =>
            Child(parent, property) is { ValueKind: JsonValueKind.Array } list ? list.EnumerateArray() : [];

        // youtube.com/watch?v=KEY, youtu.be/KEY or youtube.com/embed/KEY.
        private static string? YouTubeKey(string? url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var address))
            {
                return null;
            }

            var host = address.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) || address.Host.StartsWith("m.", StringComparison.OrdinalIgnoreCase)
                ? address.Host[(address.Host.IndexOf('.') + 1)..] : address.Host;
            var key = host.ToLowerInvariant() switch
            {
                "youtu.be" => address.AbsolutePath.Trim('/'),
                "youtube.com" when address.AbsolutePath.StartsWith("/embed/", StringComparison.Ordinal) => address.AbsolutePath["/embed/".Length..].Trim('/'),
                "youtube.com" => address.Query.TrimStart('?').Split('&').Select(pair => pair.Split('=', 2)).FirstOrDefault(pair => pair.Length == 2 && pair[0] == "v")?[1],
                _ => null
            };
            return key is { Length: 11 } && key.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_') ? key : null;
        }

        // TheTVDB names images by an absolute address or by a path on its artwork host.
        private static string? Image(string? value) => value switch
        {
            { Length: > 8 } url when url.StartsWith("https://", StringComparison.Ordinal) => url,
            { Length: > 1 } path when path.StartsWith('/') && !path.StartsWith("//", StringComparison.Ordinal) => "https://artworks.thetvdb.com" + path,
            _ => null
        };
    }
}
