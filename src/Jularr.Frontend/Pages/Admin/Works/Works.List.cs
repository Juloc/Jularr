using System.Globalization;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Media.V1;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Frontend.Framework;

namespace Jularr.Frontend.Pages.Admin.Works
{
    public sealed partial class WorksModel
    {
        // The form that the chosen titles belong to; the dock's bulk buttons and the change drawer carry it.
        private const string BulkForm = "works-bulk";

        // The columns after the title and the status, in their first order: key, the order it sorts by, when it leaves, its grid track and whether it starts out of view.
        // What a person hides or moves stays in their browser; the tracks are the page stylesheet's default grid, the script builds the same from this choice.
        private static readonly (string Key, WorkSortType? Sort, int Hide, string Width, bool Hidden)[] Columns =
        [
            ("type", WorkSortType.Type, 1, "minmax(80px, .8fr)", true), ("year", WorkSortType.Year, 2, "minmax(60px, .6fr)", true), ("profile", null, 1, "minmax(100px, 1fr)", false),
            ("library", null, 2, "minmax(100px, 1fr)", false), ("files", WorkSortType.Files, 1, "minmax(56px, .6fr)", true), ("size", WorkSortType.Size, 0, "minmax(80px, .8fr)", false),
            ("added", WorkSortType.Added, 1, "minmax(90px, .9fr)", true)
        ];

        private static readonly WorkListStatus[] Statuses = [WorkListStatus.Available, WorkListStatus.Missing, WorkListStatus.Downloading, WorkListStatus.Unmonitored];

        private bool Filtered => TypeFilter is not null || MonitoredFilter is not null || StatusFilter is not null || LibraryFilter is not null || ProfileFilter is not null || !string.IsNullOrEmpty(QueryFilter);

        // The filters of the drawer (the chips and the title field are the list's own controls).
        private bool DrawerFiltered => TypeFilter is not null || MonitoredFilter is not null || LibraryFilter is not null || ProfileFilter is not null;

        // Titles can be chosen for the mass editor by whoever may change them.
        private bool Selectable => CanManageWanted || CanManageMedia;

        // The address of the list with its filters and order, or of a handler that returns to it; paging, sorting and the chosen chip change one part of it.
        private string Address(string? handler = null, int? pageNumber = null, WorkSortType? sort = null, bool? descending = null, bool clearFilters = false, WorkListStatus? status = null, bool anyStatus = false, WorksViewType? view = null)
        {
            var values = new List<string>();
            void Add(string name, string? value)
            {
                if (!string.IsNullOrEmpty(value)) values.Add(name + "=" + Uri.EscapeDataString(value));
            }

            if (!clearFilters)
            {
                Add("type", TypeFilter is { } type ? ((byte)type).ToString(CultureInfo.InvariantCulture) : null);
                Add("monitored", MonitoredFilter is { } monitored ? (monitored ? "true" : "false") : null);
                Add("status", (anyStatus ? null : status ?? StatusFilter) is { } chosen ? ((byte)chosen).ToString(CultureInfo.InvariantCulture) : null);
                Add("library", LibraryFilter?.ToString(CultureInfo.InvariantCulture));
                Add("profile", ProfileFilter?.ToString(CultureInfo.InvariantCulture));
                Add("q", QueryFilter);
            }

            var order = sort ?? Sort;
            Add("sort", order == WorkSortType.Title ? null : ((byte)order).ToString(CultureInfo.InvariantCulture));
            Add("desc", (descending ?? Descending) ? "true" : null);
            if (pageNumber is > 1) Add("pageNumber", pageNumber.Value.ToString(CultureInfo.InvariantCulture));
            Add("view", view?.ToString());
            Add("handler", handler);
            return "/Admin/Works" + (values.Count == 0 ? "" : "?" + string.Join('&', values));
        }

        private IUiComponent Glyph(string name, int size = 20) => new Icon(name, assets, size);

