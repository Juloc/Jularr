using System.Globalization;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Activity.V1;
using Jularr.Backend.Data.Enums.Library;
using Jularr.Backend.Service.Admin.Activity.V1;
using Jularr.Frontend.Framework;
using Microsoft.AspNetCore.Mvc;

namespace Jularr.Frontend.Pages.Admin.History
{
    // What happened to the media, newest first, on the list kit: figures, filter chips with their counts, one row per event and a drawer of the event's details; a Work's page opens its own history.
    public sealed class HistoryModel(ReadActivityEventsV1 read, ReadActivityEventsSummaryV1 readSummary, FrontendAssets assets, UiText texts) : JularrPage
    {
        private PageResult<ActivityEventV1>? _events;
        private ActivityEventsSummaryV1? _summary;

        [BindProperty(SupportsGet = true)]
        public long? WorkId { get; set; }

        [BindProperty(SupportsGet = true)]
        public int PageNumber { get; set; } = 1;

        // The chip: what was fetched, what arrived, what failed, what happened to files.
        [BindProperty(SupportsGet = true, Name = "group")]
        public ActivityEventGroupType Group { get; set; }

        protected override void Define(Page page)
        {
            var work = WorkId is not null ? _events?.Items.FirstOrDefault()?.WorkTitle : null;
            page.Title("admin.history.title", work);
            if (work is not null) page.Parent("admin.history.title", "/Admin/History");
            page.Navigation("nav.admin");
            if (_events is not { } events)
            {
                return;
            }

            page.Asset("Pages/Admin/History/History.entry.scss");
            page.Asset("Framework/Components/Truncated.entry.ts");
            page.Asset("Framework/Components/Drawer.entry.ts");
            page.Asset("Framework/Components/ActionDock.entry.ts");

            page.Content.Add(new ListToolbar(Chips()));
            var columns = new List<ListColumn> { new("table.work", ListRole.Main), new("history.event", ListRole.Status), new("history.release", ListRole.Data, 1), new("history.source", ListRole.Data, 2), new("history.when"), new("common.actions", ListRole.Actions) };
            var empty = new Div(new IconTile(Glyph("search", 22), UiTone.Neutral), new P(new Text("history.empty")));
            page.Content.Add(new ListPanel("history.list", columns, [.. events.Items.Select(Row)], empty).Modifier("history-list"));
            page.Content.AddRange(events.Items.Select(DrawerOf));

            var total = CountOf(Group);
            var pages = total is { } count ? Math.Max(1, (count + events.PageSize - 1) / events.PageSize) : events.Page + (events.Items.Count == events.PageSize ? 1 : 0);
            if (pages > 1 || events.Page > 1)
            {
                page.Content.Add(new ActionDock(assets, "history.actions", paging: new DockPaging(events.Page > 1 ? Address(Group, events.Page - 1) : null, events.Page < pages ? Address(Group, events.Page + 1) : null,
                    new ValueText(string.Format(CultureInfo.CurrentCulture, texts["dock.page"], events.Page, pages)))));
            }
        }

        private IUiComponent Glyph(string name, int size = 20) => new Icon(name, assets, size);

        private static string GroupIcon(ActivityEventGroupType group) => group switch
        {
            ActivityEventGroupType.Grabbed => "download",
            ActivityEventGroupType.Arrived => "check",
            ActivityEventGroupType.Failed => "alert",
            ActivityEventGroupType.Files => "folder",
            _ => "layers"
        };

        private int? CountOf(ActivityEventGroupType group) => group switch
        {
            ActivityEventGroupType.Grabbed => _summary?.GrabbedCount,
            ActivityEventGroupType.Arrived => _summary?.ArrivedCount,
            ActivityEventGroupType.Failed => _summary?.FailedCount,
            ActivityEventGroupType.Files => _summary?.FilesCount,
            _ => _summary?.TotalCount
        };

        private NavigationTabs Chips() => new([.. Enum.GetValues<ActivityEventGroupType>().Select(group => new NavigationTab(group == ActivityEventGroupType.All ? "common.all" : "history.group." + group, Address(group), group == Group, Count: CountOf(group), Icon: Glyph(GroupIcon(group), 14)))],
            "history.filters", "filters");

        // The icon and tone of what happened.
        private static (string Icon, UiTone Tone) Kind(ActivityEventType type) => type switch
        {
            ActivityEventType.Grabbed => ("download", UiTone.Info),
            ActivityEventType.DownloadFailed => ("alert", UiTone.Danger),
            ActivityEventType.Imported => ("check", UiTone.Success),
            ActivityEventType.Upgraded => ("swap", UiTone.Success),
            ActivityEventType.FileRenamed => ("pencil", UiTone.Accent),
            ActivityEventType.FileDeleted => ("trash", UiTone.Warning),
            ActivityEventType.ManualImport => ("folder", UiTone.Success),
            ActivityEventType.Blocked => ("shield", UiTone.Danger),
            ActivityEventType.SubtitleDownloaded => ("subtitles", UiTone.Accent),
            _ => ("trash", UiTone.Danger)
        };

        private IUiComponent EventChip(ActivityEventV1 item)
        {
            var (icon, tone) = Kind(item.ActivityEventTypeId);
            return new StateChip(new Text("history.event." + item.ActivityEventTypeId), tone, Glyph(icon, 14));
        }

