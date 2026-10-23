using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Jularr.Integrations.Data.MediaServer;

namespace Jularr.Integrations.Logic.MediaServer
{
    // A Jellyfin or Emby server read and written with the administrator's API key on behalf of its users.
    internal sealed partial class MediaServerClient(HttpClient http)
    {
        public const int PageSize = 500;

        // Both servers name their fields in PascalCase.
        private static readonly JsonSerializerOptions PascalCase = new();

        public async Task<MediaServerInfo> ReadServerAsync(MediaServerConnection connection, CancellationToken ct)
        {
            using var document = await GetAsync(connection, "System/Info", ct);
            var root = document.RootElement;
            return new MediaServerInfo(Id(Text(root, "Id")) ?? throw Invalid("The server id is missing."), Text(root, "ServerName") ?? connection.BaseUrl.Host, Text(root, "Version"));
        }

        public async Task<IReadOnlyList<MediaServerUser>> ReadUsersAsync(MediaServerConnection connection, CancellationToken ct)
        {
            using var document = await GetAsync(connection, "Users", ct);
            return document.RootElement.ValueKind == JsonValueKind.Array
                ? [.. document.RootElement.EnumerateArray().Take(1000).Select(user => (Id: Id(Text(user, "Id")), Name: Text(user, "Name")))
                    .Where(user => user.Id is not null && user.Name is not null).Select(user => new MediaServerUser(user.Id!, user.Name!.Length > 200 ? user.Name[..200] : user.Name))]
                : throw Invalid("The user list is missing.");
        }

        public async Task<MediaServerItemPage> ReadItemsAsync(MediaServerConnection connection, string userId, bool series, int start, CancellationToken ct)
        {
            // Series with their provider ids, or movies and episodes with this user's state.
            var query = FormattableString.Invariant(
                $"Recursive=true&IncludeItemTypes={(series ? "Series" : "Movie,Episode")}&Fields=ProviderIds&EnableImages=false&EnableUserData={(series ? "false" : "true")}&SortBy=SortName&StartIndex={start}&Limit={PageSize}");
            using var document = await GetAsync(connection, connection.Dialect == MediaServerDialect.Jellyfin
                ? "Items?userId=" + Uri.EscapeDataString(userId) + "&" + query
                : "Users/" + Uri.EscapeDataString(userId) + "/Items?" + query, ct);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("Items", out var list) || list.ValueKind != JsonValueKind.Array)
            {
                throw Invalid("The item list is missing.");
            }

            var items = new List<MediaServerItem>();
            foreach (var item in list.EnumerateArray())
            {
                var kind = Text(item, "Type") switch
                {
                    "Movie" => MediaServerItemKind.Movie,
                    "Series" => MediaServerItemKind.Series,
                    "Episode" => MediaServerItemKind.Episode,
                    _ => (MediaServerItemKind?)null
                };
                if (kind is not { } itemKind || Id(Text(item, "Id")) is not { } id)
                {
                    continue;
                }

                var providers = item.TryGetProperty("ProviderIds", out var value) && value.ValueKind == JsonValueKind.Object ? value : default;
                var data = item.TryGetProperty("UserData", out var state) && state.ValueKind == JsonValueKind.Object ? state : default;
                items.Add(new MediaServerItem(id, itemKind, ProviderId(providers, "Tmdb"), ProviderId(providers, "Imdb"), ProviderId(providers, "Tvdb"), Id(Text(item, "SeriesId")),
                    Number(item, "ParentIndexNumber"), Number(item, "IndexNumber"), Milliseconds(data, "PlaybackPositionTicks") ?? 0,
                    data.ValueKind == JsonValueKind.Object && data.TryGetProperty("Played", out var played) && played.ValueKind == JsonValueKind.True, Milliseconds(item, "RunTimeTicks")));
            }

            var total = root.TryGetProperty("TotalRecordCount", out var count) && count.TryGetInt32(out var size) ? size : start + items.Count;
            return new MediaServerItemPage(items, total);
        }

        public async Task SaveUserDataAsync(MediaServerConnection connection, string userId, string itemId, long positionMs, bool isPlayed, CancellationToken ct)
        {
            var path = connection.Dialect == MediaServerDialect.Jellyfin
                ? "UserItems/" + Uri.EscapeDataString(itemId) + "/UserData?userId=" + Uri.EscapeDataString(userId)
                : "Users/" + Uri.EscapeDataString(userId) + "/Items/" + Uri.EscapeDataString(itemId) + "/UserData";
            using var request = Request(connection, HttpMethod.Post, path);
            request.Content = JsonContent.Create(new { PlaybackPositionTicks = positionMs * TimeSpan.TicksPerMillisecond, Played = isPlayed }, options: PascalCase);
            using var response = await SendAsync(request, ct);
        }

        private async Task<JsonDocument> GetAsync(MediaServerConnection connection, string path, CancellationToken ct)
        {
            using var request = Request(connection, HttpMethod.Get, path);
            using var response = await SendAsync(request, ct);
            try
            {
                await using var stream = await response.Content.ReadAsStreamAsync(ct);
                return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            }
            catch (JsonException exception)
            {
                throw new MediaServerException(MediaServerFailure.InvalidResponse, "The media server returned invalid JSON.", exception);
            }
        }

        private static HttpRequestMessage Request(MediaServerConnection connection, HttpMethod method, string path)
        {
            var request = new HttpRequestMessage(method, new Uri(connection.BaseUrl, path));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            if (connection.Dialect == MediaServerDialect.Jellyfin)
            {
                request.Headers.TryAddWithoutValidation("Authorization", "MediaBrowser Token=\"" + connection.ApiKey + "\"");
            }
            else
            {
                request.Headers.Add("X-Emby-Token", connection.ApiKey);
            }

            return request;
        }

        private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            HttpResponseMessage response;
            try
            {
                response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            }
            catch (Exception exception) when (exception is HttpRequestException || exception is TaskCanceledException && !ct.IsCancellationRequested)
            {
                throw new MediaServerException(MediaServerFailure.Unavailable, "The media server could not be reached.", exception);
            }

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                response.Dispose();
                throw new MediaServerException(MediaServerFailure.Unauthorized, "The media server rejected the API key.");
            }

            if (!response.IsSuccessStatusCode)
            {
                response.Dispose();
                throw new MediaServerException(MediaServerFailure.Unavailable, "The media server answered " + ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture) + ".");
            }

            return response;
        }

        private static string? Text(JsonElement element, string name) =>
            element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString()!.Trim() is { Length: > 0 } text
                ? text
                : null;

        private static int? Number(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) && number is >= 0 and <= 100_000 ? number : null;

        private static long? Milliseconds(JsonElement element, string name) =>
            element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var ticks) && ticks > 0
                ? ticks / TimeSpan.TicksPerMillisecond
                : null;

        private static string? ProviderId(JsonElement providers, string name) =>
            Text(providers, name) is { Length: <= 64 } value && value.All(char.IsAsciiLetterOrDigit) ? value : null;

        private static string? Id(string? value) => value is not null && ItemId().IsMatch(value) ? value : null;

        private static MediaServerException Invalid(string message) => new(MediaServerFailure.InvalidResponse, message);

        [GeneratedRegex("^[A-Za-z0-9-]{1,64}$")]
        private static partial Regex ItemId();
    }
}
