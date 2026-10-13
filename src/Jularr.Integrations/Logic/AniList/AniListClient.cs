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
            "id title { romaji english native } synonyms startDate { year } volumes status description(asHtml: false) coverImage { large } "
            + "staff(perPage: 8, sort: RELEVANCE) { edges { role node { name { full } } } }";

        public async Task<IReadOnlyList<AniListManga>> SearchAsync(string query, CancellationToken ct)
        {
            using var document = await PostAsync("query ($search: String) { Page(perPage: 20) { media(search: $search, type: MANGA, format_in: [MANGA, ONE_SHOT]) { " + Fields + " } } }",
                new Dictionary<string, object> { ["search"] = query }, ct);
            return document.RootElement.GetProperty("data").TryGetProperty("Page", out var page) && page.TryGetProperty("media", out var media) && media.ValueKind == JsonValueKind.Array
                ? [.. media.EnumerateArray().Select(Read).OfType<AniListManga>()]
                : [];
        }

        public async Task<AniListManga> ReadAsync(int id, CancellationToken ct)
        {
            using var document = await PostAsync("query ($id: Int) { Media(id: $id, type: MANGA) { " + Fields + " } }", new Dictionary<string, object> { ["id"] = id }, ct);
            return document.RootElement.GetProperty("data").TryGetProperty("Media", out var media) && Read(media) is { } manga
                ? manga
                : throw new AniListException(AniListFailure.NotFound, "AniList does not know this Manga.");
        }

        private static AniListManga? Read(JsonElement media)
        {
            if (media.ValueKind != JsonValueKind.Object || !media.TryGetProperty("id", out var id) || !id.TryGetInt32(out var mediaId)
                || !media.TryGetProperty("title", out var title) || Text(title, "romaji") is not { } romaji)
            {
                return null;
            }

            // Story and art staff name the work like book authors.
            var authors = media.TryGetProperty("staff", out var staff) && staff.TryGetProperty("edges", out var edges) && edges.ValueKind == JsonValueKind.Array
                ? edges.EnumerateArray()
                    .Where(edge => Text(edge, "role") is { } role && (role.Contains("Story", StringComparison.OrdinalIgnoreCase) || role.Contains("Art", StringComparison.OrdinalIgnoreCase)))
                    .Select(edge => edge.TryGetProperty("node", out var node) && node.TryGetProperty("name", out var name) ? Text(name, "full") : null)
                    .OfType<string>().Distinct(StringComparer.Ordinal).Take(3).ToArray()
                : [];
            var synonyms = media.TryGetProperty("synonyms", out var names) && names.ValueKind == JsonValueKind.Array
                ? names.EnumerateArray().Where(name => name.ValueKind == JsonValueKind.String).Select(name => name.GetString()!.Trim()).Where(name => name.Length is > 0 and <= 500).Take(10).ToArray()
                : [];
            var year = media.TryGetProperty("startDate", out var start) && start.TryGetProperty("year", out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt32() : (int?)null;
            var volumes = media.TryGetProperty("volumes", out var count) && count.ValueKind == JsonValueKind.Number && count.GetInt32() is > 0 and <= 500 ? count.GetInt32() : (int?)null;
            var description = Text(media, "description") is { } text ? Markup().Replace(text, " ").Trim() : null;
            var cover = media.TryGetProperty("coverImage", out var image) ? Text(image, "large") : null;
            return new AniListManga(mediaId, romaji, Text(title, "english"), Text(title, "native"), synonyms, year, volumes, Text(media, "status"), description,
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
            }
        }

        private static string? Text(JsonElement element, string property) =>
            element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String && value.GetString()!.Trim() is { Length: > 0 } text ? text : null;

        [GeneratedRegex("<[^>]*>")]
        private static partial Regex Markup();
    }
}
