using System.Globalization;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Public.Authentication.V1;
using Jularr.Backend.Data.User.Library.V1;
using Jularr.Backend.Data.User.Profile.V1;
using Jularr.Backend.Service.Public.Authentication.V1;
using Jularr.Backend.Service.User.Library.V1;
using Jularr.Backend.Service.User.Profile.V1;
using Jularr.Frontend.Framework;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;

namespace Jularr.Frontend.Pages.User.Library
{
    // What the profile holds, narrowed by the same media-type bar, filter drawer, order and layout as Favorites.
    public sealed class LibraryModel(ReadAuthenticationV1 authentication, ReadLibraryV1 read, ReadProfileMediaV1 readMedia, UiText texts, FrontendAssets assets, IAntiforgery antiforgery) : JularrPage
    {
        private PageResult<LibraryItemV1>? _items;
        private IReadOnlyList<ProfileMediaV1>? _media;
        private ScopeSelection? _scopes;

        // Media types as slugs, repeated or separated by commas; none shows all of them.
        [BindProperty(SupportsGet = true, Name = "type")]
        public string[] Types { get; set; } = [];

        [BindProperty(SupportsGet = true, Name = "q")]
        public string? Query { get; set; }

        [BindProperty(SupportsGet = true, Name = "sort")]
        public LibrarySortType Sort { get; set; } = LibrarySortType.RecentlyAdded;

        [BindProperty(SupportsGet = true, Name = "year")]
        public string? Year { get; set; }

        [BindProperty(SupportsGet = true, Name = "p")]
        public int PageNumber { get; set; } = 1;

        private sealed record State(IReadOnlyList<string> Types, string? Query, LibrarySortType Sort, string? Year, int Page);

        private ScopeSelection Scopes => _scopes ??= new ScopeSelection(Types, _media);

        private string? ValidYear => YearChoice.Valid(Year);

        private State Current => new(Scopes.Picked, string.IsNullOrWhiteSpace(Query) ? null : Query.Trim(), Sort, ValidYear, Math.Max(1, PageNumber));

