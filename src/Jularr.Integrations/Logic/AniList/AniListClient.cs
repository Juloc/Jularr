using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Jularr.Integrations.Data.AniList;
using Jularr.Integrations.Data.Common;
using Jularr.Integrations.Logic.Common;

namespace Jularr.Integrations.Logic.AniList
{
    // AniList: keyless anime and manga metadata through its public GraphQL endpoint.
    internal sealed partial class AniListClient(HttpClient http, RequestPace<AniListClient>? pace = null)
    {
        public static readonly TimeSpan DefaultSpacing = TimeSpan.FromMilliseconds(700);
        private readonly RequestPace<AniListClient> _pace = pace ?? new(DefaultSpacing);
        private static long _blockedUntilTicks;

        private const string Fields =
            "id type format countryOfOrigin title { romaji english native } synonyms startDate { year } episodes volumes description(asHtml: false) coverImage { large } bannerImage trailer { id site } "
            + "staff(perPage: 8, sort: RELEVANCE) { edges { role node { id name { full native } } } } relations { edges { relationType node { id type format title { romaji english } startDate { year } coverImage { large } } } }";

        public async Task<IReadOnlyList<AniListMedia>> SearchAsync(AniListMediaType type, IReadOnlyCollection<string> formats, string query, CancellationToken ct)
        {
            using var document = await PostAsync("query ($search: String, $type: MediaType, $formats: [MediaFormat]) { Page(perPage: 20) { media(search: $search, type: $type, format_in: $formats) { "
                + Fields + " } } }", new Dictionary<string, object> { ["search"] = query, ["type"] = Name(type), ["formats"] = formats }, ct);
            return Child(Child(document.RootElement, "data"), "Page") is { } page && page.TryGetProperty("media", out var media) && media.ValueKind == JsonValueKind.Array
                ? [.. media.EnumerateArray().Select(Read).OfType<AniListMedia>()]
                : [];
        }

        // A ranked list page by page; adult titles stay out unless asked for, and AniList ignores a narrowing left empty.
        public async Task<IReadOnlyList<AniListMedia>> ReadRankedAsync(AniListMediaType type, IReadOnlyCollection<string> formats, AniListListQuery query, int page, CancellationToken ct)
        {
            var sort = query.Sort switch
            {
                AniListSort.Trending => "TRENDING_DESC",
                AniListSort.Popularity => "POPULARITY_DESC",
                AniListSort.Score => "SCORE_DESC",
                AniListSort.Favourites => "FAVOURITES_DESC",
                AniListSort.Added => "ID_DESC",
                AniListSort.StartDate => "START_DATE_DESC",
                _ => "TITLE_ROMAJI"
            };
            var variables = new Dictionary<string, object> { ["page"] = page, ["type"] = Name(type), ["formats"] = formats, ["sort"] = new[] { sort } };
            if (query.Season is { } season) variables["season"] = season;
            if (query.SeasonYear is { } year) variables["seasonYear"] = year;
            if (query.Country is { } country) variables["country"] = country;
            if (!query.IncludesAdult) variables["adult"] = false;
            if (query.Genres.Count > 0) variables["genres"] = query.Genres;
            if (query.Tags.Count > 0) variables["tags"] = query.Tags;
            if (query.Statuses.Count > 0) variables["statuses"] = query.Statuses;
            if (query.Sources.Count > 0) variables["sources"] = query.Sources;
            if (query.SiteIds.Count > 0) variables["sites"] = query.SiteIds;

            // AniList compares dates (yyyymmdd) and counts without their ends.
            if (query.YearFrom is { } from) variables["startAfter"] = from * 10000;
            if (query.YearTo is { } to) variables["startBefore"] = (to + 1) * 10000;
            foreach (var (name, range) in new[] { ("episodes", query.Episodes), ("chapters", query.Chapters), ("volumes", query.Volumes), ("duration", query.Duration) })
            {
                if (range.Minimum is { } minimum) variables[name + "Above"] = minimum - 1;
                if (range.Maximum is { } maximum) variables[name + "Below"] = maximum + 1;
            }

            using var document = await PostAsync("query ($page: Int, $type: MediaType, $formats: [MediaFormat], $sort: [MediaSort], $season: MediaSeason, $seasonYear: Int, $country: CountryCode, "
                + "$adult: Boolean, $genres: [String], $tags: [String], $statuses: [MediaStatus], $sources: [MediaSource], $sites: [Int], $startAfter: FuzzyDateInt, $startBefore: FuzzyDateInt, "
                + "$episodesAbove: Int, $episodesBelow: Int, $chaptersAbove: Int, $chaptersBelow: Int, $volumesAbove: Int, $volumesBelow: Int, $durationAbove: Int, $durationBelow: Int) { "
                + "Page(page: $page, perPage: 20) { media(type: $type, format_in: $formats, sort: $sort, season: $season, seasonYear: $seasonYear, countryOfOrigin: $country, isAdult: $adult, "
                + "genre_in: $genres, tag_in: $tags, status_in: $statuses, source_in: $sources, licensedById_in: $sites, startDate_greater: $startAfter, startDate_lesser: $startBefore, "
                + "episodes_greater: $episodesAbove, episodes_lesser: $episodesBelow, chapters_greater: $chaptersAbove, chapters_lesser: $chaptersBelow, volumes_greater: $volumesAbove, "
                + "volumes_lesser: $volumesBelow, duration_greater: $durationAbove, duration_lesser: $durationBelow) { "
                + Fields + " } } }", variables, ct);
            return Child(Child(document.RootElement, "data"), "Page") is { } found && found.TryGetProperty("media", out var media) && media.ValueKind == JsonValueKind.Array
                ? [.. media.EnumerateArray().Select(Read).OfType<AniListMedia>()]
                : [];
        }

