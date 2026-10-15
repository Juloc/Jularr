using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Jularr.Integrations.Data.Common;
using Jularr.Integrations.Data.Igdb;

namespace Jularr.Integrations.Logic.Igdb
{
    // IGDB through a Twitch application token; tokens are cached until shortly before they expire.
    internal sealed partial class IgdbClient(HttpClient http)
    {
        private static readonly Uri TokenEndpoint = new("https://id.twitch.tv/oauth2/token");
        private static readonly Uri Api = new("https://api.igdb.com/v4/");
        private static readonly ConcurrentDictionary<string, (string Token, DateTimeOffset Expires)> Tokens = new(StringComparer.Ordinal);
        private static readonly SemaphoreSlim Pace = new(1, 1);
        private static readonly TimeSpan Spacing = TimeSpan.FromMilliseconds(260);
        private static DateTimeOffset _last = DateTimeOffset.MinValue;

        private const string Fields = "fields name,first_release_date,summary,cover.image_id,platforms.name,platforms.abbreviation,"
            + "involved_companies.company.id,involved_companies.company.name,involved_companies.developer,alternative_names.name;";

        public static string CoverUrl(string imageId) => "https://images.igdb.com/igdb/image/upload/t_cover_big/" + imageId + ".jpg";

        public async Task<IReadOnlyList<IgdbGame>> SearchAsync(IgdbConnection connection, string query, CancellationToken ct)
        {
            using var document = await QueryAsync(connection, "games", "search \"" + Unquoted().Replace(query, " ").Trim() + "\"; " + Fields + " limit 20;", ct);
            return [.. document.RootElement.EnumerateArray().Select(Read).OfType<IgdbGame>()];
        }

        public async Task<IgdbGame> ReadAsync(IgdbConnection connection, int id, CancellationToken ct)
        {
            using var document = await QueryAsync(connection, "games", Fields + " where id = " + id.ToString(CultureInfo.InvariantCulture) + ";", ct);
            return document.RootElement.EnumerateArray().Select(Read).OfType<IgdbGame>().FirstOrDefault() ?? throw new IgdbException(IgdbFailure.NotFound, "IGDB does not know this game.");
        }

        private static IgdbGame? Read(JsonElement game)
        {
            if (game.ValueKind != JsonValueKind.Object || Int(game, "id") is not { } id || Text(game, "name") is not { } name)
            {
                return null;
            }

            var released = game.TryGetProperty("first_release_date", out var seconds) && seconds.ValueKind == JsonValueKind.Number && seconds.TryGetInt64(out var unix)
                ? DateTimeOffset.FromUnixTimeSeconds(unix).Year
                : (int?)null;
            var platforms = Array(game, "platforms").Select(platform => Int(platform, "id") is { } platformId && Text(platform, "name") is { } platformName
                ? new IgdbPlatform(platformId, Limit(platformName, 200), Text(platform, "abbreviation") is { } abbreviation ? Limit(abbreviation, 32) : null)
                : null).OfType<IgdbPlatform>().Take(50).ToArray();
            var studios = Array(game, "involved_companies").Where(company => company.TryGetProperty("developer", out var developer) && developer.ValueKind == JsonValueKind.True)
                .Select(company => company.TryGetProperty("company", out var entry) && Text(entry, "name") is { } studio
                    ? new ProviderPerson(Int(entry, "id")?.ToString(CultureInfo.InvariantCulture), Limit(studio, 300), ProviderPerson.Developer)
                    : null)
                .OfType<ProviderPerson>().DistinctBy(studio => studio.Id ?? studio.Name).Take(3).ToArray();
            var developers = studios.Select(studio => studio.Name).Distinct(StringComparer.Ordinal).ToArray();
            var alternatives = Array(game, "alternative_names").Select(alternative => Text(alternative, "name")).OfType<string>().Where(alternative => alternative.Length <= 500).Take(10).ToArray();
            var cover = game.TryGetProperty("cover", out var image) ? Text(image, "image_id") : null;
            return new IgdbGame(id, Limit(name, 500), released, Text(game, "summary"), cover is not null && ImageId().IsMatch(cover) ? cover : null, platforms, developers, alternatives)
            {
                People = studios
            };
        }

