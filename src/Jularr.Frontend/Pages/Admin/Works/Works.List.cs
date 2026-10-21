using System.Globalization;
using Jularr.Backend.Data.Admin.Media.V1;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Frontend.Framework;

namespace Jularr.Frontend.Pages.Admin.Works
{
    public sealed partial class WorksModel
    {
        // The columns after the fixed ones, in their first order; what a person hides or moves stays in their browser.
        private static readonly (string Key, WorkSortType? Sort, bool Hidden)[] Columns =
        [
            ("type", WorkSortType.Type, false), ("year", WorkSortType.Year, false), ("monitored", null, false), ("status", null, false), ("profile", null, false),
            ("library", null, false), ("files", WorkSortType.Files, true), ("size", WorkSortType.Size, false), ("added", WorkSortType.Added, true)
        ];

        private bool Filtered => TypeFilter is not null || MonitoredFilter is not null || StatusFilter is not null || LibraryFilter is not null || ProfileFilter is not null || !string.IsNullOrEmpty(QueryFilter);

        // The address of the list with its filters and order, or of a handler that returns to it; paging and sorting change one part of it.
        private string Address(string? handler = null, int? pageNumber = null, WorkSortType? sort = null, bool? descending = null, bool clearFilters = false)
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
                Add("status", StatusFilter is { } status ? ((byte)status).ToString(CultureInfo.InvariantCulture) : null);
                Add("library", LibraryFilter?.ToString(CultureInfo.InvariantCulture));
                Add("profile", ProfileFilter?.ToString(CultureInfo.InvariantCulture));
                Add("q", QueryFilter);
            }