        protected override void Define(Page page)
        {
            page.Title("nav.library");
            page.Navigation("nav.library");
            if (_items is not { } items)
            {
                if (!texts.CanReadMedia()) page.Content.Add(new P(new Text("media.notForProfile")).Attribute("role", "status"));
                return;
            }

            page.Asset("Pages/User/Library/Library.entry.scss");
            page.Asset("Framework/Components/MediaCard.entry.ts");
            page.Asset("Framework/Components/Drawer.entry.ts");
            page.Asset("Framework/Components/ChoiceSelect.entry.ts");
            page.Asset("Framework/Components/LayoutToggle.entry.ts");

            // The media-type bar with the filter at its end, then search, order and layout, then the titles.
            var narrowed = new List<IUiComponent> { new Icon("filter", assets) };
            if (ValidYear is not null) narrowed.Add(new Span(new ValueText("1")).Class("capsule-bar__badge"));
            var tools = new List<IUiComponent> { Drawer.Opener(new Button([.. narrowed]).IconOnly(texts["library.filter.open"]).Class("button button--icon capsule-bar__tool"), "library-filter") };
            page.Content.Add(new MediaTypeBar(assets, Scopes.ListedSlugs, Scopes.ChosenSlugs, "library.scope.", types => Address(Current with { Types = types, Page = 1 }), tools));

            var keep = new Dictionary<string, string>();
            if (Scopes.Parameter(Scopes.Picked) is { } types) keep["type"] = types;
            if (ValidYear is not null) keep["year"] = ValidYear;
            if (Current.Query is { } query) keep["q"] = query;
            var sorts = Enum.GetValues<LibrarySortType>().Select(sort => new ChoiceOption("library.sort." + sort, sort.ToString(), sort == Sort)).ToList();
            var searchKeep = new Dictionary<string, string>(keep);
            searchKeep.Remove("q");
            if (Sort != LibrarySortType.RecentlyAdded) searchKeep["sort"] = Sort.ToString();
            page.Content.Add(new Div(new SearchBox("/Library", "library.search", Query, searchKeep),
                new Div(new ChoiceSelect("/Library", "sort", "library.sort", sorts, keep), new LayoutToggle(assets)).Class("library-toolbar__settings")).Class("library-toolbar"));

            if (items.Items.Count == 0)
            {
                page.Content.Add(new P(new Text(Current.Query is null && Scopes.IsAll && ValidYear is null ? "library.userEmpty" : "discover.noResults")).Attribute("role", "status"));
            }
            else
            {
                var csrf = antiforgery.GetAndStoreTokens(HttpContext).RequestToken;
                page.Content.Add(new Div([.. items.Items.Select(item => MediaCard.For(assets, item)
                    .Bookmark(new MediaBookmark(assets, item.Id, item.IsWatchlisted, Request.Path + Request.QueryString, csrf)))]).Class("media-grid library-grid"));
            }

            // A full page offers the next one.
            var paging = new List<IUiComponent>();
            if (items.Page > 1) paging.Add(new A(new Text("common.previous")).Class("button").Attribute("href", Address(Current with { Page = items.Page - 1 })));
            if (items.Items.Count == items.PageSize) paging.Add(new A(new Text("common.next")).Class("button").Attribute("href", Address(Current with { Page = items.Page + 1 })));
            if (paging.Count > 0) page.Content.Add(new Nav([.. paging]).Class("page-actions").Attribute("aria-label", texts["library.paging"]));

            // The panel opens over the page; the search and the order stay in the address.
            var drawerKeep = new Dictionary<string, string>();
            if (Current.Query is { } search) drawerKeep["q"] = search;
            if (Sort != LibrarySortType.RecentlyAdded) drawerKeep["sort"] = Sort.ToString();
            page.Content.Add(new MediaFilterDrawer(assets, "library-filter", "library.filter.title", "/Library", Scopes, ValidYear, Address(Current with { Types = [], Year = null, Page = 1 }), drawerKeep));
        }

        public async Task<IActionResult> OnGetAsync()
        {
            await LoadDataAsync();
            PreparePage();
            return Page();
        }

        protected override async Task LoadDataAsync()
        {
            if (!texts.CanReadMedia())
            {
                return;
            }

            var token = HttpContext.RequestAborted;
            var profileId = (await authentication.ExecuteAsync<AuthenticationResultV1>(cancellationToken: token)).ProfileId
                ?? throw new UnauthorizedAccessException("An active profile is required.");
            _media = await readMedia.ExecuteAsync<BaseDataList<ProfileMediaV1>>(new ReadProfileParametersV1(profileId), token);
            _scopes = null;

            // One bounded page of what the narrowed list holds.
            var (types, subtypes) = Scopes.Filter();
            var (yearFrom, yearTo) = YearChoice.Range(Year);
            _items = await read.ExecuteAsync<PageResult<LibraryItemV1>>(new ReadLibraryParametersV1(types, Current.Query, Sort, Current.Page, 48, subtypes, yearFrom, yearTo), token);
        }

        private string Address(State state)
        {
            var values = new List<string>();
            if (Scopes.Parameter(state.Types) is { } types) values.Add("type=" + Uri.EscapeDataString(types).Replace("%2C", ","));
            if (state.Sort != LibrarySortType.RecentlyAdded) values.Add("sort=" + state.Sort);
            if (state.Year is not null) values.Add("year=" + state.Year);
            if (state.Query is not null) values.Add("q=" + Uri.EscapeDataString(state.Query));
            if (state.Page > 1) values.Add("p=" + state.Page.ToString(CultureInfo.InvariantCulture));
            return values.Count == 0 ? "/Library" : "/Library?" + string.Join('&', values);
        }
    }
}
