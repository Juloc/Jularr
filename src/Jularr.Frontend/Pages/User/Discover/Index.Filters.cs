using System.Globalization;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.User.Discover.V1;
using Jularr.Frontend.Framework;
using Microsoft.AspNetCore.Mvc;

namespace Jularr.Frontend.Pages.User.Discover
{
    // Discover's filter row, as AniList's search has it: genres, year, season and format at hand, the others under More (the filter drawer).
    // Any filter shows the chosen types' titles as one grid instead of the shelves; several types share only genre and year.
    public sealed partial class IndexModel
    {
        private static readonly DiscoverFilter[] SharedFilters = [DiscoverFilter.Genre, DiscoverFilter.Year];
        private const int FirstYear = 1940;

        private readonly Dictionary<string, DiscoverFilterOptionsV1> _filterOptions = new(StringComparer.Ordinal);

        [BindProperty(SupportsGet = true, Name = "genre")]
        public string[]? GenreValues { get; set; }

        [BindProperty(SupportsGet = true, Name = "tag")]
        public string[]? TagValues { get; set; }

        [BindProperty(SupportsGet = true, Name = "format")]
        public string[]? FormatValues { get; set; }

        [BindProperty(SupportsGet = true, Name = "status")]
        public string[]? StatusValues { get; set; }

        [BindProperty(SupportsGet = true, Name = "source")]
        public string[]? SourceValues { get; set; }

        [BindProperty(SupportsGet = true, Name = "site")]
        public string[]? SiteValues { get; set; }

        [BindProperty(SupportsGet = true, Name = "country")]
        public string? CountryValue { get; set; }

        [BindProperty(SupportsGet = true, Name = "lang")]
        public string? LanguageValue { get; set; }

        [BindProperty(SupportsGet = true, Name = "season")]
        public int? SeasonValue { get; set; }

        [BindProperty(SupportsGet = true, Name = "year")]
        public int? YearValue { get; set; }

        [BindProperty(SupportsGet = true, Name = "yearFrom")]
        public int? YearFromValue { get; set; }

        [BindProperty(SupportsGet = true, Name = "yearTo")]
        public int? YearToValue { get; set; }

        [BindProperty(SupportsGet = true, Name = "episodesFrom")]
        public int? EpisodesFromValue { get; set; }

        [BindProperty(SupportsGet = true, Name = "episodesTo")]
        public int? EpisodesToValue { get; set; }

        [BindProperty(SupportsGet = true, Name = "chaptersFrom")]
        public int? ChaptersFromValue { get; set; }

        [BindProperty(SupportsGet = true, Name = "chaptersTo")]
        public int? ChaptersToValue { get; set; }

        [BindProperty(SupportsGet = true, Name = "volumesFrom")]
        public int? VolumesFromValue { get; set; }

        [BindProperty(SupportsGet = true, Name = "volumesTo")]
        public int? VolumesToValue { get; set; }

        [BindProperty(SupportsGet = true, Name = "durationFrom")]
        public int? DurationFromValue { get; set; }

        [BindProperty(SupportsGet = true, Name = "durationTo")]
        public int? DurationToValue { get; set; }

        [BindProperty(SupportsGet = true, Name = "adult")]
        public bool Adult { get; set; }

        // What the chosen types offer together: one type its own filters, several only the shared ones with the genres all of them know.
        private DiscoverFilterOptionsV1? Offered
        {
            get
            {
                var each = Chosen.Select(scope => _filterOptions.GetValueOrDefault(scope.Slug)).ToList();
                if (each.Count == 0 || each.Any(options => options is null || options.Filters.Length == 0)) return null;
                if (each is [var only]) return only;
                var genres = each.Skip(1).Aggregate((IEnumerable<Genre>)each[0]!.GenreIds, (common, options) => common.Intersect(options!.GenreIds)).ToArray();
                return new DiscoverFilterOptionsV1([.. SharedFilters.Where(filter => each.All(options => options!.Filters.Contains(filter)))], genres, [], [], [], [], [], [], new([]), new([]));
            }
        }

        private bool Offers(DiscoverFilter filter) => Offered?.Filters.Contains(filter) == true;