        // The names Discover's filter offers: AniList's tags and its streaming (anime) and reading (manga) sites.
        public async Task<AniListFilterOptions> ReadFilterOptionsAsync(CancellationToken ct)
        {
            using var document = await PostAsync("{ MediaTagCollection { id name category isAdult } anime: ExternalLinkSourceCollection(type: STREAMING, mediaType: ANIME) { id site } "
                + "manga: ExternalLinkSourceCollection(type: STREAMING, mediaType: MANGA) { id site } }", new Dictionary<string, object>(), ct);
            var data = Child(document.RootElement, "data");
            IEnumerable<JsonElement> Items(string name) => data is { } root && root.TryGetProperty(name, out var list) && list.ValueKind == JsonValueKind.Array ? list.EnumerateArray() : [];
            int? Id(JsonElement item) => item.TryGetProperty("id", out var id) && id.TryGetInt32(out var value) && value > 0 ? value : null;
            AniListOption[] Sites(string name) => [.. Items(name).Select(site => Id(site) is { } id && Text(site, "site") is { } label ? new AniListOption(id, label, null, false) : null).OfType<AniListOption>()];
            return new AniListFilterOptions(
                [.. Items("MediaTagCollection").Select(tag => Id(tag) is { } id && Text(tag, "name") is { } name
                    ? new AniListOption(id, name, Text(tag, "category"), tag.TryGetProperty("isAdult", out var adult) && adult.ValueKind == JsonValueKind.True) : null).OfType<AniListOption>()],
                Sites("anime"), Sites("manga"));
        }

        public async Task<AniListMedia> ReadAsync(AniListMediaType type, int id, CancellationToken ct)
        {
            using var document = await PostAsync("query ($id: Int, $type: MediaType) { Media(id: $id, type: $type) { " + Fields + " } }",
                new Dictionary<string, object> { ["id"] = id, ["type"] = Name(type) }, ct);
            return Child(Child(document.RootElement, "data"), "Media") is { } media && Read(media) is { } result
                ? result
                : throw new AniListException(AniListFailure.NotFound, "AniList does not know this title.");
        }

        // The fields of AniList's search filters.
        private const int CatalogPageSize = 50;

        private const string FactFields = "status source countryOfOrigin season seasonYear episodes chapters volumes duration genres isAdult tags { id } externalLinks { siteId type } ";

