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
    public sealed class LibraryModel(ReadAuthenticationV1 authentication, ReadLibraryV1 read, ReadLibraryFilterOptionsV1 readOptions,
        ReadProfileMediaV1 readMedia, UiText texts, FrontendAssets assets, IAntiforgery antiforgery) : JularrPage
    {
        private PageResult<LibraryItemV1>? _items;
        private IReadOnlyList<ProfileMediaV1>? _media;
        private ScopeSelection? _scopes;
        private LibraryFilterOptionsV1 _options = new([], [], []);

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

        [BindProperty(SupportsGet = true, Name = "availability")]
        public string[] Availability { get; set; } = [];

        [BindProperty(SupportsGet = true, Name = "progress")]
        public ProgressStateType? Progress { get; set; }

        [BindProperty(SupportsGet = true, Name = "genre")]
        public string[] Genres { get; set; } = [];

        [BindProperty(SupportsGet = true, Name = "audio")]
        public string[] Audio { get; set; } = [];

        [BindProperty(SupportsGet = true, Name = "subtitle")]
        public string[] Subtitles { get; set; } = [];

        [BindProperty(SupportsGet = true, Name = "rating")]
        public decimal? Rating { get; set; }

        private sealed record State(IReadOnlyList<string> Types, string? Query, LibrarySortType Sort, string? Year, int Page,
            LibraryAvailabilityType[] Availability, ProgressStateType? Progress, string[] Genres, string[] Audio, string[] Subtitles, decimal? Rating);

        private ScopeSelection Scopes => _scopes ??= new ScopeSelection(Types, _media);
        private string? ValidYear => YearChoice.Valid(Year);
        private static string[] Values(string[] values) => [.. values.SelectMany(value => value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)).Distinct()];
        private LibraryAvailabilityType[] Available => Values(Availability)
            .Select(value => Enum.TryParse<LibraryAvailabilityType>(value, out var parsed) && Enum.IsDefined(parsed) ? parsed : LibraryAvailabilityType.Available)
            .Distinct().DefaultIfEmpty(LibraryAvailabilityType.Available).ToArray();
        private State Current => new(Scopes.Picked, string.IsNullOrWhiteSpace(Query) ? null : Query.Trim(), Sort, ValidYear, Math.Max(1, PageNumber),
            Available, Progress, Values(Genres), Values(Audio), Values(Subtitles), Rating);

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
            page.Asset("Framework/Components/InfiniteList.entry.ts");

            // The media-type bar with the filter at its end, then search, order and layout, then the titles.
            var narrowed = new List<IUiComponent> { new Icon("filter", assets) };
            if (FilterCount > 0) narrowed.Add(new Span(new ValueText(FilterCount.ToString(CultureInfo.InvariantCulture))).Class("capsule-bar__badge"));
            var tools = new List<IUiComponent> { Drawer.Opener(new Button([.. narrowed]).IconOnly(texts["library.filter.open"]).Class("button button--icon capsule-bar__tool"), "library-filter") };
            page.Content.Add(new MediaTypeBar(assets, Scopes.ListedSlugs, Scopes.ChosenSlugs, "library.scope.", types => Address(Current with { Types = types, Page = 1 }), tools));

            var keep = FilterValues(Current);
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
                page.Content.Add(new P(new Text(Current.Query is null && Scopes.IsAll && FilterCount == 0 ? "library.userEmpty" : "discover.noResults")).Attribute("role", "status"));
            }
            else
            {
                var csrf = antiforgery.GetAndStoreTokens(HttpContext).RequestToken;
                page.Content.Add(new Div([.. items.Items.Select(item => MediaCard.For(assets, item)
                    .Bookmark(new MediaBookmark(assets, item.Id, item.IsWatchlisted, Request.Path + Request.QueryString, csrf)))]).Class("media-grid library-grid"));
            }

            // A full page offers the next one.
            page.Content.Add(new Span().Infinite(items.Items.Count, items.PageSize, "p", ".library-grid"));

            // The panel opens over the page; the search and the order stay in the address.
            var drawerKeep = new Dictionary<string, string>();
            if (Current.Query is { } search) drawerKeep["q"] = search;
            if (Sort != LibrarySortType.RecentlyAdded) drawerKeep["sort"] = Sort.ToString();
            var reset = Current with { Types = [], Year = null, Page = 1, Availability = [LibraryAvailabilityType.Available], Progress = null, Genres = [], Audio = [], Subtitles = [], Rating = null };
            page.Content.Add(new MediaFilterDrawer(assets, "library-filter", "library.filter.title", "/Library", Scopes, ValidYear, Address(reset), drawerKeep, FilterGroups()));
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
            var state = Current;
            var parameters = new ReadLibraryParametersV1(types, state.Query, Sort, state.Page, 48, subtypes, yearFrom, yearTo,
                state.Availability, state.Progress, state.Genres, state.Audio, state.Subtitles, state.Rating);
            _items = await read.ExecuteAsync<PageResult<LibraryItemV1>>(parameters, token);
            // Appended pages only consume the grid and its next link.
            if (Request.Headers["X-Jularr-Infinite"] != "1") _options = await readOptions.ExecuteAsync<LibraryFilterOptionsV1>(parameters, token);
        }

        private int FilterCount => (ValidYear is null ? 0 : 1) + (Available.SequenceEqual([LibraryAvailabilityType.Available]) ? 0 : 1)
            + (Progress is null ? 0 : 1) + Values(Genres).Length + Values(Audio).Length + Values(Subtitles).Length + (Rating is null ? 0 : 1);

        private static Dictionary<string, string> FilterValues(State state)
        {
            var values = new Dictionary<string, string>();
            if (!state.Availability.SequenceEqual([LibraryAvailabilityType.Available])) values["availability"] = string.Join(',', state.Availability);
            if (state.Progress is not null) values["progress"] = state.Progress.ToString()!;
            if (state.Genres.Length > 0) values["genre"] = string.Join(',', state.Genres);
            if (state.Audio.Length > 0) values["audio"] = string.Join(',', state.Audio);
            if (state.Subtitles.Length > 0) values["subtitle"] = string.Join(',', state.Subtitles);
            if (state.Rating is not null) values["rating"] = state.Rating.Value.ToString(CultureInfo.InvariantCulture);
            return values;
        }

        private IReadOnlyList<IUiComponent> FilterGroups()
        {
            var state = Current;
            ChoiceGroup Languages(string key, string name, string[] options, string[] selected) => new(assets, key, name,
                [.. options.Concat(selected).Distinct().OrderBy(LanguageLabel.Name).Select(code => new ChoicePick(code, selected.Contains(code), Label: LanguageLabel.Name(code)))],
                new ChoiceOptions(Multiple: true, Layout: ChoiceLayout.Checks));
            return [
                new ChoiceGroup(assets, "library.filter.availability", "availability",
                    [.. Enum.GetValues<LibraryAvailabilityType>().Select(value => new ChoicePick(value.ToString(), state.Availability.Contains(value), "library.availability." + value))], new ChoiceOptions(Multiple: true)),
                new ChoiceGroup(assets, "media.progress", "progress", [
                    new("", state.Progress is null, "discover.all"),
                    new("NotStarted", state.Progress == ProgressStateType.NotStarted, "favorites.status.notStarted"),
                    new("InProgress", state.Progress == ProgressStateType.InProgress, "favorites.status.inProgress"),
                    new("Finished", state.Progress == ProgressStateType.Finished, "favorites.status.finished")]),
                new ChoiceGroup(assets, "library.filter.genres", "genre",
                    [.. _options.Genres.Concat(state.Genres).Distinct().Order().Select(value => new ChoicePick(value, state.Genres.Contains(value)))], new ChoiceOptions(Multiple: true, Layout: ChoiceLayout.Checks)),
                Languages("library.filter.audio", "audio", _options.AudioLanguages, state.Audio),
                Languages("library.filter.subtitles", "subtitle", _options.SubtitleLanguages, state.Subtitles),
                new ChoiceGroup(assets, "library.filter.rating", "rating",
                    [new("", state.Rating is null, "discover.all"), .. Enumerable.Range(1, 10).Select(value => new ChoicePick(value.ToString(CultureInfo.InvariantCulture), state.Rating == value))])
            ];
        }

        private string Address(State state)
        {
            var values = FilterValues(state).Select(pair => pair.Key + "=" + Uri.EscapeDataString(pair.Value)).ToList();
            if (Scopes.Parameter(state.Types) is { } types) values.Add("type=" + Uri.EscapeDataString(types).Replace("%2C", ","));
            if (state.Sort != LibrarySortType.RecentlyAdded) values.Add("sort=" + state.Sort);
            if (state.Year is not null) values.Add("year=" + state.Year);
            if (state.Query is not null) values.Add("q=" + Uri.EscapeDataString(state.Query));
            if (state.Page > 1) values.Add("p=" + state.Page.ToString(CultureInfo.InvariantCulture));
            return values.Count == 0 ? "/Library" : "/Library?" + string.Join('&', values);
        }
    }
}
