using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Jularr.Integrations.Data.Tmdb;

namespace Jularr.Integrations.Logic.Tmdb
{
    // TMDB API v3 reads; the base address is configured by the host.
    internal sealed class TmdbClient(HttpClient httpClient)
    {
        public async Task<IReadOnlyList<TmdbSearchResult>> SearchAsync(TmdbConnection connection, TmdbMediaKind kind, string query, int? year, CancellationToken ct)
        {
            var parameters = new List<KeyValuePair<string, string>> { new("query", query), new("include_adult", "false") };
            if (year is { } value)
            {
                parameters.Add(new(kind == TmdbMediaKind.Movie ? "year" : "first_air_date_year", value.ToString(CultureInfo.InvariantCulture)));
            }

            using var document = await ReadAsync(connection, kind == TmdbMediaKind.Movie ? "search/movie" : "search/tv", parameters, ct);
            return document.RootElement.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array
                ? results.EnumerateArray().Select(item => ParseResult(kind, item)).OfType<TmdbSearchResult>().Take(20).ToArray()
                : [];
        }

        public async Task<TmdbWork> ReadWorkAsync(TmdbConnection connection, TmdbMediaKind kind, int id, CancellationToken ct)
        {
            var path = (kind == TmdbMediaKind.Movie ? "movie/" : "tv/") + id.ToString(CultureInfo.InvariantCulture);
            using var document = await ReadAsync(connection, path, [new("append_to_response", "external_ids")], ct);
            var root = document.RootElement;
            var result = ParseResult(kind, root) ?? throw new TmdbException(TmdbFailure.InvalidResponse, "TMDB returned a work without a title.");
            var externalIds = root.TryGetProperty("external_ids", out var ids) ? ids : default;

            // Specials keep season 0; the acquisition decides whether to want them.
            var seasons = root.TryGetProperty("seasons", out var seasonList) && seasonList.ValueKind == JsonValueKind.Array
                ? seasonList.EnumerateArray()
                    .Where(season => Int(season, "season_number") is not null)
                    .Select(season => new TmdbSeason(Int(season, "season_number")!.Value, Text(season, "name"), Date(season, "air_date"), Int(season, "episode_count") ?? 0))
                    .ToArray()
                : [];
            return new TmdbWork(kind, result.Id, result.Title, result.OriginalTitle, result.Year, result.Overview, result.PosterPath,
                Text(externalIds, "imdb_id") ?? Text(root, "imdb_id"), Int(externalIds, "tvdb_id"), seasons);
        }

        public async Task<IReadOnlyList<TmdbEpisode>> ReadSeasonAsync(TmdbConnection connection, int seriesId, int seasonNumber, CancellationToken ct)
        {
            using var document = await ReadAsync(connection, "tv/" + seriesId.ToString(CultureInfo.InvariantCulture) + "/season/" + seasonNumber.ToString(CultureInfo.InvariantCulture), [], ct);
            return document.RootElement.TryGetProperty("episodes", out var episodes) && episodes.ValueKind == JsonValueKind.Array
                ? episodes.EnumerateArray()
                    .Where(episode => Int(episode, "episode_number") is > 0)
                    .Select(episode => new TmdbEpisode(Int(episode, "season_number") ?? seasonNumber, Int(episode, "episode_number")!.Value, Text(episode, "name"), Date(episode, "air_date")))
                    .ToArray()
                : [];
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

            var poster = Text(item, "poster_path");
            return new TmdbSearchResult(id, title, Text(item, movie ? "original_title" : "original_name"), Date(item, movie ? "release_date" : "first_air_date")?.Year,
                Text(item, "overview"), poster is { Length: <= 200 } && poster.StartsWith('/') ? poster : null);
        }

        private static string? Text(JsonElement element, string name) =>
            element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                && value.GetString()?.Trim() is { Length: > 0 } text ? text : null;

        private static int? Int(JsonElement element, string name) =>
            element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
                ? number : null;

        private static DateOnly? Date(JsonElement element, string name) =>
            DateOnly.TryParseExact(Text(element, name), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;
    }
}