        private static HtmlElement Choice(string value, IUiComponent label, bool selected)
        {
            var option = new Option(label).Attribute("value", value);
            return selected ? option.Attribute("selected", "selected") : option;
        }

        // One filter: the first choice lets everything through and names the filter.
        private static HtmlElement FilterSelect(string name, string labelKey, string? current, IEnumerable<(string Value, IUiComponent Label)> choices) => new Select([Choice("", new Text(labelKey), current is null),
            .. choices.Select(choice => Choice(choice.Value, choice.Label, choice.Value == current))]).Attribute("name", name).Attribute("data-works-filter", "").TextAttribute("aria-label", labelKey).Class("works-select" + (current is null ? "" : " works-select--set"));

        private int? CountOf(WorkListStatus? status) => status switch
        {
            null => _summary?.TotalCount,
            WorkListStatus.Available => _summary?.AvailableCount,
            WorkListStatus.Missing => _summary?.MissingCount,
            WorkListStatus.Downloading => _summary?.DownloadingCount,
            _ => _summary?.UnmonitoredCount
        };

        // The one row of filters: a chip per state with its count, the title field and the button of the other filters.
        private IUiComponent Toolbar()
        {
            var chips = new List<NavigationTab> { new("common.all", Address(anyStatus: true), StatusFilter is null, Count: _summary?.TotalCount, Icon: Glyph("layers", 14)) };
            chips.AddRange(Statuses.Select(status => new NavigationTab("works.status." + status, Address(status: status), StatusFilter == status, Count: CountOf(status), Icon: Glyph(StatusIcon(status), 14))));

            var inv = CultureInfo.InvariantCulture;
            var keep = new Dictionary<string, string>();
            if (TypeFilter is { } type) keep["type"] = ((byte)type).ToString(inv);
            if (MonitoredFilter is { } monitored) keep["monitored"] = monitored ? "true" : "false";
            if (StatusFilter is { } status2) keep["status"] = ((byte)status2).ToString(inv);
            if (LibraryFilter is { } library) keep["library"] = library.ToString(inv);
            if (ProfileFilter is { } profile) keep["profile"] = profile.ToString(inv);
            if (Sort != WorkSortType.Title) keep["sort"] = ((byte)Sort).ToString(inv);
            if (Descending) keep["desc"] = "true";

            var filters = Drawer.Opener(new Button(Glyph("filter", 16)).IconOnly(texts["works.filter.drawer"]), "works-filter");
            if (DrawerFiltered) filters.Class("button button--icon button--soft");
            var views = new ViewSwitch("works.views", [new ViewChoice("works.view.Posters", Address(view: WorksViewType.Posters), Glyph("grid", 16), _view == WorksViewType.Posters),
                new ViewChoice("works.view.Overview", Address(view: WorksViewType.Overview), Glyph("overview", 16), _view == WorksViewType.Overview),
                new ViewChoice("works.view.Table", Address(view: WorksViewType.Table), Glyph("table", 16), _view == WorksViewType.Table)]);
            return new ListToolbar(new NavigationTabs(chips, "works.filters", "filters"), new SearchBox("/Admin/Works", "works.search", QueryFilter, keep, null, Glyph("search", 15)), views, filters);
        }

        // The icon and tone of a state, as its chip and its chips' filter say it.
        private static (UiTone Tone, string Icon) StatusLook(WorkListStatus status) => status switch
        {
            WorkListStatus.Available => (UiTone.Success, "check"),
            WorkListStatus.Downloading => (UiTone.Info, "download"),
            WorkListStatus.Missing => (UiTone.Warning, "alert"),
            _ => (UiTone.Neutral, "pause")
        };

        private static string StatusIcon(WorkListStatus status) => StatusLook(status).Icon;