        // The titles of the next fifty ids, anime and manga alike, adult ones included, and the id to go on after; AniList pages no deeper than 5000 entries,
        // so a walk asks for ids, and its newest id says whether more follow.
        public async Task<(IReadOnlyList<AniListCatalogEntry> Entries, int NextAfterId, bool HasMore)> ReadCatalogPageAsync(int afterId, CancellationToken ct)
        {
            var ids = Enumerable.Range(afterId + 1, CatalogPageSize).ToArray();
            using var document = await PostAsync("query ($ids: [Int]) { Page(perPage: 50) { media(id_in: $ids, sort: ID) { "
                + "id type format title { romaji english native } startDate { year month day } description(asHtml: false) coverImage { large } popularity averageScore favourites " + FactFields
                + "} } last: Page(perPage: 1) { media(sort: ID_DESC) { id } } }",
                new Dictionary<string, object> { ["ids"] = ids }, ct);
            var data = Child(document.RootElement, "data");
            if (Child(data, "Page") is not { } page || !page.TryGetProperty("media", out var media) || media.ValueKind != JsonValueKind.Array
                || Child(data, "last") is not { } last || !last.TryGetProperty("media", out var newest) || newest.ValueKind != JsonValueKind.Array)
            {
                throw new AniListException(AniListFailure.InvalidResponse, "AniList did not answer with a page.");
            }

            var entries = media.EnumerateArray().Select(CatalogEntry).OfType<AniListCatalogEntry>().ToArray();
            var lastId = newest.EnumerateArray().Select(item => item.TryGetProperty("id", out var id) && id.TryGetInt32(out var value) ? value : 0).FirstOrDefault();
            return (entries, ids[^1], ids[^1] < lastId);
        }

        private static AniListCatalogEntry? CatalogEntry(JsonElement media)
        {
            if (!media.TryGetProperty("id", out var id) || !id.TryGetInt32(out var mediaId) || Text(media, "type") is not { } type
                || Child(media, "title") is not { } title || Text(title, "romaji") is not { } romaji)
            {
                return null;
            }

            int? Part(JsonElement date, string name) => date.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt32() : null;
            var start = Child(media, "startDate");
            var year = start is { } date ? Part(date, "year") : null;
            DateOnly? released = start is { } day && year is { } y && Part(day, "month") is { } m && Part(day, "day") is { } d && m is >= 1 and <= 12 && d >= 1 && d <= DateTime.DaysInMonth(y, m)
                ? new DateOnly(y, m, d) : null;
            var description = Text(media, "description") is { } text ? Markup().Replace(text, " ").Trim() : null;
            return new AniListCatalogEntry(mediaId, type, Text(media, "format"), Text(title, "english") ?? romaji, Text(title, "english") is null ? Text(title, "native") : romaji,
                released, year, description, Https(Child(media, "coverImage") is { } image ? Text(image, "large") : null), Count(media, "popularity", int.MaxValue), Count(media, "averageScore", 100),
                Count(media, "favourites", int.MaxValue), Text(media, "countryOfOrigin") is { Length: 2 } country ? country : null,
                Text(media, "season") is "WINTER" or "SPRING" or "SUMMER" or "FALL" ? Text(media, "season") : null, Count(media, "seasonYear", 2200), Facts(media));
        }