        private async Task<JsonDocument> QueryAsync(IgdbConnection connection, string endpoint, string body, CancellationToken ct)
        {
            for (var attempt = 0; ; attempt++)
            {
                var token = await TokenAsync(connection, attempt > 0, ct);
                using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(Api, endpoint)) { Content = new StringContent(body, Encoding.UTF8, "text/plain") };
                request.Headers.Add("Client-ID", connection.ClientId);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                using var response = await SendAsync(request, ct);

                // An expired token is renewed once.
                if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0)
                {
                    continue;
                }

                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    throw new IgdbException(IgdbFailure.Unauthorized, "IGDB refused the application.");
                }

                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    throw new IgdbException(IgdbFailure.RateLimited, "IGDB limits requests.");
                }

                if (!response.IsSuccessStatusCode)
                {
                    throw new IgdbException(IgdbFailure.Unavailable, "IGDB failed.");
                }

                var document = await ParseAsync(response, ct);
                if (document.RootElement.ValueKind != JsonValueKind.Array)
                {
                    document.Dispose();
                    throw new IgdbException(IgdbFailure.InvalidResponse, "IGDB returned no list.");
                }

                return document;
            }
        }

        private async Task<string> TokenAsync(IgdbConnection connection, bool renew, CancellationToken ct)
        {
            // The cache key never contains the secret itself.
            var key = connection.ClientId + ":" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(connection.ClientSecret)));
            if (!renew && Tokens.TryGetValue(key, out var cached) && cached.Expires > DateTimeOffset.UtcNow)
            {
                return cached.Token;
            }

            using var request = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint)
            {
                Content = new FormUrlEncodedContent([new("client_id", connection.ClientId), new("client_secret", connection.ClientSecret), new("grant_type", "client_credentials")])
            };
            using var response = await SendAsync(request, ct);
            if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                throw new IgdbException(IgdbFailure.Unauthorized, "Twitch refused the application.");
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new IgdbException(IgdbFailure.Unavailable, "Twitch did not issue a token.");
            }

            using var document = await ParseAsync(response, ct);
            var token = Text(document.RootElement, "access_token") ?? throw new IgdbException(IgdbFailure.InvalidResponse, "Twitch returned no token.");
            var lifetime = document.RootElement.TryGetProperty("expires_in", out var expires) && expires.TryGetInt64(out var value) ? TimeSpan.FromSeconds(Math.Max(60, value)) : TimeSpan.FromHours(1);
            Tokens[key] = (token, DateTimeOffset.UtcNow + lifetime - TimeSpan.FromMinutes(1));
            return token;
        }

        private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            // IGDB allows four requests per second.
            await Pace.WaitAsync(ct);
            try
            {
                var wait = _last + Spacing - DateTimeOffset.UtcNow;
                if (wait > TimeSpan.Zero)
                {
                    await Task.Delay(wait, ct);
                }

                return await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            }
            catch (Exception exception) when (exception is HttpRequestException || exception is TaskCanceledException && !ct.IsCancellationRequested)
            {
                throw new IgdbException(IgdbFailure.Unavailable, "IGDB could not be reached.", exception);
            }
            finally
            {
                _last = DateTimeOffset.UtcNow;
                Pace.Release();
            }
        }

        private static async Task<JsonDocument> ParseAsync(HttpResponseMessage response, CancellationToken ct)
        {
            try
            {
                await using var stream = await response.Content.ReadAsStreamAsync(ct);
                return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            }
            catch (JsonException exception)
            {
                throw new IgdbException(IgdbFailure.InvalidResponse, "IGDB returned invalid JSON.", exception);
            }
            catch (Exception exception) when (exception is HttpRequestException or IOException || exception is TaskCanceledException && !ct.IsCancellationRequested)
            {
                throw new IgdbException(IgdbFailure.Unavailable, "IGDB stopped answering.", exception);
            }
        }

        private static IEnumerable<JsonElement> Array(JsonElement element, string property) =>
            element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Array
                ? value.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.Object)
                : [];

        private static string? Text(JsonElement element, string property) =>
            element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String && value.GetString()!.Trim() is { Length: > 0 } text ? text : null;

        private static int? Int(JsonElement element, string property) =>
            element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : null;

        private static string Limit(string value, int length) => value.Length <= length ? value : value[..length];

        // Search text never closes the query string.
        [GeneratedRegex("[\"\\\\;]")]
        private static partial Regex Unquoted();

        [GeneratedRegex("^[a-z0-9]{1,64}$")]
        private static partial Regex ImageId();
    }
}