        // The filters of the toolbar's button, applied by the form of the drawer; the chip, the order and the title words stay.
        private IUiComponent FilterDrawer()
        {
            var inv = CultureInfo.InvariantCulture;
            var body = new List<IUiComponent>
            {
                Field("type", "works.col.type", FilterSelect("type", "works.filter.type", TypeFilter is { } type ? ((byte)type).ToString(inv) : null,
                    Enum.GetValues<MediaType>().Select(value => (((byte)value).ToString(inv), (IUiComponent)new Text("media.type." + value))))),
                Field("monitored", "works.col.monitored", FilterSelect("monitored", "works.filter.monitored", MonitoredFilter is { } monitored ? (monitored ? "true" : "false") : null,
                    [("true", new Text("works.filter.monitored.yes")), ("false", new Text("works.filter.monitored.no"))])),
                Field("library", "works.col.library", FilterSelect("library", "works.filter.library", LibraryFilter?.ToString(inv), _libraries.Select(library => (library.Id.ToString(inv), (IUiComponent)new ValueText(library.Name)))))
            };

            if (CanAcquire && _profiles.Count > 0)
            {
                body.Add(Field("profile", "works.col.profile", FilterSelect("profile", "works.filter.profile", ProfileFilter?.ToString(inv), _profiles.Select(profile => (profile.Id.ToString(inv), (IUiComponent)new ValueText(profile.Name))))));
            }

            if (StatusFilter is { } status) body.Add(new Input().Attribute("type", "hidden").Attribute("name", "status").Attribute("value", ((byte)status).ToString(inv)));
            if (Sort != WorkSortType.Title) body.Add(new Input().Attribute("type", "hidden").Attribute("name", "sort").Attribute("value", ((byte)Sort).ToString(inv)));
            if (Descending) body.Add(new Input().Attribute("type", "hidden").Attribute("name", "desc").Attribute("value", "true"));
            if (!string.IsNullOrEmpty(QueryFilter)) body.Add(new Input().Attribute("type", "hidden").Attribute("name", "q").Attribute("value", QueryFilter));

            var footer = new List<IUiComponent> { new Button(new Text("works.filter.apply")).Primary().Submit() };
            if (DrawerFiltered) footer.Insert(0, new A(new Text("works.filter.clear")).Class("button").Attribute("href", AddressWithoutDrawerFilters()));
            return new Drawer(assets, "works-filter", [new DrawerPane("filter", "works.filter.drawer", "works.filter.drawer", "filter", body, footer, "/Admin/Works")]);
        }

        // The list without the drawer's filters: the chip, the words and the order stay.
        private string AddressWithoutDrawerFilters()
        {
            var values = new List<string>();
            if (StatusFilter is { } status) values.Add("status=" + ((byte)status).ToString(CultureInfo.InvariantCulture));
            if (!string.IsNullOrEmpty(QueryFilter)) values.Add("q=" + Uri.EscapeDataString(QueryFilter));
            if (Sort != WorkSortType.Title) values.Add("sort=" + ((byte)Sort).ToString(CultureInfo.InvariantCulture));
            if (Descending) values.Add("desc=true");
            return "/Admin/Works" + (values.Count == 0 ? "" : "?" + string.Join('&', values));
        }

        private static IUiComponent Field(string name, string titleKey, IUiComponent control) =>
            new Div(new Span(new Text(titleKey)).Class("drawer__field-title"), control).Class("works-field").Attribute("data-field", name);