        private string? Unit(ActivityEventV1 item) => MediaUnitLabel.Format(texts, item.WorkUnitTypeId, item.UnitNumber, item.ParentUnitNumber);

        private ListRow Row(ActivityEventV1 item)
        {
            var (icon, tone) = Kind(item.ActivityEventTypeId);
            IUiComponent title = item.WorkId is { } workId
                ? new A(new Truncated(item.WorkTitle)).Class("list-identity__title").Attribute("href", "/Admin/Works/" + workId.ToString(CultureInfo.InvariantCulture))
                : new Truncated(item.WorkTitle);
            // Under the title: the episode or volume, else the quality or the release.
            var line = Unit(item) is { Length: > 0 } unit ? unit : item.QualityKey is { Length: > 0 } quality ? quality : item.ReleaseTitle;
            var identity = new ListIdentity(new IconTile(Glyph(icon), tone), title, line is { Length: > 0 } ? new Truncated(line) : new Span());
            var release = item.ReleaseTitle is { Length: > 0 } name ? new Span(new Truncated(name)).Class("list-value list-value--fill") : Dim();
            var source = string.Join(" · ", new[] { item.IndexerName, item.ClientName }.OfType<string>().Distinct());
            var when = new Span(new ValueText(item.OccurredAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture))).Class("list-value");
            var actions = new Div(Drawer.Opener(new Button(Glyph("dots", 18)).IconOnly(texts["history.details"]), DrawerId(item))).Class("list-actions");
            var row = new ListRow([identity, EventChip(item), release, source.Length > 0 ? new Span(new Truncated(source)).Class("list-value list-value--fill") : Dim(), when, actions],
                [ListPanel.Fact("history.when", when, Glyph("clock", 14))]);
            if (item.ActivityEventTypeId is ActivityEventType.DownloadFailed or ActivityEventType.Blocked) row.Tinted(UiTone.Danger);
            return row;
        }

        private static IUiComponent Dim() => new Span(new ValueText("–")).Class("list-value list-value--dim");

        private static string DrawerId(ActivityEventV1 item) => "event-" + item.Id.ToString(CultureInfo.InvariantCulture);

        // One event's details: what it was, the release and where it came from, the file and who did it.
        private IUiComponent DrawerOf(ActivityEventV1 item)
        {
            var facts = new List<(string, IUiComponent)> { ("history.when", new ValueText(item.OccurredAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture))), ("history.by", new ValueText(item.AccountName ?? texts["history.byJularr"])) };
            if (Unit(item) is { Length: > 0 } unit) facts.Insert(0, ("history.unit", new ValueText(unit)));
            if (item.ReleaseTitle is { Length: > 0 } release) facts.Add(("history.release", new ValueText(release)));
            if (item.QualityKey is { Length: > 0 } quality) facts.Add(("history.quality", new ValueText(quality)));
            var source = string.Join(" · ", new[] { item.IndexerName, item.ClientName }.OfType<string>().Distinct());
            if (source.Length > 0) facts.Add(("history.source", new ValueText(source)));

            var body = new List<IUiComponent> { Drawer.State("history.event", EventChip(item)), Drawer.Facts([.. facts]) };
            if (item.Path is { Length: > 0 } path)
            {
                var lines = new List<IUiComponent>();
                if (item.PreviousPath is { } before) lines.Add(new P(new ValueText(before + " → ")));
                lines.Add(new P(new ValueText(path)).Class("history-path"));
                body.Add(Drawer.Block("history.file", [.. lines]));
            }

            if (item.ReasonKey is { } reason) body.Add(Drawer.Block("history.reason", new P(new Text("acquisition.outcome." + reason))));
            var footer = item.WorkId is { } id
                ? new List<IUiComponent> { new A(new Text("history.openWork")).Class("button").Attribute("href", "/Admin/Works/" + id.ToString(CultureInfo.InvariantCulture)) }
                : [];
            return new Drawer(assets, DrawerId(item), [new DrawerPane("details", "history.details", "history.details", "info", body, footer, null, item.WorkTitle)], sheet: true);
        }

        public async Task<IActionResult> OnGetAsync()
        {
            await LoadDataAsync();
            PreparePage();
            return Page();
        }

        protected override async Task LoadDataAsync()
        {
            var ct = HttpContext.RequestAborted;
            _events = await read.ExecuteAsync<PageResult<ActivityEventV1>>(new ReadActivityEventsParametersV1(WorkId, Math.Max(1, PageNumber), 25, Group), ct);
            _summary = await readSummary.ExecuteAsync<ActivityEventsSummaryV1>(new ReadActivityEventsParametersV1(WorkId), ct);
        }

        private string Address(ActivityEventGroupType group, int page = 1)
        {
            var values = new List<string>();
            if (group != ActivityEventGroupType.All) values.Add("group=" + group);
            if (page > 1) values.Add("PageNumber=" + page.ToString(CultureInfo.InvariantCulture));
            if (WorkId is { } work) values.Add("WorkId=" + work.ToString(CultureInfo.InvariantCulture));
            return "/Admin/History" + (values.Count == 0 ? "" : "?" + string.Join('&', values));
        }
    }
}
