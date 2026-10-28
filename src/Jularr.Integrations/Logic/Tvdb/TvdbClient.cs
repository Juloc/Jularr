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
        private const string TitleLanguage = "eng";

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

        public async Task<TvdbSeries> ReadSeriesAsync(TvdbSession session, int id, CancellationToken ct)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(id);
            using var document = await RequestAsync(HttpMethod.Get, "series/" + id.ToString(CultureInfo.InvariantCulture) + "/extended", session.Token, null, ct);
            var data = Data(document.RootElement);
            if (Int(data, "id") != id || Text(data, "name") is not { } name)
            {
                throw new TvdbException(TvdbFailure.InvalidResponse, "TVDB returned an invalid series.");
            }

            string? imdbId = null;
            var remoteIds = Child(data, "remoteIds");
            if (remoteIds.ValueKind == JsonValueKind.Array)
            {
                imdbId = remoteIds.EnumerateArray()
                    .Where(item => Text(item, "sourceName") is { } source && source.Contains("IMDb", StringComparison.OrdinalIgnoreCase))
                    .Select(item => Text(item, "id")).FirstOrDefault(value => value is not null);
            }

            return new TvdbSeries(id, name, imdbId);
        }

        // Episode titles are asked in English, like Sonarr; TVDB names them in the series' original language otherwise (Japanese for most anime).
        // An episode without an English title keeps its original-language name.
        public async Task<IReadOnlyList<TvdbEpisode>> ReadEpisodesAsync(TvdbSession session, int seriesId, CancellationToken ct)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(seriesId);
            var english = await ReadEpisodePagesAsync(session, seriesId, "/" + TitleLanguage, ct);
            if (english.All(episode => episode.Name is not null))
            {
                return english;
            }

            var original = (await ReadEpisodePagesAsync(session, seriesId, "", ct)).ToDictionary(episode => episode.Id);
            return [.. english.Select(episode => episode.Name is null && original.TryGetValue(episode.Id, out var named) ? episode.WithName(named.Name) : episode)];
        }

        // Default is the series' TVDB episode order. Pagination is never replaced by a remote URL.
        private async Task<List<TvdbEpisode>> ReadEpisodePagesAsync(TvdbSession session, int seriesId, string languagePath, CancellationToken ct)
        {
            var episodes = new List<TvdbEpisode>();
            var known = new HashSet<int>();
            for (var page = 0; page < 100; page++)
            {
                var path = "series/" + seriesId.ToString(CultureInfo.InvariantCulture) + "/episodes/default" + languagePath + "?page=" + page.ToString(CultureInfo.InvariantCulture);
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
                    episodes.Add(new TvdbEpisode(id, season, number, absolute is > 0 ? absolute : null, Text(entry, "name"),
                        DateOnly.TryParseExact(Text(entry, "aired"), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var aired) ? aired : null,
                        Int(entry, "runtime") is > 0 and <= 1440 ? Int(entry, "runtime") : null));
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
    }
}