        // The choices that count: what the chosen types offer, in the catalogs' values.
        private Genre[] Genres => Offers(DiscoverFilter.Genre) ? [.. Parsed<Genre>(GenreValues).Where(genre => Offered!.GenreIds.Contains(genre)).Order()] : [];
        private CatalogFormat[] Formats => Offers(DiscoverFilter.Format) ? [.. Parsed<CatalogFormat>(FormatValues).Where(format => Offered!.CatalogFormatIds.Contains(format)).Order()] : [];
        private ReleaseStatus[] Statuses => Offers(DiscoverFilter.Status) ? [.. Parsed<ReleaseStatus>(StatusValues).Where(status => Offered!.ReleaseStatusIds.Contains(status)).Order()] : [];
        private CatalogSource[] Sources => Offers(DiscoverFilter.Source) ? [.. Parsed<CatalogSource>(SourceValues).Where(source => Offered!.CatalogSourceIds.Contains(source)).Order()] : [];
        private int[] Tags => Offers(DiscoverFilter.Tag) ? Numbers(TagValues) : [];
        private int[] Sites => Offers(DiscoverFilter.Site) ? Numbers(SiteValues) : [];
        private string? Country => Offers(DiscoverFilter.Country) && Offered!.CountryCodes.Contains(CountryValue) ? CountryValue : null;
        private string? Language => Offers(DiscoverFilter.Language) && Offered!.LanguageCodes.Contains(LanguageValue) ? LanguageValue : null;
        private int? Season => Offers(DiscoverFilter.Season) && SeasonValue is >= 1 and <= 4 ? SeasonValue : null;
        private int? Year => Offers(DiscoverFilter.Year) && YearValue is >= FirstYear and <= 2200 ? YearValue : null;
        private int? YearFrom => Offers(DiscoverFilter.Year) ? Bounded(YearFromValue, 1800, 2200) : null;
        private int? YearTo => Offers(DiscoverFilter.Year) ? Bounded(YearToValue, 1800, 2200) : null;
        private bool IncludesAdult => Offers(DiscoverFilter.Adult) && Adult;

        private (int? From, int? To) Range(DiscoverFilter filter, int? from, int? to) => Offers(filter) ? (Bounded(from, 0, 100000), Bounded(to, 0, 100000)) : (null, null);

        // Each filter set counts once; the type alone shows the shelves.
        private int FilterCount => new[]
        {
            Genres.Length > 0, Formats.Length > 0, Statuses.Length > 0, Sources.Length > 0, Tags.Length > 0, Sites.Length > 0, Country is not null, Language is not null, Season is not null,
            Year is not null || YearFrom is not null || YearTo is not null, IncludesAdult,
            Range(DiscoverFilter.Episodes, EpisodesFromValue, EpisodesToValue) != (null, null), Range(DiscoverFilter.Chapters, ChaptersFromValue, ChaptersToValue) != (null, null),
            Range(DiscoverFilter.Volumes, VolumesFromValue, VolumesToValue) != (null, null), Range(DiscoverFilter.Duration, DurationFromValue, DurationToValue) != (null, null)
        }.Count(set => set);

        private bool Filtering => string.IsNullOrWhiteSpace(Query) && FilterCount > 0;

        // An order of the grid other than popularity, when the one chosen type offers it.
        private DiscoverSort? FilterSort => Filtering && View is null && Chosen.Count == 1 && Offered is { } offered
            && SortSlugs.Where(sort => sort.Slug == Sort).Select(sort => (DiscoverSort?)sort.Sort).FirstOrDefault() is { } chosen
            && chosen != DiscoverSort.Popularity && offered.Sorts.Contains(chosen) ? chosen : null;

        private async Task LoadFilterOptionsAsync()
        {
            foreach (var scope in Chosen.Where(scope => !_filterOptions.ContainsKey(scope.Slug)))
            {
                _filterOptions[scope.Slug] = await filterOptions.ExecuteAsync<DiscoverFilterOptionsV1>(new ReadDiscoverFilterOptionsDataV1(scope.Slug, Chosen.Count == 1),
                    HttpContext.RequestAborted);
            }
        }

