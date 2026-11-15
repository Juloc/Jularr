using System.Globalization;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.User.Discover.V1;
using Jularr.Backend.Data.User.Media.V1;
using Jularr.Backend.Service.User.Discover.V1;
using Jularr.Backend.Service.User.Media.V1;
using Jularr.Frontend.Framework;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;

namespace Jularr.Frontend.Pages.User.Discover
{
    // "Explore": a full-screen vertical feed for phones first. Each title fills the screen, its trailer (or its picture) in the upper half and its name, facts, summary and
    // buttons in the lower half; the scroll snaps title by title. The titles are the mode's trending lists mixed with what the profile may like, a few at a time: the page
    // holds only the first title's skeleton, the parts load as the reader nears the end.
    public sealed class FeedModel(ReadDiscoverScopesV1 readScopes, ReadDiscoverFeedV1 feeds, ReadDiscoverCatalogFeedV1 catalogFeeds, ReadDiscoverTasteV1 readTaste, ReadWorkV1 readWork,
        UiText texts, FrontendAssets assets, IAntiforgery antiforgery) : JularrPage
    {
        // Titles of one type in a part, and how many types one part mixes.
        private const int PerType = 4;
        private const int MostTypes = 3;
        // A list page holds twenty titles: five parts read from one page.
        private const int PartsPerListPage = 20 / PerType;
        private const int MostParts = 60;
        private const int ForYouTitles = 3;

        private sealed record Entry(long WorkId, string Reason, WorkDetailsV1? Work, string Title, int? Year, string? Poster, string? Backdrop, MediaType Type, MediaSubtype? Subtype);

        private IReadOnlyList<Entry> _entries = [];
        private bool _partRead;
        private bool _moreParts;

        // The media types as slugs, repeated or separated by commas; none takes the mode's types.
        [BindProperty(SupportsGet = true, Name = "type")]
        public string[]? TypeSlugs { get; set; }

        [BindProperty(SupportsGet = true, Name = "p")]
        public int PartNumber { get; set; } = 1;

        private int Part => Math.Clamp(PartNumber, 1, MostParts);

        protected override void Define(Page page)
        {
            page.Title("discover.explore.title");
            page.Parent("discover.title", "/");
            page.Navigation("nav.home");
            if (!texts.CanReadMedia())
            {
                page.Content.Add(new P(new Text("media.notForProfile")).Attribute("role", "status"));
                return;
            }

            page.Asset("Pages/User/Discover/Feed.entry.scss");
            page.Asset("Pages/User/Discover/Feed.entry.ts");
            var csrf = antiforgery.GetAndStoreTokens(HttpContext).RequestToken ?? "";
            page.Content.Add(Body(csrf));
        }

        private IUiComponent Body(string csrf)
        {
            var content = new List<IUiComponent>(Items(csrf));

            // Before the first part arrives, one title's room is held by its skeleton, so nothing moves when it does.
            if (!_partRead) content.Add(Skeleton());
            else if (_entries.Count == 0 && Part == 1) content.Add(new P(new Text("discover.explore.empty")).Class("feed__note").Attribute("role", "status"));

            content.Add(Next());
            return new Div([.. content]).Attribute("id", "discover-feed").Class("feed").Attribute("data-csrf", csrf).Attribute("data-load-error", texts["discover.loadFailed"])
                .Attribute("data-label-add", texts["media.favorites.add"]).Attribute("data-label-remove", texts["media.favorites.remove"])
                .Attribute("data-autoplay", AutoplayAllowed ? "true" : "false").Attribute("aria-label", texts["discover.explore.title"]).Attribute("tabindex", "0");
        }

        private bool AutoplayAllowed => texts.AutoplayPreviews && !string.Equals(Request.Headers["Save-Data"], "on", StringComparison.OrdinalIgnoreCase);

        private IEnumerable<IUiComponent> Items(string csrf) => _entries.Select(entry => Item(entry, csrf));

        private IUiComponent Skeleton() => new Div(new Div(new Span().Class("skeleton-block feed__skeleton-media")),
            new Div(new Span().Class("skeleton-block feed__skeleton-line feed__skeleton-line--title"), new Span().Class("skeleton-block feed__skeleton-line"),
                new Span().Class("skeleton-block feed__skeleton-line"), new Span().Class("skeleton-block feed__skeleton-line feed__skeleton-line--short")).Class("feed__info"))
            .Class("feed__item feed__item--skeleton").Attribute("aria-hidden", "true");

        // The link to the next part: the page's script follows it as the reader nears the end, without script it is a link; none after a part that held nothing.
        private IUiComponent Next()
        {
            if (_partRead && !_moreParts)
            {
                return new P(new Text("discover.end")).Class("feed__note feed__note--end").Attribute("role", "status");
            }

            var types = TypeSlugs is { Length: > 0 } ? "&type=" + Uri.EscapeDataString(string.Join(',', TypeSlugs)).Replace("%2C", ",") : "";
            var next = (_partRead ? Part + 1 : 1).ToString(CultureInfo.InvariantCulture);
            return new Nav(new A(new Text(_partRead ? "list.loadMore" : "discover.loadRecommendations")).Class("button").Attribute("data-feed-next", "true")
                .Attribute("data-retry-label", texts["discover.retry"]).Attribute("data-navigation", "full").Attribute("href", "/Discover/Feed?handler=Part&p=" + next + types)).Class("feed__more");
        }

        private IUiComponent Item(Entry entry, string csrf)
        {
            var id = entry.WorkId.ToString(CultureInfo.InvariantCulture);
            var details = "/Details/" + id;
            var work = entry.Work;
            var art = entry.Backdrop ?? entry.Poster;
            IUiComponent picture = art is null
                ? new NoCover(assets, NoCoverKind.Poster, "feed__cover")
                : new MediaImage(art, "", 1280, 720, sizes: "100vw");
            var media = new List<IUiComponent> { new Div(picture).Class("feed__picture") };
            var trailer = work?.TrailerKey is { Length: 11 } key && key.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_') ? key : null;
            if (trailer is not null)
            {
                media.Add(new Div().Class("feed__trailer").Attribute("data-feed-trailer", ""));
                media.Add(new Button(new Icon("player-volume", assets), new Icon("player-muted", assets)).Class("feed__sound").Attribute("data-feed-sound", "").Attribute("hidden", "hidden")
                    .Attribute("aria-label", texts["media.preview.unmuteTrailer"]).Attribute("data-label-muted", texts["media.preview.unmuteTrailer"])
                    .Attribute("data-label-sound", texts["media.preview.muteTrailer"]));
            }

            // Type, year and the first genres.
            var facts = new List<string> { texts[MediaTypeLabel.Key(entry.Type, entry.Subtype)] };
            if (entry.Year is { } year) facts.Add(year.ToString(CultureInfo.InvariantCulture));
            if (work?.Genres is { Length: > 0 } genres) facts.Add(string.Join(", ", genres.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(3)));

            var info = new List<IUiComponent>
            {
                new P(new Text(entry.Reason)).Class("feed__reason"),
                new H2(new A(new ValueText(entry.Title)).Attribute("href", details)).Class("feed__title"),
                new P(new ValueText(string.Join(" · ", facts))).Class("feed__facts")
            };
            if (!string.IsNullOrWhiteSpace(work?.Overview)) info.Add(new P(new ValueText(work!.Overview!)).Class("feed__overview"));

            var actions = new List<IUiComponent>();
            if (work is not null && work.AvailableUnits > 0 && texts.CanPlay())
            {
                actions.Add(new A(new Icon("play", assets, 16), new Span(new Text("discover.action.watch"))).Class("button button--primary").Attribute("href", details));
            }
            else if (work is { IsRequestable: true } && texts.CanRequest())
            {
                actions.Add(new A(new Icon("plus", assets, 16), new Span(new Text("discover.action.request"))).Class("button button--primary").Attribute("href", details));
            }

            actions.Add(new MediaBookmark(assets, entry.WorkId, work?.IsWatchlisted ?? false, "/Discover/Feed", csrf));
            actions.Add(new A(new Span(new Text("media.details"))).Class("button").Attribute("href", details));
            info.Add(new Div([.. actions]).Class("feed__actions"));

            var item = new Section(new Div([.. media]).Class("feed__media"), new Div([.. info]).Class("feed__info")).Class("feed__item").Attribute("data-feed-item", "");
            if (trailer is not null) item.Attribute("data-trailer-id", trailer);
            return item;
        }

        public IActionResult OnGet()
        {
            PreparePage();
            return Page();
        }

        // One part of titles; the page's script loads it as a fragment, without script it is the page itself.
        public async Task<IActionResult> OnGetPartAsync()
        {
            if (!texts.CanReadMedia()) return Forbid();
            Response.Headers.Vary = "X-Jularr-Fragment";
            await LoadDataAsync();
            PreparePage();
            if (Request.Headers["X-Jularr-Fragment"] != "feed") return Page();

            var csrf = antiforgery.GetAndStoreTokens(HttpContext).RequestToken ?? "";
            return Partial("_UiComponent", new Div([.. Items(csrf), Next()]).Attribute("id", "discover-feed-part"));
        }

        protected override async Task LoadDataAsync()
        {
            if (!texts.CanReadMedia()) return;

            var token = HttpContext.RequestAborted;
            var all = await readScopes.ExecuteAsync<BaseDataList<DiscoverScopeV1>>(new DiscoverParametersV1(), token);
            var requested = (TypeSlugs ?? []).SelectMany(value => value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).ToHashSet();
            var inMode = all.Where(scope => texts.Mode.Slugs.Contains(scope.Slug)).ToList();
            var scopes = (requested.Count > 0 && inMode.Any(scope => requested.Contains(scope.Slug)) ? inMode.Where(scope => requested.Contains(scope.Slug)) : inMode).Take(MostTypes).ToList();
            _partRead = true;
            if (scopes.Count == 0) return;

            // The part's slice of each type's trending list (the first list a type has when it has none); each list page serves several parts.
            var page = ((Part - 1) / PartsPerListPage) + 1;
            var offset = ((Part - 1) % PartsPerListPage) * PerType;
            var taste = Part == 1
                ? readTaste.ExecuteAsync<BaseDataList<DiscoverTasteShelfV1>>(new ReadDiscoverTasteDataV1([.. scopes.Select(scope => scope.Slug)]), token)
                : Task.FromResult(new BaseDataList<DiscoverTasteShelfV1>([]));
            var lists = await Task.WhenAll(scopes.Select(scope => ReadListAsync(scope, page, offset, token)));
            var forYou = (await taste).Where(shelf => shelf.GenreId is null).ToList();

            var picked = new List<(long WorkId, string Reason, string Title, int? Year, string? Poster, string? Backdrop, MediaType Type, MediaSubtype? Subtype)>();
            void Add(long id, string reason, string title, int? year, string? poster, string? backdrop, DiscoverScopeV1 scope)
            {
                if (picked.All(item => item.WorkId != id)) picked.Add((id, reason, title, year, poster, backdrop, scope.MediaTypeId, scope.MediaSubtypeId));
            }

            // What fits the profile first, the types taking turns, then the trending titles the same way.
            var taken = forYou.Select(shelf => (Scope: scopes.FirstOrDefault(scope => scope.Slug == shelf.ScopeSlug), shelf)).Where(entry => entry.Scope is not null)
                .Select(entry => entry.Scope!.IsCatalog
                    ? entry.shelf.CatalogItems.Take(ForYouTitles).Select(item => (item.WorkId, item.Title, item.ReleaseYear, item.PosterUrl, (string?)null, entry.Scope)).ToList()
                    : entry.shelf.Items.Take(ForYouTitles).Select(item => (item.WorkId, item.Title, item.ReleaseYear, item.PosterUrl, item.BackdropUrl, entry.Scope)).ToList()).ToList();
            for (var index = 0; index < ForYouTitles; index++)
            {
                foreach (var column in taken.Where(column => index < column.Count)) Add(column[index].WorkId, "discover.forYou", column[index].Title, column[index].ReleaseYear, column[index].PosterUrl, column[index].Item5, column[index].Scope);
            }

            var reasons = scopes.Select(scope => scope.Feeds.Contains(DiscoverFeedType.Trending) ? DiscoverFeedType.Trending : scope.Feeds[0]).ToList();
            var columns = lists.Select((list, index) => (List: list, Scope: scopes[index], Reason: "discover.feed." + reasons[index])).ToList();
            for (var index = 0; index < PerType; index++)
            {
                foreach (var (list, scope, reason) in columns.Where(column => index < column.List.Count))
                {
                    Add(list[index].WorkId, reason, list[index].Title, list[index].Year, list[index].Poster, list[index].Backdrop, scope);
                }
            }

            // Each title with its own details, read together; the ones that cannot be read keep what their list said.
            var works = await Task.WhenAll(picked.Select(async item =>
            {
                try
                {
                    return await readWork.ExecuteAsync<WorkDetailsV1>(new WorkParametersV1(item.WorkId), token);
                }
                catch (Exception exception) when (exception is KeyNotFoundException or DataValidationException or ServiceUnavailableException)
                {
                    return null;
                }
            }));
            _entries = [.. picked.Select((item, index) => new Entry(item.WorkId, item.Reason, works[index], works[index]?.CanonicalTitle ?? item.Title, works[index]?.ReleaseYear ?? item.Year,
                works[index]?.PosterUrl ?? item.Poster, works[index]?.BackdropUrl ?? item.Backdrop, item.Type, item.Subtype))];
            _moreParts = Part < MostParts && lists.Any(list => list.Count >= PerType) && _entries.Count > 0;
        }

        // The slice of one type's list: its titles, or none when its provider does not answer.
        private async Task<List<(long WorkId, string Title, int? Year, string? Poster, string? Backdrop)>> ReadListAsync(DiscoverScopeV1 scope, int page, int offset, CancellationToken token)
        {
            var feed = scope.Feeds.Contains(DiscoverFeedType.Trending) ? DiscoverFeedType.Trending : scope.Feeds[0];
            try
            {
                if (scope.IsCatalog)
                {
                    var catalog = await catalogFeeds.ExecuteAsync<BaseDataList<DiscoverCatalogItemV1>>(new ReadDiscoverCatalogFeedDataV1(scope.MediaTypeId, feed, scope.MediaSubtypeId, page), token);
                    return [.. catalog.Skip(offset).Take(PerType).Select(item => (item.WorkId, item.Title, item.ReleaseYear, item.PosterUrl, (string?)null))];
                }

                var items = await feeds.ExecuteAsync<BaseDataList<DiscoverItemV1>>(new ReadDiscoverFeedDataV1(scope.MediaTypeId, feed, page), token);
                return [.. items.Skip(offset).Take(PerType).Select(item => (item.WorkId, item.Title, item.ReleaseYear, item.PosterUrl, item.BackdropUrl))];
            }
            catch (Exception exception) when (exception is ServiceUnavailableException or KeyNotFoundException)
            {
                return [];
            }
        }
    }
}
