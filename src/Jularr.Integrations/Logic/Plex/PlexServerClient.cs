using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using Jularr.Integrations.Data.Plex;

namespace Jularr.Integrations.Logic.Plex
{
    // The owner's Plex Media Server: libraries, contents and scans.
    internal sealed partial class PlexServerClient(HttpClient http)
    {
        public const int PageSize = 200;

        public async Task<PlexServerInfo> ReadServerAsync(PlexServerConnection connection, CancellationToken ct)
        {
            // The identity proves the server; the sections prove the token.
            using var identity = await SendAsync(connection, "identity", ct);
            var machineIdentifier = ReadString(Container(identity), "machineIdentifier") ?? throw Invalid("The Plex identity is missing.");
            var version = ReadString(Container(identity), "version");

            using var sections = await SendAsync(connection, "library/sections", ct);
            var found = new List<PlexSection>();
            foreach (var directory in Array(Container(sections), "Directory"))
            {
                var kind = ReadString(directory, "type") switch
                {
                    "movie" => PlexSectionKind.Movie,
                    "show" => PlexSectionKind.Show,
                    _ => (PlexSectionKind?)null
                };
                if (kind is { } sectionKind && ReadString(directory, "key") is { } key && SectionKey().IsMatch(key))
                {
                    found.Add(new PlexSection(key, sectionKind, ReadString(directory, "title") ?? key));
                }
            }

            return new PlexServerInfo(machineIdentifier, version, found);
        }

        public async Task<PlexItemPage> ReadItemsAsync(PlexServerConnection connection, PlexSection section, int start, CancellationToken ct)
        {
            // Provider ids come with includeGuids; items without one cannot be matched.
            using var document = await SendAsync(connection, FormattableString.Invariant(
                $"library/sections/{section.Key}/all?includeGuids=1&X-Plex-Container-Start={start}&X-Plex-Container-Size={PageSize}"), ct);
            var container = Container(document);
            var items = new List<PlexItem>();
            foreach (var metadata in Array(container, "Metadata"))
            {
                if (ReadString(metadata, "ratingKey") is not { } ratingKey || !SectionKey().IsMatch(ratingKey) || ReadString(metadata, "title") is not { } title)
                {
                    continue;
                }

                string? tmdb = null, imdb = null, tvdb = null;
                foreach (var guid in Array(metadata, "Guid"))
                {
                    var id = ReadString(guid, "id");
                    if (id?.StartsWith("tmdb://", StringComparison.Ordinal) == true) tmdb = Identifier(id[7..]);
                    else if (id?.StartsWith("imdb://", StringComparison.Ordinal) == true) imdb = Identifier(id[7..]);
                    else if (id?.StartsWith("tvdb://", StringComparison.Ordinal) == true) tvdb = Identifier(id[7..]);
                }

                var year = metadata.TryGetProperty("year", out var value) && value.TryGetInt32(out var number) && number is >= 1800 and <= 2200 ? number : (int?)null;
                items.Add(new PlexItem(ratingKey, title.Length > 500 ? title[..500] : title, year, tmdb, imdb, tvdb));
            }

            var total = container.TryGetProperty("totalSize", out var size) && size.TryGetInt32(out var count) ? count : start + items.Count;
            return new PlexItemPage(items, total);
        }

        public async Task RefreshSectionAsync(PlexServerConnection connection, PlexSection section, CancellationToken ct)
        {
            using var response = await RequestAsync(connection, "library/sections/" + section.Key + "/refresh", ct);
        }

        private async Task<JsonDocument> SendAsync(PlexServerConnection connection, string path, CancellationToken ct)
        {
            using var response = await RequestAsync(connection, path, ct);
            try
            {
                await using var stream = await response.Content.ReadAsStreamAsync(ct);
                return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            }
            catch (JsonException exception)
            {
                throw new PlexServerException(PlexServerFailure.InvalidResponse, "Plex returned invalid JSON.", exception);
            }
        }

        private async Task<HttpResponseMessage> RequestAsync(PlexServerConnection connection, string path, CancellationToken ct)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(connection.BaseUrl, path));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.Add("X-Plex-Token", connection.Token);
            request.Headers.Add("X-Plex-Product", "Jularr");
            request.Headers.Add("X-Plex-Client-Identifier", "jularr-server");
            HttpResponseMessage response;
            try
            {
                response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            }
            catch (Exception exception) when (exception is HttpRequestException || exception is TaskCanceledException && !ct.IsCancellationRequested)
            {
                throw new PlexServerException(PlexServerFailure.Unavailable, "Plex could not be reached.", exception);
            }

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                response.Dispose();
                throw new PlexServerException(PlexServerFailure.Unauthorized, "Plex rejected the token.");
            }

            if (!response.IsSuccessStatusCode)
            {
                response.Dispose();
                throw new PlexServerException(PlexServerFailure.Unavailable, "Plex answered " + ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture) + ".");
            }

            return response;
        }

        private static JsonElement Container(JsonDocument document) =>
            document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("MediaContainer", out var container) && container.ValueKind == JsonValueKind.Object
                ? container
                : throw Invalid("The Plex response has no MediaContainer.");

        private static IEnumerable<JsonElement> Array(JsonElement element, string name) =>
            element.TryGetProperty(name, out var values) && values.ValueKind == JsonValueKind.Array ? values.EnumerateArray().Where(value => value.ValueKind == JsonValueKind.Object).ToArray() : [];

        private static string? ReadString(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) ? value.ValueKind switch
            {
                JsonValueKind.String => string.IsNullOrWhiteSpace(value.GetString()) ? null : value.GetString()!.Trim(),
                JsonValueKind.Number => value.GetRawText(),
                _ => null
            } : null;

        private static string? Identifier(string value) => value.Length is > 0 and <= 64 && value.All(character => char.IsAsciiLetterOrDigit(character)) ? value : null;

        private static PlexServerException Invalid(string message) => new(PlexServerFailure.InvalidResponse, message);

        [GeneratedRegex("^[0-9]{1,12}$")]
        private static partial Regex SectionKey();
    }
}