        // One page of each chosen type, together, the types taking turns title by title; a type that does not answer leaves the others.
        private async Task LoadFilteredAsync()
        {
            var (episodesFrom, episodesTo) = Range(DiscoverFilter.Episodes, EpisodesFromValue, EpisodesToValue);
            var (chaptersFrom, chaptersTo) = Range(DiscoverFilter.Chapters, ChaptersFromValue, ChaptersToValue);
            var (volumesFrom, volumesTo) = Range(DiscoverFilter.Volumes, VolumesFromValue, VolumesToValue);
            var (durationFrom, durationTo) = Range(DiscoverFilter.Duration, DurationFromValue, DurationToValue);
            var reads = Chosen.Where(scope => _filterOptions.GetValueOrDefault(scope.Slug) is { Filters.Length: > 0 } options && Genres.All(options.GenreIds.Contains)).Select(async scope =>
            {
                var data = new ReadDiscoverFilteredDataV1(scope.Slug, PageNumber, FilterSort, Genres, Tags, Formats, Statuses, Sources, Sites, Country, Language, Season, YearFrom ?? Year,
                    YearTo ?? Year, episodesFrom, episodesTo, chaptersFrom, chaptersTo, volumesFrom, volumesTo, durationFrom, durationTo, IncludesAdult);
                try
                {
                    return (Scope: scope, Page: await filtered.ExecuteAsync<DiscoverFilteredV1>(data, HttpContext.RequestAborted));
                }
                catch (ServiceUnavailableException exception)
                {
                    _providerUnavailable = true;
                    if (exception.Code == "tvdb_key_missing") _providerNotice = "discover.tvdbKeyMissing";
                    return (Scope: scope, Page: (DiscoverFilteredV1?)null);
                }
            });

            var pages = await Task.WhenAll(reads);
            var results = new List<DiscoverItemV1>();
            var catalog = new List<(DiscoverScopeV1 Scope, DiscoverCatalogItemV1 Item)>();
            for (var place = 0; pages.Any(page => page.Page is { } found && found.Items.Count + found.CatalogItems.Count > place); place++)
            {
                foreach (var (scope, page) in pages)
                {
                    if (page is null) continue;
                    if (place < page.Items.Count) results.Add(page.Items[place]);
                    if (place < page.CatalogItems.Count) catalog.Add((scope, page.CatalogItems[place]));
                }
            }

            _results = results;
            _catalog = catalog;
        }

        // Genres, year, season and format at hand, More opens the rest in the filter drawer; one GET form, the drawer's choices travel along hidden.
        private IUiComponent? FilterRow()
        {
            if (Offered is not { } offered || !string.IsNullOrWhiteSpace(Query))
            {
                return null;
            }

            var parts = new List<IUiComponent>();
            if (Offers(DiscoverFilter.Genre))
            {
                // AniList's adult genre shows only with adult titles.
                parts.Add(PopupFilter("genre", "discover.filter.genres", [.. offered.GenreIds.Where(genre => genre != Genre.Hentai || IncludesAdult).Select(genre => new ChoicePick(genre.ToString(), Genres.Contains(genre), "discover.genre." + genre))],
                    Genres.Select(genre => texts["discover.genre." + genre])));
            }

            if (Offers(DiscoverFilter.Year))
            {
                var years = Enumerable.Range(FirstYear, DateTime.UtcNow.Year + 2 - FirstYear).Reverse()
                    .Select(year => new ChoiceOption("", year.ToString(CultureInfo.InvariantCulture), Year == year, year.ToString(CultureInfo.InvariantCulture)));
                parts.Add(SelectFilter("year", "discover.filter.year", [new("discover.filter.any", "", Year is null), .. years]));
            }

            if (Offers(DiscoverFilter.Season))
            {
                parts.Add(SelectFilter("season", "discover.filter.season", [new("discover.filter.any", "", Season is null),
                    .. Enumerable.Range(1, 4).Select(season => new ChoiceOption("discover.season." + season, season.ToString(CultureInfo.InvariantCulture), Season == season))]));
            }

            if (Offers(DiscoverFilter.Format) && offered.CatalogFormatIds.Length > 1)
            {
                parts.Add(PopupFilter("format", "discover.filter.format", [.. offered.CatalogFormatIds.Select(format => new ChoicePick(format.ToString(), Formats.Contains(format), "discover.format." + format))],
                    Formats.Select(format => texts["discover.format." + format])));
            }

            // More counts what it holds (the availability too); clearing keeps only the types.
            var more = (Availability is not null ? 1 : 0) + FilterCount - (Genres.Length > 0 ? 1 : 0) - (Year is not null || YearFrom is not null || YearTo is not null ? 1 : 0) - (Season is not null ? 1 : 0) - (Formats.Length > 0 ? 1 : 0);
            var moreLabel = new List<IUiComponent> { new Icon("sliders", assets, 16), new Span(new Text("discover.filter.more")) };
            if (more > 0) moreLabel.Add(new Span(new ValueText(more.ToString(CultureInfo.CurrentCulture))).Class("capsule-bar__badge"));
            parts.Add(Drawer.Opener(new Button([.. moreLabel]).Class("button discover-filter__more"), "discover-panel", "filter"));
            if (FilterCount > 0)
            {
                parts.Add(new A(new Icon("close", assets, 16), new Span(new Text("discover.filter.clear"))).Class("button discover-filter__clear")
                    .Attribute("href", Address(Current with { Sort = null, Filters = false, Page = 1 })));
            }

            parts.AddRange(Hidden(except: ["genre", "year", "season", "format"]));
            return new FormElement([.. parts]).Class("discover-filters").Attribute("action", "/").Attribute("method", "get").Attribute("data-discover-filters", "")
                .Attribute("aria-label", texts["discover.filters"]);
        }

