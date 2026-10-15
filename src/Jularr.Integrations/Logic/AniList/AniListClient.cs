using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Jularr.Integrations.Data.AniList;
using Jularr.Integrations.Data.Common;

namespace Jularr.Integrations.Logic.AniList
{
    // AniList: keyless anime and manga metadata through its public GraphQL endpoint.
    internal sealed partial class AniListClient(HttpClient http)
    {
        private static readonly TimeSpan Spacing = TimeSpan.FromMilliseconds(700);
        private static readonly SemaphoreSlim Pace = new(1, 1);
        private static DateTimeOffset _last = DateTimeOffset.MinValue;
        private static long _blockedUntilTicks;

        private const string Fields =
            "id type format title { romaji english native } synonyms startDate { year } episodes volumes description(asHtml: false) coverImage { large } bannerImage trailer { id site } "
            + "staff(perPage: 8, sort: RELEVANCE) { edges { role node { id name { full } } } } relations { edges { relationType node { id type } } }";

        public async Task<IReadOnlyList<AniListMedia>> SearchAsync(AniListMediaType type, IReadOnlyCollection<string> formats, string query, CancellationToken ct)
        {
            using var document = await PostAsync("query ($search: String, $type: MediaType, $formats: [MediaFormat]) { Page(perPage: 20) { media(search: $search, type: $type, format_in: $formats) { "
                + Fields + " } } }", new Dictionary<string, object> { ["search"] = query, ["type"] = Name(type), ["formats"] = formats }, ct);
            return Child(Child(document.RootElement, "data"), "Page") is { } page && page.TryGetProperty("media", out var media) && media.ValueKind == JsonValueKind.Array
                ? [.. media.EnumerateArray().Select(Read).OfType<AniListMedia>()]
                : [];
        }

        public async Task<IReadOnlyList<AniListMedia>> ReadRankedAsync(AniListMediaType type, IReadOnlyCollection<string> formats, AniListRanking ranking, CancellationToken ct)
        {
            var (sort, statuses) = ranking switch
            {
                AniListRanking.Trending => ("TRENDING_DESC", new[] { "RELEASING", "FINISHED", "NOT_YET_RELEASED", "HIATUS" }),
                AniListRanking.Score => ("SCORE_DESC", ["RELEASING", "FINISHED", "HIATUS"]),
                _ => ("START_DATE_DESC", ["RELEASING", "FINISHED"])
            };
            using var document = await PostAsync("query ($type: MediaType, $formats: [MediaFormat], $sort: [MediaSort], $statuses: [MediaStatus]) { Page(perPage: 20) { "
                + "media(type: $type, format_in: $formats, sort: $sort, status_in: $statuses, isAdult: false) { " + Fields + " } } }",
                new Dictionary<string, object> { ["type"] = Name(type), ["formats"] = formats, ["sort"] = new[] { sort }, ["statuses"] = statuses }, ct);
            return Child(Child(document.RootElement, "data"), "Page") is { } page && page.TryGetProperty("media", out var media) && media.ValueKind == JsonValueKind.Array
                ? [.. media.EnumerateArray().Select(Read).OfType<AniListMedia>()]
                : [];
        }

        public async Task<AniListMedia> ReadAsync(AniListMediaType type, int id, CancellationToken ct)
        {
            using var document = await PostAsync("query ($id: Int, $type: MediaType) { Media(id: $id, type: $type) { " + Fields + " } }",
                new Dictionary<string, object> { ["id"] = id, ["type"] = Name(type) }, ct);
            return Child(Child(document.RootElement, "data"), "Media") is { } media && Read(media) is { } result
                ? result
                : throw new AniListException(AniListFailure.NotFound, "AniList does not know this title.");
        }

        private static string Name(AniListMediaType type) => type == AniListMediaType.Anime ? "ANIME" : "MANGA";

