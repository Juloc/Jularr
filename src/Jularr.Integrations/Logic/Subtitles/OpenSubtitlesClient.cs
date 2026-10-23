using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Jularr.Integrations.Data.Subtitles;

namespace Jularr.Integrations.Logic.Subtitles
{
    // OpenSubtitles.com REST API: search with the API key, download after an account login.
    internal sealed class OpenSubtitlesClient(HttpClient http)
    {
        private const long MaximumBytes = 10L * 1024 * 1024;
        private static readonly ConcurrentDictionary<string, (string Token, DateTimeOffset ExpiresAt)> Sessions = new(StringComparer.Ordinal);

        public async Task<IReadOnlyList<SubtitleOffer>> SearchAsync(SubtitleAccess access, SubtitleQuery query, CancellationToken ct)
        {
            // Portuguese and Chinese exist only as regional variants.
            var languages = query.Languages.SelectMany(language => language switch { "pt" => new[] { "pt-br", "pt-pt" }, "zh" => ["zh-cn", "zh-tw"], _ => [language] });
            var parameters = new List<string> { "languages=" + string.Join(',', languages.Order(StringComparer.Ordinal)) };
            var imdb = query.ImdbId?.StartsWith("tt", StringComparison.OrdinalIgnoreCase) is true ? query.ImdbId[2..].TrimStart('0') : null;
            if (query.IsEpisode)
            {
                parameters.Add(imdb is not null ? "parent_imdb_id=" + imdb : "parent_tmdb_id=" + query.TmdbId);
                parameters.Add("season_number=" + query.Season?.ToString(CultureInfo.InvariantCulture));
                parameters.Add("episode_number=" + query.Episode?.ToString(CultureInfo.InvariantCulture));
            }
            else
            {
                parameters.Add(imdb is not null ? "imdb_id=" + imdb : "tmdb_id=" + query.TmdbId);
            }

            using var request = Request(HttpMethod.Get, access, "subtitles?" + string.Join('&', parameters.Order(StringComparer.Ordinal)));
            using var document = await SendAsync(request, ct);
            var offers = new List<SubtitleOffer>();
            if (document.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in data.EnumerateArray())
                {
                    if (!item.TryGetProperty("attributes", out var attributes) || !attributes.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array
                        || files.GetArrayLength() == 0 || !files[0].TryGetProperty("file_id", out var fileId) || Text(attributes, "language") is not { } language)
                    {
                        continue;
                    }

                    offers.Add(new SubtitleOffer(fileId.GetRawText(), language.Split('-')[0].ToLowerInvariant(), Text(attributes, "release"), Flag(attributes, "hearing_impaired"),
                        Flag(attributes, "foreign_parts_only"), attributes.TryGetProperty("download_count", out var count) && count.TryGetInt64(out var downloads) ? downloads : 0));
                }
            }

            return offers;
        }

        public async Task<byte[]> DownloadAsync(SubtitleAccess access, SubtitleOffer offer, CancellationToken ct)
        {
            var token = await LoginAsync(access, ct);
            using var request = Request(HttpMethod.Post, access, "download");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Content = JsonContent.Create(new { file_id = long.Parse(offer.Handle, CultureInfo.InvariantCulture), sub_format = "srt" });
            using var document = await SendAsync(request, ct);
            if (Text(document.RootElement, "link") is not { } link || !Uri.TryCreate(link, UriKind.Absolute, out var address) || address.Scheme != Uri.UriSchemeHttps)
            {
                throw new SubtitleException("acquisition_invalid_response");
            }

            return await ReadFileAsync(address, ct);
        }

        // A login proves the key and the account.
        public async Task TestAsync(SubtitleAccess access, CancellationToken ct) => await LoginAsync(access, ct);

        // A login lasts a day; the provider allows few of them.
        private async Task<string> LoginAsync(SubtitleAccess access, CancellationToken ct)
        {
            if (access.Username is null || access.Password is null)
            {
                throw new SubtitleException("acquisition_authentication_failed");
            }

            var account = access.BaseUrl + "\n" + access.Username + "\n" + access.Password;
            if (Sessions.TryGetValue(account, out var session) && session.ExpiresAt > DateTimeOffset.UtcNow)
            {
                return session.Token;
            }

            using var request = Request(HttpMethod.Post, access, "login");
            request.Content = JsonContent.Create(new { username = access.Username, password = access.Password });
            using var document = await SendAsync(request, ct);
            var token = Text(document.RootElement, "token") ?? throw new SubtitleException("acquisition_authentication_failed");
            Sessions[account] = (token, DateTimeOffset.UtcNow.AddHours(20));
            return token;
        }

        private static HttpRequestMessage Request(HttpMethod method, SubtitleAccess access, string path)
        {
            var request = new HttpRequestMessage(method, new Uri(new Uri(access.BaseUrl.TrimEnd('/') + "/"), path));
            request.Headers.Add("Api-Key", access.ApiKey);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            return request;
        }

        private async Task<JsonDocument> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            HttpResponseMessage response;
            try
            {
                response = await http.SendAsync(request, ct);
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                throw new SubtitleException("acquisition_unavailable", exception);
            }

            using (response)
            {
                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    throw new SubtitleException("acquisition_authentication_failed");
                }

                if (response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode == 406)
                {
                    throw new SubtitleException("acquisition_rate_limited");
                }

                if (!response.IsSuccessStatusCode)
                {
                    throw new SubtitleException("acquisition_unavailable");
                }

                try
                {
                    return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
                }
                catch (JsonException exception)
                {
                    throw new SubtitleException("acquisition_invalid_response", exception);
                }
            }
        }

        private async Task<byte[]> ReadFileAsync(Uri address, CancellationToken ct)
        {
            try
            {
                using var response = await http.GetAsync(address, HttpCompletionOption.ResponseHeadersRead, ct);
                if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaximumBytes)
                {
                    throw new SubtitleException("acquisition_unavailable");
                }

                var bytes = await response.Content.ReadAsByteArrayAsync(ct);
                return bytes.Length is > 0 and <= (int)MaximumBytes ? bytes : throw new SubtitleException("acquisition_invalid_response");
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                throw new SubtitleException("acquisition_unavailable", exception);
            }
        }

        private static string? Text(JsonElement element, string name) =>
            element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text ? text : null;

        private static bool Flag(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
    }
}