        // What the chosen titles are set to: the mass editor's drawer. Its controls belong to the bulk form, so Save posts them with the chosen titles.
        private IUiComponent ChangeDrawer()
        {
            var inv = CultureInfo.InvariantCulture;
            var body = new List<IUiComponent>
            {
                Field("monitoring", "works.bar.monitoring", new Select(Choice("", new Text("works.bar.keep"), true), Choice("on", new Text("works.filter.monitored.yes"), false), Choice("off", new Text("works.filter.monitored.no"), false))
                    .Attribute("name", "monitoring").Attribute("form", BulkForm).TextAttribute("aria-label", "works.bar.monitoring").Class("works-select"))
            };

            if (CanAcquire && _profiles.Count > 0)
            {
                body.Add(Field("profile", "works.bar.profile", new Select([Choice("", new Text("works.bar.keep"), true), Choice("0", new Text("works.bar.default"), false),
                    .. _profiles.Select(profile => Choice(profile.Id.ToString(inv), new ValueText(profile.Name), false))]).Attribute("name", "profileId").Attribute("form", BulkForm).TextAttribute("aria-label", "works.bar.profile").Class("works-select")));
            }

            // New files only: the files that are filed stay where they are.
            body.Add(Field("library", "works.bar.library", new Select([Choice("", new Text("works.bar.keep"), true), Choice("0", new Text("works.bar.default"), false),
                .. _libraries.Select(library => Choice(library.Id.ToString(inv), new ValueText(library.Name), false))]).Attribute("name", "libraryId").Attribute("form", BulkForm).TextAttribute("aria-label", "works.bar.library").Class("works-select")));

            var save = new Button(new Text("common.save")).Primary().Submit().Attribute("form", BulkForm).Attribute("name", "action").Attribute("value", "apply");
            return new Drawer(assets, "works-change", [new DrawerPane("change", "works.bulk.title", "works.bulk.title", "sliders", body, [save])]);
        }

        // The titles in the view the person chose; an empty list always shows the table's empty state.
        private IUiComponent Titles(IReadOnlyList<WorkListItemV1> items, string? csrf) => items.Count == 0 ? ListTable(items, csrf) : _view switch
        {
            WorksViewType.Posters => new Div([.. items.Select(item => PosterCard(item, csrf))]).Class("media-grid works-posters"),
            WorksViewType.Overview => OverviewList(items, csrf),
            _ => ListTable(items, csrf)
        };

        // A cover with its box and its monitoring on it, the state over its foot, and the title's page to edit; the title and its facts follow.
        private IUiComponent PosterCard(WorkListItemV1 item, string? csrf)
        {
            var id = item.Id.ToString(CultureInfo.InvariantCulture);
            var card = new MediaCard(assets, item.CanonicalTitle, "/Admin/Works/" + id, item.PosterUrl).Facts(item.ReleaseYear, MediaTypeLabel.Key(item.MediaTypeId, item.MediaSubtypeId), null).Label(StatusChip(item));
            if (Selectable) card.Select(ListIdentity.Select("workIds", id, BulkForm, "works.select"));
            if (item.WantedItemId is not null && item.MediaTypeId != MediaType.Game && CanManageWanted)
            {
                var label = texts[item.IsMonitored ? "work.monitor.stop" : "work.monitor.start"];
                card.Bookmark(new PostForm(Address("Monitor"), csrf, [("workId", id), ("isMonitored", item.IsMonitored ? "false" : "true")],
                    new Button(Glyph("bookmark", 17)).IconOnly(label).Submit().Attribute("aria-pressed", item.IsMonitored ? "true" : "false").Attribute("title", label)));
            }

            return card.Action(new A(Glyph("pencil", 16), new Span(new Text("work.edit")).Class("media-card__primary-label")).Class("button button--primary button--compact media-card__primary").Attribute("href", "/Admin/Works/" + id));
        }

        // Poster, synopsis and facts: a larger cover, the title with its type and year, the start of its synopsis and what it holds, then its state and actions.
        private IUiComponent OverviewList(IReadOnlyList<WorkListItemV1> items, string? csrf)
        {
            var columns = new List<ListColumn> { new("media.title", ListRole.Main, Head: TitleHead()), new("works.col.status", ListRole.Status), new("common.actions", ListRole.Actions) };
            return new ListPanel("works.list", columns, [.. items.Select(item => OverviewRow(item, csrf))]).Modifier("works-overview");
        }