        // The drawer's part: the filters not at hand, then the row's own as hidden fields.
        private IEnumerable<IUiComponent> MoreFilters()
        {
            if (Offered is not { } offered)
            {
                yield break;
            }

            if (Offers(DiscoverFilter.Status))
            {
                yield return new ChoiceGroup(assets, "discover.filter.status", "status", [.. offered.ReleaseStatusIds.Select(status => new ChoicePick(status.ToString(), Statuses.Contains(status),
                    "discover.status." + status))], new ChoiceOptions(Multiple: true));
            }

            if (Offers(DiscoverFilter.Country))
            {
                yield return new ChoiceGroup(assets, "discover.filter.country", "country", [new("", Country is null, "discover.filter.any"),
                    .. offered.CountryCodes.Select(code => new ChoicePick(code, Country == code, "discover.country." + code))]);
            }

            if (Offers(DiscoverFilter.Language))
            {
                yield return new ChoiceGroup(assets, "discover.filter.language", "lang", [new("", Language is null, "discover.filter.any"),
                    .. offered.LanguageCodes.Select(code => new ChoicePick(code, Language == code, "discover.language." + code))]);
            }

            if (Offers(DiscoverFilter.Source))
            {
                yield return new ChoiceGroup(assets, "discover.filter.source", "source", [.. offered.CatalogSourceIds.Select(source => new ChoicePick(source.ToString(), Sources.Contains(source),
                    "discover.source." + source))], new ChoiceOptions(Multiple: true));
            }

            if (Offers(DiscoverFilter.Site) && offered.Sites.Count > 0)
            {
                var reading = Chosen[0].MediaTypeId == MediaType.Book;
                yield return new ChoiceGroup(assets, reading ? "discover.filter.readable" : "discover.filter.streaming", "site", [.. offered.Sites.Select(site => new ChoicePick(
                    site.Id.ToString(CultureInfo.InvariantCulture), Sites.Contains(site.Id), Label: site.Name))], new ChoiceOptions(Multiple: true, Layout: ChoiceLayout.Checks));
            }

            // Tags by category, adult ones only with adult titles; a chosen tag opens its category.
            if (Offers(DiscoverFilter.Tag) && offered.Tags.Count > 0)
            {
                var categories = offered.Tags.Where(tag => IncludesAdult || !tag.IsAdult).GroupBy(tag => tag.Category ?? "").Select(category =>
                {
                    var group = new Framework.Details(new Summary(new ValueText(category.Key)), new Div([.. category.Select(tag =>
                    {
                        var box = new Input().Attribute("type", "checkbox").Attribute("name", "tag").Attribute("value", tag.Id.ToString(CultureInfo.InvariantCulture));
                        if (Tags.Contains(tag.Id)) box.Attribute("checked", "checked");
                        return (IUiComponent)new Label(box, new Span(new ValueText(tag.Name))).Class("choice-group__check");
                    })]).Class("choice-group choice-group--checks discover-tags__items")).Class("discover-tags__category");
                    return (IUiComponent)(category.Any(tag => Tags.Contains(tag.Id)) ? group.Attribute("open", "open") : group);
                });
                yield return new Div(new Span(new Text("discover.filter.tags")).Class("drawer__field-title"), new Div([.. categories]).Class("discover-tags")).Class("drawer__section");
            }

            var ranges = new List<IUiComponent>();
            if (Offers(DiscoverFilter.Year)) ranges.Add(RangeInputs("discover.filter.years", "year", YearFrom, YearTo));
            if (Offers(DiscoverFilter.Episodes)) ranges.Add(RangeInputs("discover.filter.episodes", "episodes", EpisodesFromValue, EpisodesToValue));
            if (Offers(DiscoverFilter.Chapters)) ranges.Add(RangeInputs("discover.filter.chapters", "chapters", ChaptersFromValue, ChaptersToValue));
            if (Offers(DiscoverFilter.Volumes)) ranges.Add(RangeInputs("discover.filter.volumes", "volumes", VolumesFromValue, VolumesToValue));
            if (Offers(DiscoverFilter.Duration)) ranges.Add(RangeInputs("discover.filter.duration", "duration", DurationFromValue, DurationToValue));
            if (ranges.Count > 0) yield return new Div([.. ranges]).Class("drawer__section discover-ranges");

            if (Offers(DiscoverFilter.Adult))
            {
                var toggle = new Toggle("discover.filter.adult", IncludesAdult);
                toggle.Control.Attribute("name", "adult");
                yield return toggle;
            }

            foreach (var field in Hidden(except: ["type", "availability", "feed", "status", "country", "lang", "source", "site", "tag", "yearFrom", "yearTo", "episodesFrom", "episodesTo",
                "chaptersFrom", "chaptersTo", "volumesFrom", "volumesTo", "durationFrom", "durationTo", "adult"]))
            {
                yield return field;
            }
        }