            var order = sort ?? Sort;
            Add("sort", order == WorkSortType.Title ? null : ((byte)order).ToString(CultureInfo.InvariantCulture));
            Add("desc", (descending ?? Descending) ? "true" : null);
            if (pageNumber is > 1) Add("pageNumber", pageNumber.Value.ToString(CultureInfo.InvariantCulture));
            Add("handler", handler);
            return "/Admin/Works" + (values.Count == 0 ? "" : "?" + string.Join('&', values));
        }

        private IUiComponent Icon(string name, int size = 20) => new Framework.Icon(name, assets, size, "2.4");

        private static HtmlElement Choice(string value, IUiComponent label, bool selected)
        {
            var option = new Option(label).Attribute("value", value);
            return selected ? option.Attribute("selected", "selected") : option;
        }

        // One filter: the first choice lets everything through and names the filter.
        private IUiComponent FilterSelect(string name, string labelKey, string? current, IEnumerable<(string Value, IUiComponent Label)> choices) => new Select([Choice("", new Text(labelKey), current is null),
            .. choices.Select(choice => Choice(choice.Value, choice.Label, choice.Value == current))]).Attribute("name", name).TextAttribute("aria-label", labelKey).Class("works-select" + (current is null ? "" : " works-select--set"));

        // The filter bar: native selects that apply at once, and without script by their button. It is one row that slides sideways on a narrow screen.
        private IUiComponent Filters()
        {
            var inv = CultureInfo.InvariantCulture;
            var items = new List<IUiComponent>
            {
                FilterSelect("type", "works.filter.type", TypeFilter is { } type ? ((byte)type).ToString(inv) : null,
                    Enum.GetValues<MediaType>().Select(value => (((byte)value).ToString(inv), (IUiComponent)new Text("media.type." + value)))),
                FilterSelect("monitored", "works.filter.monitored", MonitoredFilter is { } monitored ? (monitored ? "true" : "false") : null,
                    [("true", new Text("works.filter.monitored.yes")), ("false", new Text("works.filter.monitored.no"))]),
                FilterSelect("status", "works.filter.status", StatusFilter is { } status ? ((byte)status).ToString(inv) : null,
                    Enum.GetValues<WorkListStatus>().Select(value => (((byte)value).ToString(inv), (IUiComponent)new Text("works.status." + value)))),
                FilterSelect("library", "works.filter.library", LibraryFilter?.ToString(inv), _libraries.Select(library => (library.Id.ToString(inv), (IUiComponent)new ValueText(library.Name))))
            };

            if (CanAcquire && _profiles.Count > 0)
            {
                items.Add(FilterSelect("profile", "works.filter.profile", ProfileFilter?.ToString(inv), _profiles.Select(profile => (profile.Id.ToString(inv), (IUiComponent)new ValueText(profile.Name)))));
            }

            // Order and the header search's words stay with the filters.
            if (Sort != WorkSortType.Title) items.Add(new Input().Attribute("type", "hidden").Attribute("name", "sort").Attribute("value", ((byte)Sort).ToString(inv)));
            if (Descending) items.Add(new Input().Attribute("type", "hidden").Attribute("name", "desc").Attribute("value", "true"));
            if (!string.IsNullOrEmpty(QueryFilter))
            {
                items.Add(new Input().Attribute("type", "hidden").Attribute("name", "q").Attribute("value", QueryFilter));
                items.Add(new Tag(Icon("search", 16), new ValueText(QueryFilter)).Class("tag works-query"));
            }

            items.Add(new Button(Icon("filter"), new Span(new Text("works.filter.apply")).Class("works-filters__apply")).Submit().Class("button works-apply").TextAttribute("aria-label", "works.filter.apply"));
            if (Filtered) items.Add(new A(Icon("close", 16), new Span(new Text("works.filter.clear"))).Class("button works-clear").Attribute("href", Address(clearFilters: true)));

            return new Div(new FormElement([.. items]).Class("works-filters").Attribute("method", "get").Attribute("action", "/Admin/Works").Attribute("data-works-filters", ""),
                CanManageMedia ? new Button(new Text("works.add")).Primary().Class("button button--primary works-add").Attribute("data-dialog-open", "works-add") : new Span()).Class("works-toolbar");
        }

        // The mass editor: what the chosen titles are set to, and what is done to them. It shows once a title is chosen.
        private IUiComponent Editor(string? csrf)
        {
            var inv = CultureInfo.InvariantCulture;
            var items = new List<IUiComponent> { new Span(new Text("works.bar.selected")).Class("works-bar__count").Attribute("data-works-count", "").Attribute("data-format", texts["works.bar.selected"]) };
            var hasSettings = false;
            if (CanManageWanted)
            {
                hasSettings = true;
                items.Add(new Select(Choice("", new Text("works.bar.monitoring"), true), Choice("on", new Text("works.filter.monitored.yes"), false), Choice("off", new Text("works.filter.monitored.no"), false))
                    .Attribute("name", "monitoring").TextAttribute("aria-label", "works.bar.monitoring").Class("works-select"));
                if (CanAcquire && _profiles.Count > 0)
                {
                    items.Add(new Select([Choice("", new Text("works.bar.profile"), true), Choice("0", new Text("works.bar.default"), false),
                        .. _profiles.Select(profile => Choice(profile.Id.ToString(inv), new ValueText(profile.Name), false))]).Attribute("name", "profileId").TextAttribute("aria-label", "works.bar.profile").Class("works-select"));
                }

                // New files only: the files that are filed stay where they are.
                items.Add(new Select([Choice("", new Text("works.bar.library"), true), Choice("0", new Text("works.bar.default"), false),
                    .. _libraries.Select(library => Choice(library.Id.ToString(inv), new ValueText(library.Name), false))]).Attribute("name", "libraryId").TextAttribute("aria-label", "works.bar.library").Class("works-select"));
            }

            if (hasSettings) items.Add(new Button(new Text("common.save")).Submit().Primary().Attribute("name", "action").Attribute("value", "apply"));
            if (CanAcquire) items.Add(new Button(Icon("search", 18), new Text("works.bar.search")).Submit().Attribute("name", "action").Attribute("value", "search"));
            if (CanManageMedia)
            {
                items.Add(new Button(Icon("sync", 18), new Text("works.bar.refresh")).Submit().Attribute("name", "action").Attribute("value", "refresh"));
                items.Add(new Button(Icon("trash", 18), new Text("work.delete")).Class("button works-delete").Attribute("data-works-delete", ""));
            }

            return new FormElement(new Input().Attribute("type", "hidden").Attribute("name", "__RequestVerificationToken").Attribute("value", csrf ?? ""), new Div([.. items]).Class("works-bar"))
                .Class("works-editor").Attribute("id", "works-bulk").Attribute("method", "post").Attribute("action", Address("Bulk")).Attribute("data-works-bar", "");
        }

        private IUiComponent ListTable(IReadOnlyList<WorkListItemV1> items, string? csrf)
        {
            var selectable = CanManageWanted || CanManageMedia;
            var heads = new List<IUiComponent>
            {
                Head("select", null, null, fix: true, content: selectable ? new Input().Attribute("type", "checkbox").Attribute("data-works-select-all", "").TextAttribute("aria-label", "works.selectAll") : new Span()),
                Head("cover", null, null, fix: true, content: new Span(new Text("works.col.cover")).Class("visually-hidden")),
                Head("title", "media.title", WorkSortType.Title, fix: true)
            };
            foreach (var (key, sort, hidden) in Columns) heads.Add(Head(key, "works.col." + key, sort, hidden: hidden));
            heads.Add(new Th(new Button(Icon("settings"), new Span(new Text("work.columns")).Class("visually-hidden")).Attribute("data-columns-open", "").Class("work-gear")).Attribute("data-col", "actions").Attribute("scope", "col"));

            var rows = items.Select(item => (IUiComponent)new Tr([.. RowCells(item, csrf, selectable), .. Columns.Select(column => ColumnCell(column.Key, item, csrf)), Actions(item, csrf)]).Class("works-row"));
            return new Div(new Div(new TableElement(new Thead(new Tr([.. heads])), new Tbody([.. rows])).Class("page-table works-table").Attribute("data-work-table", "works")).Class("page-table-wrap works-table-wrap"),
                new ColumnPicker()).Class("works-list");
        }

        // A header: a sortable one is a link that sets the order, and the current order is marked.
        private IUiComponent Head(string key, string? labelKey, WorkSortType? sort, bool fix = false, bool hidden = false, IUiComponent? content = null)
        {
            IUiComponent label = content ?? (labelKey is null ? new Span() : new Text(labelKey));
            if (sort is { } order && labelKey is not null)
            {
                label = new A(new Text(labelKey)).Class("work-sort").Attribute("href", Address(sort: order, descending: Sort == order && !Descending));
            }

            var head = new Th(label).Attribute("data-col", key).Attribute("scope", "col").Attribute("data-hidden", hidden ? "true" : "false");
            if (labelKey is not null) head.Attribute("data-label", texts[labelKey]);
            if (fix) head.Attribute("data-fixed", "true");
            if (sort is { } current && Sort == current) head.Attribute("aria-sort", Descending ? "descending" : "ascending");
            return head;
        }

        // The cells that stay in front: the box, the cover and the title that opens the title's page.
        private IEnumerable<IUiComponent> RowCells(WorkListItemV1 item, string? csrf, bool selectable)
        {
            var id = item.Id.ToString(CultureInfo.InvariantCulture);
            var home = "/Admin/Works/" + id;
            yield return Cell("select", selectable
                ? new Input().Attribute("type", "checkbox").Attribute("name", "workIds").Attribute("value", id).Attribute("form", "works-bulk").TextAttribute("aria-label", "works.select")
                : new Span());
            yield return Cell("cover", new A(item.PosterUrl is { } poster ? new MediaImage(poster, "", 40, 60) : new Span().Class("works-cover__placeholder")).Class("works-cover")
                .Attribute("href", home).Attribute("tabindex", "-1").Attribute("aria-hidden", "true"));
            yield return Cell("title", new A(new ValueText(item.CanonicalTitle)).Class("work-title-text works-title").Attribute("href", home).Attribute("title", item.CanonicalTitle));
        }

        private IUiComponent ColumnCell(string key, WorkListItemV1 item, string? csrf) => key switch
        {
            "type" => Cell(key, new Text(MediaTypeLabel.Key(item.MediaTypeId, item.MediaSubtypeId))),
            "year" => Cell(key, new ValueText(item.ReleaseYear?.ToString(CultureInfo.InvariantCulture) ?? "-")),
            "monitored" => Cell(key, MonitorSwitch(item, csrf)),
            "status" => Cell(key, new Span(new Text("works.status." + item.WorkListStatusId)).Class("state-chip" + item.WorkListStatusId switch
            {
                WorkListStatus.Available => " state-chip--available",
                WorkListStatus.Downloading => " state-chip--inprogress",
                _ => ""
            })),
            "profile" => Cell(key, new ValueText(item.ProfileName ?? "-")),
            "library" => Cell(key, new ValueText(item.LibraryName ?? "-")),
            "files" => Cell(key, new ValueText(item.FileCount.ToString("N0", CultureInfo.CurrentCulture))),
            "size" => Cell(key, new ValueText(item.FileCount > 0 ? ByteSize.Format(item.SizeBytes) : "-")),
            _ => Cell(key, new ValueText(item.CreatedAt.ToLocalTime().ToString("d", CultureInfo.CurrentCulture)))
        };

        private static IUiComponent Cell(string key, IUiComponent content) => new Td(content).Attribute("data-col", key);

        // The title's own rule: monitored while its target is not paused. Titles that are not wanted, and games that are monitored per platform, only show it.
        private IUiComponent MonitorSwitch(WorkListItemV1 item, string? csrf)
        {
            if (item.WantedItemId is null) return new ValueText("-");
            if (item.MediaTypeId == MediaType.Game || !CanManageWanted)
            {
                return new Span(new Text(item.IsMonitored ? "work.monitor.on" : "work.monitor.off")).Class("work-switch work-switch--static" + (item.IsMonitored ? " work-switch--on" : ""));
            }

            return new PostForm(Address("Monitor"), csrf, [("workId", item.Id.ToString(CultureInfo.InvariantCulture)), ("isMonitored", item.IsMonitored ? "false" : "true")],
                new SwitchButton(item.IsMonitored, item.IsMonitored ? "work.monitor.stop" : "work.monitor.start"));
        }

        // Search now, as the title's page does; edit is that page.
        private IUiComponent Actions(WorkListItemV1 item, string? csrf)
        {
            var id = item.Id.ToString(CultureInfo.InvariantCulture);
            var icons = new List<IUiComponent>();
            if (CanAcquire && item.WantedItemId is not null && item.MediaTypeId != MediaType.Game)
            {
                icons.Add(new PostForm(Address("SearchWork"), csrf, [("workId", id)],
                    new Button(Icon("search", 20)).Submit().Class("work-icon").TextAttribute("aria-label", "work.search").TextAttribute("title", "work.search")));
            }

            icons.Add(new A(Icon("pencil", 20)).Class("work-icon").Attribute("href", "/Admin/Works/" + id).TextAttribute("aria-label", "work.edit").TextAttribute("title", "work.edit"));
            return new Td(new Div([.. icons]).Class("work-icons")).Attribute("data-col", "actions");
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