        private ListRow OverviewRow(WorkListItemV1 item, string? csrf)
        {
            var id = item.Id.ToString(CultureInfo.InvariantCulture);
            var type = new Text(MediaTypeLabel.Key(item.MediaTypeId, item.MediaSubtypeId));
            var subline = item.ReleaseYear is { } year ? (IUiComponent)new Span(type, new ValueText(" · " + year.ToString(CultureInfo.InvariantCulture))) : type;
            var more = new List<IUiComponent>();
            if (item.Overview is { Length: > 0 } overview) more.Add(new Truncated(overview));
            var facts = new List<string>();
            if (item.ProfileName is { } profile) facts.Add(profile);
            if (item.LibraryName is { } library) facts.Add(library);
            if (item.FileCount > 0) facts.Add(ByteSize.Format(item.SizeBytes));
            if (facts.Count > 0) more.Add(new Truncated(string.Join(" · ", facts)));
            var identity = new ListIdentity(new ListPoster(item.PosterUrl is { } poster ? new MediaImage(poster, "", 56, 84) : null, large: true),
                new A(new Truncated(item.CanonicalTitle)).Class("list-identity__title").Attribute("href", "/Admin/Works/" + id), subline,
                select: Selectable ? ListIdentity.Select("workIds", id, BulkForm, "works.select") : null, more: more);
            return new ListRow([identity, StatusChip(item), Actions(item, csrf)]);
        }

        private IUiComponent ListTable(IReadOnlyList<WorkListItemV1> items, string? csrf)
        {
            var columns = new List<ListColumn>
            {
                new("media.title", ListRole.Main, Key: "title", Width: "minmax(220px, 2.6fr)", Fixed: true, Head: TitleHead()),
                new("works.col.status", ListRole.Status, Key: "status", Width: "minmax(138px, 1fr)")
            };
            foreach (var (key, sort, hide, width, hidden) in Columns)
            {
                columns.Add(new ListColumn("works.col." + key, ListRole.Data, hide, key, width, false, hidden, sort is { } order ? SortHead("works.col." + key, order) : null));
            }

            columns.Add(new ListColumn("common.actions", ListRole.Actions, Key: "actions", Width: "minmax(130px, 1fr)", Fixed: true));
            var empty = new Div(new IconTile(Glyph("search", 22), UiTone.Neutral), new P(new Text(Filtered ? "works.emptyFiltered" : "media.empty")),
                Filtered ? new A(new Text("works.filter.clear")).Class("button button--soft button--compact").Attribute("href", Address(clearFilters: true)) : new Span());
            return new ListPanel("works.list", columns, [.. items.Select(item => Row(item, csrf))], empty).Modifier("works-list").Pickable("works");
        }

        // A head that sorts: the link sets the order, the current one shows its arrow.
        private IUiComponent SortHead(string labelKey, WorkSortType order) =>
            ListPanel.SortLink(labelKey, Address(sort: order, descending: Sort == order && !Descending), Sort == order ? (Descending ? "descending" : "ascending") : null);

        // The title's head holds the select-all box where the rows hold theirs.
        private IUiComponent TitleHead()
        {
            var link = SortHead("media.title", WorkSortType.Title);
            return Selectable ? ListIdentity.SelectAllHead(link, "works.selectAll") : link;
        }

        private ListRow Row(WorkListItemV1 item, string? csrf)
        {
            var id = item.Id.ToString(CultureInfo.InvariantCulture);
            var home = "/Admin/Works/" + id;
            var type = new Text(MediaTypeLabel.Key(item.MediaTypeId, item.MediaSubtypeId));
            var subline = item.ReleaseYear is { } year ? (IUiComponent)new Span(type, new ValueText(" · " + year.ToString(CultureInfo.InvariantCulture))) : type;
            var identity = new ListIdentity(new ListPoster(item.PosterUrl is { } poster ? new MediaImage(poster, "", 38, 56) : null),
                new A(new Truncated(item.CanonicalTitle)).Class("list-identity__title").Attribute("href", home), subline,
                select: Selectable ? ListIdentity.Select("workIds", id, BulkForm, "works.select") : null);
            var cells = new List<IUiComponent> { identity, StatusChip(item) };
            cells.AddRange(Columns.Select(column => ColumnCell(column.Key, item)));
            cells.Add(Actions(item, csrf));
            return new ListRow(cells, Caption(item));
        }