        private static AniListFacts Facts(JsonElement media)
        {
            IEnumerable<JsonElement> Items(string property) => media.TryGetProperty(property, out var list) && list.ValueKind == JsonValueKind.Array ? list.EnumerateArray() : [];
            int? Id(JsonElement item, string property) => item.ValueKind == JsonValueKind.Object && item.TryGetProperty(property, out var id) && id.TryGetInt32(out var value) && value > 0 ? value : null;

            // Only streaming links name a site to watch or read on.
            var genres = Items("genres").Where(genre => genre.ValueKind == JsonValueKind.String).Select(genre => genre.GetString()!).Distinct(StringComparer.Ordinal).ToArray();
            var tags = Items("tags").Select(tag => Id(tag, "id")).OfType<int>().Distinct().ToArray();
            var sites = Items("externalLinks").Where(link => Text(link, "type") == "STREAMING").Select(link => Id(link, "siteId")).OfType<int>().Distinct().ToArray();
            return new AniListFacts(Text(media, "status"), Text(media, "source"), Count(media, "episodes", 5000), Count(media, "chapters", 100000), Count(media, "volumes", 500),
                Count(media, "duration", 1000), genres, tags, sites, media.TryGetProperty("isAdult", out var adult) && adult.ValueKind == JsonValueKind.True);
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
            var nativeAuthors = Child(media, "staff") is { } credits && credits.TryGetProperty("edges", out var credited) && credited.ValueKind == JsonValueKind.Array
                ? credited.EnumerateArray()
                    .Select(edge => Text(edge, "role") is { } role && role.Contains("Story", StringComparison.OrdinalIgnoreCase) && Child(edge, "node") is { } node
                        && Child(node, "name") is { } name ? Text(name, "native") : null)
                    .OfType<string>().Distinct(StringComparer.Ordinal).Take(3).ToArray()
                : [];
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
                Count(media, "volumes", 500), description, Https(cover), authors, sequels, prequels, Https(banner), trailer)
            {
                People = people,
                NativeAuthors = nativeAuthors,
                Relations = Relations(media),
                OriginalLanguage = Text(media, "countryOfOrigin") switch { "JP" => "ja", "KR" => "ko", "CN" or "TW" => "zh", _ => null }
            };
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

        // Every related entry that AniList names with a title.
        private static AniListRelation[] Relations(JsonElement media) =>
            Child(media, "relations") is { } relations && relations.TryGetProperty("edges", out var links) && links.ValueKind == JsonValueKind.Array
                ? [.. links.EnumerateArray().Select(link =>
                    {
                        if (Text(link, "relationType") is not { } relationType || Child(link, "node") is not { } node || Text(node, "type") is not { } type
                            || !node.TryGetProperty("id", out var id) || !id.TryGetInt32(out var relatedId) || relatedId <= 0 || Child(node, "title") is not { } title)
                        {
                            return null;
                        }

                        var name = Text(title, "english") ?? Text(title, "romaji");
                        var year = Child(node, "startDate") is { } start && start.TryGetProperty("year", out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt32() : (int?)null;
                        return name is null ? null : new AniListRelation(relationType, relatedId, type, Text(node, "format"), name, year, Https(Child(node, "coverImage") is { } image ? Text(image, "large") : null));
                    }).OfType<AniListRelation>()]
                : [];

        private Task<JsonDocument> PostAsync(string query, Dictionary<string, object> variables, CancellationToken ct, string? token = null) =>
            PostAsync(new Dictionary<string, object> { ["query"] = query, ["variables"] = variables }, http.BaseAddress!, token, true, ct);

        // A person's own calls carry their token; the token exchange answers without GraphQL data.
        private async Task<JsonDocument> PostAsync(object body, Uri endpoint, string? token, bool graphql, CancellationToken ct)
        {
            // A rate limit holds every caller until AniList allows requests again.
            if (Interlocked.Read(ref _blockedUntilTicks) > DateTimeOffset.UtcNow.UtcTicks)
            {
                throw new AniListException(AniListFailure.RateLimited, "AniList limits requests.");
            }

            HttpResponseMessage response;
            await _pace.Gate.WaitAsync(ct);
            try
            {
                await _pace.WaitForTurnAsync(ct);
                using var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = JsonContent.Create(body) };
                if (token is not null)
                {
                    request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
                }

                response = await http.SendAsync(request, ct);
            }
            catch (Exception exception) when (exception is HttpRequestException || exception is TaskCanceledException && !ct.IsCancellationRequested)
            {
                throw new AniListException(AniListFailure.Unavailable, "AniList could not be reached.", exception);
            }
            finally
            {
                _pace.Gate.Release();
            }

            using (response)
            {
                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    throw new AniListException(AniListFailure.NotFound, "AniList does not know this title.");
                }

                // A token AniList no longer accepts, or a code it refuses.
                if (response.StatusCode == HttpStatusCode.Unauthorized
                    || response.StatusCode == HttpStatusCode.BadRequest && (!graphql || token is not null && (await response.Content.ReadAsStringAsync(ct)).Contains("Invalid token", StringComparison.Ordinal)))
                {
                    throw new AniListException(AniListFailure.Unauthorized, "AniList does not accept this sign-in.");
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
                    if (graphql && (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object))
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