        // The grid's order, when the one chosen type offers more than popularity.
        private IUiComponent? FilterHeading()
        {
            if (Chosen.Count != 1 || Offered is not { Sorts.Length: > 1 } offered)
            {
                return null;
            }

            var keep = new Dictionary<string, string>();
            if (TypeParameter(Current.Types) is { } types) keep["type"] = types;
            if (Availability is not null) keep["availability"] = Availability;
            KeepFilters(keep);
            var shown = FilterSort ?? DiscoverSort.Popularity;
            return new Div(new ChoiceSelect("/", "sort", "discover.sort", [.. SortSlugs.Where(sort => offered.Sorts.Contains(sort.Sort))
                .Select(sort => new ChoiceOption("discover.sort." + sort.Sort, sort.Sort == DiscoverSort.Popularity ? "" : sort.Slug, sort.Sort == shown))], keep)).Class("discover-view-heading");
        }

        // The filters in the address, in one order, lists as comma lists.
        private IEnumerable<string> FilterValues()
        {
            foreach (var (name, value) in FilterFields())
            {
                yield return name + "=" + Uri.EscapeDataString(value).Replace("%2C", ",");
            }
        }

        private void KeepFilters(Dictionary<string, string> keep)
        {
            foreach (var (name, value) in FilterFields()) keep[name] = value;
        }

        private IEnumerable<(string Name, string Value)> FilterFields()
        {
            static string Join<T>(IEnumerable<T> values) => string.Join(',', values.Select(value => Convert.ToString(value, CultureInfo.InvariantCulture)));
            if (Genres.Length > 0) yield return ("genre", Join(Genres));
            if (Year is { } year) yield return ("year", year.ToString(CultureInfo.InvariantCulture));
            if (Season is { } season) yield return ("season", season.ToString(CultureInfo.InvariantCulture));
            if (Formats.Length > 0) yield return ("format", Join(Formats));
            if (Statuses.Length > 0) yield return ("status", Join(Statuses));
            if (Country is { } country) yield return ("country", country);
            if (Language is { } language) yield return ("lang", language);
            if (Sources.Length > 0) yield return ("source", Join(Sources));
            if (Sites.Length > 0) yield return ("site", Join(Sites));
            if (Tags.Length > 0) yield return ("tag", Join(Tags));
            if (YearFrom is { } from) yield return ("yearFrom", from.ToString(CultureInfo.InvariantCulture));
            if (YearTo is { } to) yield return ("yearTo", to.ToString(CultureInfo.InvariantCulture));
            foreach (var (filter, name, low, high) in new[]
            {
                (DiscoverFilter.Episodes, "episodes", EpisodesFromValue, EpisodesToValue), (DiscoverFilter.Chapters, "chapters", ChaptersFromValue, ChaptersToValue),
                (DiscoverFilter.Volumes, "volumes", VolumesFromValue, VolumesToValue), (DiscoverFilter.Duration, "duration", DurationFromValue, DurationToValue)
            })
            {
                var (rangeFrom, rangeTo) = Range(filter, low, high);
                if (rangeFrom is { } bottom) yield return (name + "From", bottom.ToString(CultureInfo.InvariantCulture));
                if (rangeTo is { } top) yield return (name + "To", top.ToString(CultureInfo.InvariantCulture));
            }

            if (IncludesAdult) yield return ("adult", "true");
        }