        private IUiComponent StatusChip(WorkListItemV1 item)
        {
            var (tone, icon) = StatusLook(item.WorkListStatusId);
            return new StateChip(new Text("works.status." + item.WorkListStatusId), tone, Glyph(icon, 14));
        }

        private static IUiComponent ColumnCell(string key, WorkListItemV1 item) => key switch
        {
            "type" => new Span(new Text(MediaTypeLabel.Key(item.MediaTypeId, item.MediaSubtypeId))).Class("list-value"),
            "year" => Value(item.ReleaseYear?.ToString(CultureInfo.InvariantCulture)),
            "profile" => Value(item.ProfileName),
            "library" => Value(item.LibraryName),
            "files" => Value(item.FileCount.ToString("N0", CultureInfo.CurrentCulture)),
            "size" => Value(item.FileCount > 0 ? ByteSize.Format(item.SizeBytes) : null),
            _ => Value(item.CreatedAt.ToLocalTime().ToString("d", CultureInfo.CurrentCulture))
        };

        // One line that ends in an ellipsis where it has no room; a missing value is a quiet dash.
        private static IUiComponent Value(string? text) => text is { Length: > 0 }
            ? new Span(new Truncated(text)).Class("list-value list-value--fill")
            : new Span(new ValueText("–")).Class("list-value list-value--dim");

        // A phone card's line of the facts its columns would show.
        private static IReadOnlyList<IUiComponent> Caption(WorkListItemV1 item)
        {
            var parts = new List<IUiComponent>();
            if (item.ProfileName is { } profile) parts.Add(ListPanel.Fact("works.col.profile", new ValueText(profile)));
            if (item.LibraryName is { } library) parts.Add(ListPanel.Fact("works.col.library", new ValueText(library)));
            if (item.FileCount > 0) parts.Add(ListPanel.Fact("works.col.size", new ValueText(ByteSize.Format(item.SizeBytes))));
            return parts;
        }

        // The title's own rule: monitored while its target is not paused. Titles that are not wanted, and games that are monitored per platform, only show it.
        private IUiComponent MonitorSwitch(WorkListItemV1 item, string? csrf)
        {
            if (item.WantedItemId is null) return new Span();
            if (item.MediaTypeId == MediaType.Game || !CanManageWanted)
            {
                return new Span(new Text(item.IsMonitored ? "work.monitor.on" : "work.monitor.off")).Class("work-switch work-switch--static" + (item.IsMonitored ? " work-switch--on" : ""));
            }

            return new PostForm(Address("Monitor"), csrf, [("workId", item.Id.ToString(CultureInfo.InvariantCulture)), ("isMonitored", item.IsMonitored ? "false" : "true")],
                new SwitchButton(item.IsMonitored, item.IsMonitored ? "work.monitor.stop" : "work.monitor.start"));
        }

        // The monitoring switch, search now as the title's page does, and the title's page to edit.
        private IUiComponent Actions(WorkListItemV1 item, string? csrf)
        {
            var id = item.Id.ToString(CultureInfo.InvariantCulture);
            var actions = new List<IUiComponent> { MonitorSwitch(item, csrf) };
            if (CanAcquire && item.WantedItemId is not null && item.MediaTypeId != MediaType.Game)
            {
                actions.Add(new PostForm(Address("SearchWork"), csrf, [("workId", id)],
                    new Button(Glyph("search", 18)).IconOnly(texts["work.search"]).Submit().TextAttribute("title", "work.search")));
            }

            actions.Add(new A(Glyph("pencil", 18)).Class("button button--icon").Attribute("href", "/Admin/Works/" + id).TextAttribute("aria-label", "work.edit").TextAttribute("title", "work.edit"));
            return new Div([.. actions]).Class("list-actions");
        }

