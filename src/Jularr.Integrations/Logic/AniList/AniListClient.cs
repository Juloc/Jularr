using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Jularr.Integrations.Data.AniList;

namespace Jularr.Integrations.Logic.AniList
{
    // AniList: keyless Manga metadata through its public GraphQL endpoint.
    internal sealed partial class AniListClient(HttpClient http)
    {
        private static readonly Uri Endpoint = new("https://graphql.anilist.co");

        private const string Fields =
            "id title { romaji english native } startDate { year } volumes description(asHtml: false) coverImage { large } "
            + "staff(perPage: 8, sort: RELEVANCE) { edges { role node { name { full } } } }";

        public async Task<IReadOnlyList<AniListManga>> SearchAsync(string query, CancellationToken ct)
        {
            using var document = await PostAsync("query ($search: String) { Page(perPage: 20) { media(search: $search, type: MANGA, format_in: [MANGA, ONE_SHOT]) { " + Fields + " } } }",
                new Dictionary<string, object> { ["search"] = query }, ct);
            return Child(Child(document.RootElement, "data"), "Page") is { } page && page.TryGetProperty("media", out var media) && media.ValueKind == JsonValueKind.Array
                ? [.. media.EnumerateArray().Select(Read).OfType<AniListManga>()]
                : [];
        }

        public async Task<AniListManga> ReadAsync(int id, CancellationToken ct)
        {
            using var document = await PostAsync("query ($id: Int) { Media(id: $id, type: MANGA) { " + Fields + " } }", new Dictionary<string, object> { ["id"] = id }, ct);
            return Child(Child(document.RootElement, "data"), "Media") is { } media && Read(media) is { } manga
                ? manga
                : throw new AniListException(AniListFailure.NotFound, "AniList does not know this Manga.");
        }

        private static AniListManga? Read(JsonElement media)
        {
            if (media.ValueKind != JsonValueKind.Object || !media.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.Number || !id.TryGetInt32(out var mediaId)
                || Child(media, "title") is not { } title || Text(title, "romaji") is not { } romaji)
            {
                return null;
            }

            // Story and art staff name the work like book authors.
            var authors = Child(media, "staff") is { } staff && staff.TryGetProperty("edges", out var edges) && edges.ValueKind == JsonValueKind.Array
                ? edges.EnumerateArray()
                    .Where(edge => Text(edge, "role") is { } role && (role.Contains("Story", StringComparison.OrdinalIgnoreCase) || role.Contains("Art", StringComparison.OrdinalIgnoreCase)))
                    .Select(edge => Child(Child(edge, "node"), "name") is { } name ? Text(name, "full") : null)
                    .OfType<string>().Distinct(StringComparer.Ordinal).Take(3).ToArray()
                : [];
            var year = Child(media, "startDate") is { } start && start.TryGetProperty("year", out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt32() : (int?)null;
            var volumes = media.TryGetProperty("volumes", out var count) && count.ValueKind == JsonValueKind.Number && count.GetInt32() is > 0 and <= 500 ? count.GetInt32() : (int?)null;
            var description = Text(media, "description") is { } text ? Markup().Replace(text, " ").Trim() : null;
            var cover = Child(media, "coverImage") is { } image ? Text(image, "large") : null;
            return new AniListManga(mediaId, romaji, Text(title, "english"), Text(title, "native"), year, volumes, description,
                cover is not null && cover.StartsWith("https://", StringComparison.Ordinal) ? cover : null, authors);
        }

        private async Task<JsonDocument> PostAsync(string query, Dictionary<string, object> variables, CancellationToken ct)
        {
            HttpResponseMessage response;
            try
            {
                response = await http.PostAsJsonAsync(Endpoint, new Dictionary<string, object> { ["query"] = query, ["variables"] = variables }, ct);
            }
            catch (Exception exception) when (exception is HttpRequestException || exception is TaskCanceledException && !ct.IsCancellationRequested)
            {
                throw new AniListException(AniListFailure.Unavailable, "AniList could not be reached.", exception);
            }

            using (response)
            {
                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    throw new AniListException(AniListFailure.NotFound, "AniList does not know this Manga.");
                }

                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    throw new AniListException(AniListFailure.RateLimited, "AniList limits requests.");
                }

                if (!response.IsSuccessStatusCode)
                {
                    throw new AniListException(AniListFailure.Unavailable, "AniList failed.");
                }

                try
                {
                    await using var stream = await response.Content.ReadAsStreamAsync(ct);
                    var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
                    if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
                    {
                        document.Dispose();
                        throw new AniListException(AniListFailure.InvalidResponse, "AniList returned no data.");
                    }

                    return document;
                }
                catch (JsonException exception)
                {
                    throw new AniListException(AniListFailure.InvalidResponse, "AniList returned invalid JSON.", exception);
                }
                catch (Exception exception) when (exception is HttpRequestException or IOException || exception is TaskCanceledException && !ct.IsCancellationRequested)
                {
                    throw new AniListException(AniListFailure.Unavailable, "AniList stopped answering.", exception);
                }
            }
        }

        // JSON null fields count as missing.
        private static JsonElement? Child(JsonElement? element, string property) =>
            element is { ValueKind: JsonValueKind.Object } parent && parent.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Object ? value : null;

        private static string? Text(JsonElement element, string property) =>
            element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String && value.GetString()!.Trim() is { Length: > 0 } text
                ? text
                : null;

        [GeneratedRegex("<[^>]*>")]
        private static partial Regex Markup();
    }
}
