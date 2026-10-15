using System.Globalization;
using System.Net;
using System.Text.Json;
using Jularr.Integrations.Data.Servarr;

namespace Jularr.Integrations.Logic.Servarr
{
    // Reads Prowlarr, Sonarr, Radarr, Lidarr and Readarr once for a takeover; Jularr never runs alongside them.
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
                var key = FieldText(fields, "apiKey");
                return new ServarrIndexer(Text(item, "name") ?? baseUrl, baseUrl, Secret(key), Bool(item, "enable") ?? Bool(item, "enableRss") ?? true,
                    Math.Clamp(Int(item, "priority") ?? 25, 1, 100), Categories(fields)) { HasHiddenKey = key is not null && Secret(key) is null };
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
                    FieldText(fields, "movieCategory") ?? FieldText(fields, "tvCategory") ?? FieldText(fields, "musicCategory") ?? FieldText(fields, "bookCategory"));
            }).Where(client => Uri.TryCreate(client.BaseUrl, UriKind.Absolute, out var uri) && uri.Host.Length > 0)];
        }

        public async Task<IReadOnlyList<ServarrTitle>> ReadMoviesAsync(ServarrConnection connection, CancellationToken ct)
        {
            using var document = await GetAsync(connection, "movie", ct);
            return [.. Items(document).Select(item => new ServarrTitle(Text(item, "title") ?? "", Int(item, "year"), Int(item, "tmdbId") is > 0 and var id ? id : null, Bool(item, "monitored") ?? false,
                Bool(item, "hasFile") == false, qualityProfileId: Int(item, "qualityProfileId")) { Path = Text(item, "path") }).Where(title => title.Title.Length > 0)];
        }

        public async Task<IReadOnlyList<ServarrTitle>> ReadSeriesAsync(ServarrConnection connection, CancellationToken ct)
        {
            // A series misses something while aired episodes have no file.
            using var document = await GetAsync(connection, "series", ct);
            return [.. Items(document).Select(item =>
            {
                var statistics = item.TryGetProperty("statistics", out var value) && value.ValueKind == JsonValueKind.Object ? value : default;
                var missing = statistics.ValueKind == JsonValueKind.Object && (Int(statistics, "episodeFileCount") ?? 0) < (Int(statistics, "episodeCount") ?? 0);
                return new ServarrTitle(Text(item, "title") ?? "", Int(item, "year"), Int(item, "tmdbId") is > 0 and var id ? id : null, Bool(item, "monitored") ?? false, missing,
                    qualityProfileId: Int(item, "qualityProfileId")) { Path = Text(item, "path") };
            }).Where(title => title.Title.Length > 0)];
        }

        public async Task<IReadOnlyList<ServarrTitle>> ReadAlbumsAsync(ServarrConnection connection, CancellationToken ct)
        {
            // An album misses something while it has fewer track files than tracks.
            using var document = await GetAsync(connection, "album", ct);
            return [.. Items(document).Select(item =>
            {
                var statistics = Child(item, "statistics");
                var missing = (Int(statistics, "trackFileCount") ?? 0) < (Int(statistics, "trackCount") ?? Int(statistics, "totalTrackCount") ?? 1);
                return new ServarrTitle(Text(item, "title") ?? "", Year(Text(item, "releaseDate")), null, Bool(item, "monitored") ?? false, missing, Text(item, "foreignAlbumId"),
                    Text(Child(item, "artist"), "artistName"), Int(Child(item, "artist"), "qualityProfileId"))
                {
                    Path = Text(Child(item, "artist"), "path"),
                    AlbumType = Text(item, "albumType"),
                    MetadataProfileId = Int(Child(item, "artist"), "metadataProfileId")
                };
            }).Where(title => title.Title.Length > 0)];
        }

        public async Task<IReadOnlyList<ServarrTitle>> ReadBooksAsync(ServarrConnection connection, CancellationToken ct)
        {
            // Readarr's ids are its own catalog's; the title and author find the book again.
            using var document = await GetAsync(connection, "book", ct);
            return [.. Items(document).Select(item => new ServarrTitle(Text(item, "title") ?? "", Year(Text(item, "releaseDate")), null, Bool(item, "monitored") ?? false,
                (Int(Child(item, "statistics"), "bookFileCount") ?? 0) == 0, null, Text(Child(item, "author"), "authorName"), Int(Child(item, "author"), "qualityProfileId"))
            {
                Path = Text(Child(item, "author"), "path"),
                MetadataProfileId = Int(Child(item, "author"), "metadataProfileId")
            }).Where(title => title.Title.Length > 0)];
        }

        // Quality profiles; groups pass their allowance to their qualities. Sonarr and Radarr describe a quality by source and resolution, Lidarr and Readarr by its name.
        public async Task<IReadOnlyList<ServarrQualityProfile>> ReadQualityProfilesAsync(ServarrConnection connection, CancellationToken ct)
        {
            using var document = await GetAsync(connection, "qualityprofile", ct);
            return [.. Items(document).Where(item => Int(item, "id") is not null && Text(item, "name") is not null).Select(item =>
            {
                var cutoff = Int(item, "cutoff");
                var qualities = new List<ServarrQuality>();
                foreach (var entry in Array(item, "items"))
                {
                    var allowed = Bool(entry, "allowed") ?? false;
                    var members = Array(entry, "items").ToArray();
                    var groupIsCutoff = members.Length > 0 && Int(entry, "id") == cutoff;
                    foreach (var member in members.Length > 0 ? members : [entry])
                    {
                        var quality = Child(member, "quality");
                        if (quality.ValueKind != JsonValueKind.Object) continue;
                        qualities.Add(new ServarrQuality(Text(quality, "source"), Int(quality, "resolution") ?? 0, allowed || Bool(member, "allowed") == true && members.Length == 0,
                            groupIsCutoff || members.Length == 0 && Int(quality, "id") == cutoff, Text(quality, "name")));
                    }
                }

                var scores = new Dictionary<int, int>();
                foreach (var format in Array(item, "formatItems"))
                {
                    if (Int(format, "format") is { } id && Int(format, "score") is { } score) scores[id] = score;
                }

                return new ServarrQualityProfile(Int(item, "id")!.Value, Text(item, "name")!, Bool(item, "upgradeAllowed") ?? false, qualities, scores, Int(item, "minFormatScore") ?? 0);
            })];
        }

        // Lidarr's allowed release types and statuses, Readarr's languages and least pages.
        public async Task<IReadOnlyList<ServarrMetadataProfile>> ReadMetadataProfilesAsync(ServarrConnection connection, CancellationToken ct)
        {
            using var document = await GetAsync(connection, "metadataprofile", ct);
            return [.. Items(document).Where(item => Int(item, "id") is not null && Text(item, "name") is not null).Select(item => new ServarrMetadataProfile(Int(item, "id")!.Value,
                Text(item, "name")!, Allowed(item, "primaryAlbumTypes", "albumType"), Allowed(item, "secondaryAlbumTypes", "albumType"), Allowed(item, "releaseStatuses", "releaseStatus"),
                [.. (Text(item, "allowedLanguages") ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                    .Where(code => code.Length == 3 && code.All(char.IsAsciiLetterLower)).Take(20)],
                Int(item, "minPages") is > 0 and var pages ? pages : null))];
        }

        public async Task<IReadOnlyList<ServarrRootFolder>> ReadRootFoldersAsync(ServarrConnection connection, CancellationToken ct)
        {
            using var document = await GetAsync(connection, "rootfolder", ct);
            return [.. Items(document).Select(item => Text(item, "path") is { } path
                ? new ServarrRootFolder(path, Int(item, "defaultQualityProfileId"), Int(item, "defaultMetadataProfileId"))
                : null).OfType<ServarrRootFolder>().Take(50)];
        }

        private static string[] Allowed(JsonElement item, string list, string entry) =>
            [.. Array(item, list).Where(value => Bool(value, "allowed") == true).Select(value => Text(Child(value, entry), "name")).OfType<string>().Take(30)];

        public async Task<IReadOnlyList<ServarrCustomFormat>> ReadCustomFormatsAsync(ServarrConnection connection, CancellationToken ct)
        {
            using var document = await GetAsync(connection, "customformat", ct);
            return [.. Items(document).Where(item => Int(item, "id") is not null && Text(item, "name") is not null).Select(item => new ServarrCustomFormat(Int(item, "id")!.Value, Text(item, "name")!,
                [.. Array(item, "specifications").Select(condition => new ServarrFormatCondition(Text(condition, "implementation") ?? "", Bool(condition, "negate") ?? false,
                    Bool(condition, "required") ?? false, Fields(condition).TryGetValue("value", out var value) ? value.ValueKind switch
                    {
                        JsonValueKind.String => value.GetString(),
                        JsonValueKind.Number => value.GetRawText(),
                        _ => null
                    } : null))]))];
        }

        private static IEnumerable<JsonElement> Array(JsonElement element, string property) =>
            element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Array
                ? value.EnumerateArray().Where(entry => entry.ValueKind == JsonValueKind.Object).Take(1000)
                : [];

        private static JsonElement Child(JsonElement element, string property) =>
            element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Object ? value : default;

        private static int? Year(string? date) => date is { Length: >= 4 } && int.TryParse(date[..4], NumberStyles.None, CultureInfo.InvariantCulture, out var year) && year is >= 1800 and <= 2200 ? year : null;

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
