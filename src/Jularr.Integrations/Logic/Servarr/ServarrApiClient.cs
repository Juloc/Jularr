using System.Globalization;
using System.Net;
using System.Text.Json;
using Jularr.Integrations.Data.Servarr;

namespace Jularr.Integrations.Logic.Servarr
{
    // Reads Prowlarr, Sonarr and Radarr once for a takeover; Jularr never runs alongside them.
    internal sealed class ServarrApiClient(HttpClient http)
    {
        private const long MaximumBytes = 32L * 1024 * 1024;

        public async Task<IReadOnlyList<ServarrIndexer>> ReadIndexersAsync(ServarrConnection connection, CancellationToken ct)
        {
            // Usenet Newznab indexers only; torrent trackers have no place in Jularr.
            using var document = await GetAsync(connection, "indexer", ct);
            return [.. Items(document).Where(item => Text(item, "protocol") == "usenet" && Text(item, "implementation") == "Newznab").Select(item =>
            {
                var fields = Fields(item);
                var baseUrl = (FieldText(fields, "baseUrl") ?? "").TrimEnd('/') + (FieldText(fields, "apiPath") is { } path && path != "/api" ? path : "");
                return new ServarrIndexer(Text(item, "name") ?? baseUrl, baseUrl, Secret(FieldText(fields, "apiKey")), Bool(item, "enable") ?? Bool(item, "enableRss") ?? true,
                    Math.Clamp(Int(item, "priority") ?? 25, 1, 100), Categories(fields));
            }).Where(indexer => Uri.TryCreate(indexer.BaseUrl, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")];
        }

        public async Task<IReadOnlyList<ServarrClient>> ReadClientsAsync(ServarrConnection connection, CancellationToken ct)
        {
            using var document = await GetAsync(connection, "downloadclient", ct);
            return [.. Items(document).Where(item => Text(item, "implementation") == "Sabnzbd").Select(item =>
            {
                var fields = Fields(item);
                var scheme = FieldBool(fields, "useSsl") == true ? "https" : "http";
                var port = FieldInt(fields, "port") is { } value ? ":" + value.ToString(CultureInfo.InvariantCulture) : "";
                var baseUrl = scheme + "://" + FieldText(fields, "host") + port + (FieldText(fields, "urlBase") is { Length: > 0 } urlBase ? "/" + urlBase.Trim('/') : "");
                return new ServarrClient(Text(item, "name") ?? "SABnzbd", baseUrl, Secret(FieldText(fields, "apiKey")), Bool(item, "enable") ?? true, Math.Clamp(Int(item, "priority") ?? 25, 1, 100),
                    FieldText(fields, "movieCategory") ?? FieldText(fields, "tvCategory"));
            }).Where(client => Uri.TryCreate(client.BaseUrl, UriKind.Absolute, out var uri) && uri.Host.Length > 0)];
        }

        public async Task<IReadOnlyList<ServarrTitle>> ReadMoviesAsync(ServarrConnection connection, CancellationToken ct)
        {
            using var document = await GetAsync(connection, "movie", ct);
            return [.. Items(document).Select(item => new ServarrTitle(Text(item, "title") ?? "", Int(item, "year"), Int(item, "tmdbId") is > 0 and var id ? id : null, Bool(item, "monitored") ?? false,
                Bool(item, "hasFile") == false)).Where(title => title.Title.Length > 0)];
        }

        public async Task<IReadOnlyList<ServarrTitle>> ReadSeriesAsync(ServarrConnection connection, CancellationToken ct)
        {
            // A series misses something while aired episodes have no file.
            using var document = await GetAsync(connection, "series", ct);
            return [.. Items(document).Select(item =>
            {
                var statistics = item.TryGetProperty("statistics", out var value) && value.ValueKind == JsonValueKind.Object ? value : default;
                var missing = statistics.ValueKind == JsonValueKind.Object && (Int(statistics, "episodeFileCount") ?? 0) < (Int(statistics, "episodeCount") ?? 0);
                return new ServarrTitle(Text(item, "title") ?? "", Int(item, "year"), Int(item, "tmdbId") is > 0 and var id ? id : null, Bool(item, "monitored") ?? false, missing);
            }).Where(title => title.Title.Length > 0)];
        }

        private async Task<JsonDocument> GetAsync(ServarrConnection connection, string resource, CancellationToken ct)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(connection.BaseUrl, "api/v" + connection.ApiVersion.ToString(CultureInfo.InvariantCulture) + "/" + resource));
            request.Headers.Add("X-Api-Key", connection.ApiKey);
            try
            {
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    throw new ServarrException(ServarrFailure.Unauthorized, "The API key was refused.");
                }

                if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaximumBytes)
                {
                    throw new ServarrException(ServarrFailure.Unavailable, "The application did not answer.");
                }

                await using var stream = await response.Content.ReadAsStreamAsync(ct);
                return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            }
            catch (JsonException exception)
            {
                throw new ServarrException(ServarrFailure.InvalidResponse, "The application returned invalid JSON.", exception);
            }
            catch (Exception exception) when (exception is HttpRequestException or IOException || exception is TaskCanceledException && !ct.IsCancellationRequested)
            {
                throw new ServarrException(ServarrFailure.Unavailable, "The application could not be reached.", exception);
            }
        }

        private static IEnumerable<JsonElement> Items(JsonDocument document) =>
            document.RootElement.ValueKind == JsonValueKind.Array ? document.RootElement.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.Object).Take(10_000) : [];

        private static Dictionary<string, JsonElement> Fields(JsonElement item)
        {
            var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            if (item.TryGetProperty("fields", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var field in list.EnumerateArray().Where(field => field.ValueKind == JsonValueKind.Object))
                {
                    if (Text(field, "name") is { } name && field.TryGetProperty("value", out var value))
                    {
                        fields[name] = value;
                    }
                }
            }

            return fields;
        }

        // Hidden keys come back as asterisks.
        private static string? Secret(string? value) => value is { Length: > 0 } && value.Trim('*').Length > 0 ? value : null;

        private static int[] Categories(Dictionary<string, JsonElement> fields) =>
            fields.TryGetValue("categories", out var value) && value.ValueKind == JsonValueKind.Array
                ? [.. value.EnumerateArray().Where(category => category.ValueKind == JsonValueKind.Number && category.TryGetInt32(out var id) && id is >= 1 and <= 999999).Select(category => category.GetInt32()).Distinct().Take(50)]
                : [];

        private static string? FieldText(Dictionary<string, JsonElement> fields, string name) =>
            fields.TryGetValue(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString()!.Trim() is { Length: > 0 } text ? text : null;

        private static int? FieldInt(Dictionary<string, JsonElement> fields, string name) => fields.TryGetValue(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : null;

        private static bool? FieldBool(Dictionary<string, JsonElement> fields, string name) => fields.TryGetValue(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;

        private static string? Text(JsonElement element, string property) =>
            element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String && value.GetString()!.Trim() is { Length: > 0 } text ? text : null;

        private static int? Int(JsonElement element, string property) =>
            element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : null;

        private static bool? Bool(JsonElement element, string property) =>
            element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;
    }
}