        // The page's actions and the mass editor's, and the paging of the list, float at the bottom as the save pill does.
        private ActionDock Dock(PageResult<WorkListItemV1>? works)
        {
            var actions = new List<IUiComponent>();
            if (CanManageMedia) actions.Add(new Button(Glyph("plus", 16), new Span(new Text("works.add")).Class("action-dock__text")).Primary().Attribute("data-dialog-open", "works-add"));
            if (_view == WorksViewType.Table && works is { Items.Count: > 0 }) actions.Add(new Button(Glyph("settings", 16), new Span(new Text("work.columns")).Class("action-dock__text")).Attribute("data-columns-kind", "works"));

            var bulk = new List<IUiComponent>();
            if (CanAcquire) bulk.Add(BulkButton("search", "works.bar.search", "search"));
            if (CanManageMedia) bulk.Add(BulkButton("refresh", "works.bar.refresh", "sync"));
            if (CanManageWanted) bulk.Add(Drawer.Opener(new Button(Glyph("sliders", 16), new Span(new Text("works.bulk.change")).Class("action-dock__text")), "works-change"));
            if (CanManageMedia) bulk.Add(new Button(Glyph("trash", 16), new Span(new Text("work.delete")).Class("action-dock__text")).Attribute("data-works-delete", ""));
            return new ActionDock(assets, "works.actions", actions, Selectable ? bulk : null, works is null ? null : Paging(works));
        }

        private IUiComponent BulkButton(string action, string labelKey, string icon) =>
            new Button(Glyph(icon, 16), new Span(new Text(labelKey)).Class("action-dock__text")).Submit().Attribute("form", BulkForm).Attribute("name", "action").Attribute("value", action);

        // Page n of m while the count is known, else only the page; the next link stays while a full page may be followed by another.
        private DockPaging? Paging(PageResult<WorkListItemV1> works)
        {
            int? total = MonitoredFilter is null ? CountOf(StatusFilter) : null;
            var next = total is { } known ? works.Page * works.PageSize < known : works.Items.Count == works.PageSize;
            if (works.Page == 1 && !next) return null;
            var label = total is { } count
                ? string.Format(CultureInfo.CurrentCulture, texts["dock.page"], works.Page, Math.Max(1, (count + works.PageSize - 1) / works.PageSize))
                : string.Format(CultureInfo.CurrentCulture, texts["dock.pageOnly"], works.Page);
            return new DockPaging(works.Page > 1 ? Address(pageNumber: works.Page - 1) : null, next ? Address(pageNumber: works.Page + 1) : null, new ValueText(label));
        }

        // Sonarr's dialog for the chosen titles: the files go too when asked, and an exclusion keeps a request from bringing them back. The titles are written in when it opens.
        private IUiComponent DeleteDialog(string? csrf)
        {
            var files = new Toggle("work.delete.files");
            files.Control.Attribute("name", "deletesFiles");
            var exclusion = new Toggle("work.delete.exclusion");
            exclusion.Control.Attribute("name", "addsExclusion");
            return new Modal(assets, "works-delete", "works.delete.title",
                [new PostForm(Address("Delete"), csrf, [], new Div().Attribute("data-works-ids", ""), files, exclusion, new Button(new Text("works.delete.confirm")).Submit()).Class("works-delete-form")]);
        }