        // The page's other choices as hidden fields of a filter form, so that submitting one form keeps the other's.
        private IEnumerable<IUiComponent> Hidden(IReadOnlyCollection<string> except)
        {
            var values = new List<(string Name, string Value)>();
            if (TypeParameter(Current.Types) is { } types) values.Add(("type", types));
            if (Availability is not null) values.Add(("availability", Availability));
            if (FilterSort is { } sort) values.Add(("sort", SortSlugs.First(entry => entry.Sort == sort).Slug));
            values.AddRange(FilterFields());
            return values.Where(value => !except.Contains(value.Name))
                .Select(value => (IUiComponent)new Input().Attribute("type", "hidden").Attribute("name", value.Name).Attribute("value", value.Value));
        }

        // A button with the chosen values that opens its choices over the page; closing it applies them.
        private IUiComponent PopupFilter(string name, string labelKey, IReadOnlyList<ChoicePick> picks, IEnumerable<string> chosen)
        {
            var id = "discover-filter-" + name;
            var summary = string.Join(", ", chosen);
            return new Div(
                new Span(new Text(labelKey)).Class("discover-filter__label").Attribute("id", id + "-label"),
                new Button(new Span(summary.Length > 0 ? new ValueText(summary) : new Text("discover.filter.any")).Class("discover-filter__value"))
                    .Class("discover-filter__button").Attribute("popovertarget", id).Attribute("aria-haspopup", "dialog").Attribute("aria-expanded", "false")
                    .Attribute("aria-describedby", id + "-label"),
                new Div(new ChoiceGroup(assets, labelKey, name, picks, new ChoiceOptions(Multiple: true, Layout: ChoiceLayout.Checks))).Class("discover-filter__popup")
                    .Attribute("id", id).Attribute("popover", "auto")).Class("discover-filter" + (summary.Length > 0 ? " discover-filter--set" : ""));
        }

        private IUiComponent SelectFilter(string name, string labelKey, IReadOnlyList<ChoiceOption> options)
        {
            var id = "discover-filter-" + name;
            return new Div(new Label(new Text(labelKey)).Class("discover-filter__label").Attribute("for", id),
                new Select([.. options.Select(option =>
                {
                    var item = new Option(option.LabelValue is { } value ? new ValueText(value) : new Text(option.LabelKey)).Attribute("value", option.Value);
                    return (IUiComponent)(option.Selected ? item.Attribute("selected", "selected") : item);
                })]).Attribute("id", id).Attribute("name", name).Class("discover-filter__select"))
                .Class("discover-filter discover-filter--select" + (options.Any(option => option.Selected && option.Value.Length > 0) ? " discover-filter--set" : ""));
        }

        private static IUiComponent RangeInputs(string labelKey, string name, int? from, int? to)
        {
            IUiComponent Field(string part, string partKey, int? value)
            {
                var input = new Input().Attribute("type", "number").Attribute("inputmode", "numeric").Attribute("min", "0").Attribute("name", name + part)
                    .Attribute("id", "discover-range-" + name + part);
                if (value is { } number) input.Attribute("value", number.ToString(CultureInfo.InvariantCulture));
                return new Label(new Span(new Text(partKey)), input).Class("discover-range__field");
            }

            return new Div(new Span(new Text(labelKey)).Class("drawer__field-title"),
                new Div(Field("From", "discover.filter.from", from), Field("To", "discover.filter.to", to)).Class("discover-range__fields")).Class("discover-range");
        }

        // A list sent as a comma list or repeated; unknown values are left out.
        private static IEnumerable<T> Parsed<T>(string[]? values) where T : struct, Enum =>
            Split(values).Select(value => Enum.TryParse<T>(value, true, out var parsed) && Enum.IsDefined(parsed) && !int.TryParse(value, out _) ? (T?)parsed : null).OfType<T>().Distinct();

        private static int[] Numbers(string[]? values) =>
            [.. Split(values).Select(value => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number > 0 ? number : 0).Where(number => number > 0).Distinct().Order().Take(20)];

        private static IEnumerable<string> Split(string[]? values) => (values ?? []).SelectMany(value => value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        private static int? Bounded(int? value, int minimum, int maximum) => value is { } number && number >= minimum && number <= maximum ? number : null;
    }
}