        private static AniListMedia? Read(JsonElement media)
        {
            if (media.ValueKind != JsonValueKind.Object || !media.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.Number || !id.TryGetInt32(out var mediaId)
                || Child(media, "title") is not { } title || Text(title, "romaji") is not { } romaji)
            {
                return null;
            }

            // Story and art staff name the work like book authors; sequels of the same type continue it.
            var people = Child(media, "staff") is { } staff && staff.TryGetProperty("edges", out var edges) && edges.ValueKind == JsonValueKind.Array
                ? edges.EnumerateArray()
                    .Select(edge => Text(edge, "role") is { } role && (role.Contains("Story", StringComparison.OrdinalIgnoreCase) || role.Contains("Art", StringComparison.OrdinalIgnoreCase))
                        && Child(edge, "node") is { } node && Child(node, "name") is { } name && Text(name, "full") is { } full
                            ? new ProviderPerson(node.TryGetProperty("id", out var staffId) && staffId.ValueKind == JsonValueKind.Number ? staffId.GetInt32().ToString(CultureInfo.InvariantCulture) : null,
                                full, role.Contains("Story", StringComparison.OrdinalIgnoreCase) ? ProviderPerson.Author : ProviderPerson.Illustrator, role)
                            : null)
                    .OfType<ProviderPerson>().DistinctBy(person => person.Id ?? person.Name).Take(3).ToArray()
                : [];
            var authors = people.Select(person => person.Name).ToArray();
            var sequels = Related(media, "SEQUEL");
            var prequels = Related(media, "PREQUEL");
            var synonyms = media.TryGetProperty("synonyms", out var names) && names.ValueKind == JsonValueKind.Array
                ? names.EnumerateArray().Where(name => name.ValueKind == JsonValueKind.String).Select(name => name.GetString()!.Trim()).Where(name => name.Length > 0).Take(20).ToArray()
                : [];
            var year = Child(media, "startDate") is { } start && start.TryGetProperty("year", out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt32() : (int?)null;
            var description = Text(media, "description") is { } text ? Markup().Replace(text, " ").Trim() : null;
            var cover = Child(media, "coverImage") is { } image ? Text(image, "large") : null;
            var banner = Text(media, "bannerImage");

            // Only a YouTube id of YouTube's shape is kept.
            var trailer = Child(media, "trailer") is { } video && Text(video, "site") == "youtube" && Text(video, "id") is { Length: 11 } key
                && key.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_') ? key : null;
            return new AniListMedia(mediaId, Text(media, "format"), romaji, Text(title, "english"), Text(title, "native"), synonyms, year, Count(media, "episodes", 5000),
                Count(media, "volumes", 500), description, Https(cover), authors, sequels, prequels, Https(banner), trailer) { People = people };
        }

        private static string? Https(string? url) => url is not null && url.StartsWith("https://", StringComparison.Ordinal) ? url : null;

        // Entries of the same type before or after this one.
        private static int[] Related(JsonElement media, string relationType) =>
            Child(media, "relations") is { } relations && relations.TryGetProperty("edges", out var links) && links.ValueKind == JsonValueKind.Array
                ? links.EnumerateArray()
                    .Where(link => Text(link, "relationType") == relationType && Child(link, "node") is { } node && Text(node, "type") == Type(media))
                    .Select(link => Child(link, "node")!.Value.TryGetProperty("id", out var related) && related.TryGetInt32(out var relatedId) ? relatedId : 0)
                    .Where(relatedId => relatedId > 0).Distinct().ToArray()
                : [];

        private async Task<JsonDocument> PostAsync(string query, Dictionary<string, object> variables, CancellationToken ct)
        {
            // A rate limit holds every caller until AniList allows requests again.
            if (Interlocked.Read(ref _blockedUntilTicks) > DateTimeOffset.UtcNow.UtcTicks)
            {
                throw new AniListException(AniListFailure.RateLimited, "AniList limits requests.");
            }

            HttpResponseMessage response;
            await Pace.WaitAsync(ct);
            try
            {
                var wait = _last + Spacing - DateTimeOffset.UtcNow;
                if (wait > TimeSpan.Zero)
                {
                    await Task.Delay(wait, ct);
                }

                _last = DateTimeOffset.UtcNow;
                response = await http.PostAsJsonAsync(http.BaseAddress, new Dictionary<string, object> { ["query"] = query, ["variables"] = variables }, ct);
            }
            catch (Exception exception) when (exception is HttpRequestException || exception is TaskCanceledException && !ct.IsCancellationRequested)
            {
                throw new AniListException(AniListFailure.Unavailable, "AniList could not be reached.", exception);
            }
            finally
            {
                Pace.Release();
            }

            using (response)
            {
                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    throw new AniListException(AniListFailure.NotFound, "AniList does not know this title.");
                }

                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    var delay = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromMinutes(1);
                    var until = (DateTimeOffset.UtcNow + (delay > TimeSpan.FromMinutes(5) ? TimeSpan.FromMinutes(5) : delay)).UtcTicks;
                    for (var current = Interlocked.Read(ref _blockedUntilTicks); current < until; current = Interlocked.Read(ref _blockedUntilTicks))
                    {
                        if (Interlocked.CompareExchange(ref _blockedUntilTicks, until, current) == current)
                        {
                            break;
                        }
                    }

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

        private static string? Type(JsonElement media) => Text(media, "type");

        private static string? Text(JsonElement element, string property) =>
            element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String && value.GetString()!.Trim() is { Length: > 0 } text
                ? text
                : null;

        private static int? Count(JsonElement element, string property, int maximum) =>
            element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var count) && count > 0 && count <= maximum ? count : null;

        [GeneratedRegex("<[^>]*>")]
        private static partial Regex Markup();
    }
}