        // What happened to the titles of the last action: how many, and why the others stayed.
        private IUiComponent Report(string report)
        {
            var lines = report.Split('\n');
            var done = int.Parse(lines[1], CultureInfo.InvariantCulture);
            var total = int.Parse(lines[2], CultureInfo.InvariantCulture);
            var summary = total == 0 ? texts["works.report.none"] : string.Format(CultureInfo.CurrentCulture, texts["works.report"], texts["works.action." + lines[0]], done, total);
            var stayed = lines.Skip(3).Select(line => line.Split('\t', 2)).Where(parts => parts.Length == 2).Select(parts => (IUiComponent)new Li(
                new Span(new ValueText(parts[1])).Class("works-report__title"), new Span(new Text(OutcomeKey(parts[0]))).Class("works-report__reason"))).ToArray();
            var content = new List<IUiComponent> { new P(new ValueText(summary)) };
            if (stayed.Length > 0) content.Add(new Ul(stayed));
            return new Div([.. content]).Class("works-report").Attribute("role", "status");
        }

        private string OutcomeKey(string key) => texts.Has("works.outcome." + key) ? "works.outcome." + key : texts.Has("acquisition.outcome." + key) ? "acquisition.outcome." + key : "works.outcome.other";

        // Metadata search and manual creation open from a button, so the list stays the page.
        private IUiComponent AddDialog(string? csrf)
        {
            var mediaTypes = Enum.GetValues<MediaType>().Select(type => new SelectOption(((byte)type).ToString(CultureInfo.InvariantCulture), texts["media.type." + type]));
            SelectOption[] subtypes = [new SelectOption("", texts["media.subtype.none"]), .. Enum.GetValues<MediaSubtype>()
                .Select(subtype => new SelectOption(((byte)subtype).ToString(CultureInfo.InvariantCulture), texts["media.subtype." + subtype]))];

            // Each type's provider creates Works with their identity and units.
            var content = new List<IUiComponent>
            {
                new Form<SearchMetadataDataV1>(form =>
                {
                    form.Select(item => item.MediaTypeId, "media.type", mediaTypes);
                    form.Select(item => item.MediaSubtypeId, "media.subtype", subtypes);
                    form.Text(item => item.Query, "media.title");
                    form.Number(item => item.ReleaseYear, "media.year");
                    form.Submit("admin.works.searchMetadata");
                }, Address("Search"), csrf, _search, _search is null ? null : FormError)
            };

            if (_results is { } results && _search is { } searched)
            {
                if (results.Count == 0) content.Add(new P(new Text("admin.works.noMetadata")).Attribute("role", "status"));
                else content.Add(new Table<MetadataSearchResultV1>(results)
                    .Text(item => item.Title, "media.title")
                    .Resource(item => item.Namespace, "admin.works.source", source => "admin.works.source." + source)
                    .Text(item => item.OriginalTitle, "admin.works.originalTitle")
                    .Text(item => item.Author, "media.author")
                    .Text(item => item.ReleaseYear, "media.year")
                    .Content("common.actions", item => item.WorkId is { } workId
                        ? new A(new Text("common.open")).Attribute("href", "/Admin/Works/" + workId.ToString(CultureInfo.InvariantCulture))
                        : new Form<ImportMetadataWorkDataV1>(form =>
                        {
                            form.Hidden(data => data.MediaTypeId);
                            form.Hidden(data => data.MediaSubtypeId);
                            form.Hidden(data => data.Namespace);
                            form.Hidden(data => data.ExternalId);
                            form.Submit("admin.works.import");
                        }, Address("Import"), csrf, new ImportMetadataWorkDataV1(searched.MediaTypeId, item.ExternalId, searched.MediaSubtypeId, item.Namespace))));
            }

            content.Add(new Form<CreateWorkDataV1>(form =>
            {
                form.Select(item => item.MediaTypeId, "media.type", mediaTypes);
                form.Select(item => item.MediaSubtypeId, "media.subtype", subtypes);
                form.Text(item => item.CanonicalTitle, "media.title");
                form.Number(item => item.ReleaseYear, "media.year");
                form.Submit("media.create");
            }, Address(), csrf, _input, _search is null && !_bulkFailed ? FormError : null));

            var open = _search is not null || _input is not null && FormError is not null;
            return new Modal(assets, "works-add", "works.add", [new Div([.. content]).Class("works-add-form")], open);
        }
    }
}
