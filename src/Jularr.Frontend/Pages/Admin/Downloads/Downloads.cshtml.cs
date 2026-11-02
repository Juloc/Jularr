using System.Globalization;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Acquisition.V1;
using Jularr.Backend.Data.Admin.Activity.V1;
using Jularr.Backend.Data.Admin.Network.V1;
using Jularr.Backend.Data.Admin.Storage.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Enums.Operation;
using Jularr.Backend.Data.Enums.Storage;
using Jularr.Backend.Service.Admin.Acquisition.V1;
using Jularr.Backend.Service.Admin.Activity.V1;
using Jularr.Backend.Service.Admin.Network.V1;
using Jularr.Backend.Service.Admin.Storage.V1;
using Jularr.Backend.Service.Core;
using Jularr.Frontend.Framework;
using Jularr.Frontend.Modules.Live;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Jularr.Frontend.Pages.Admin.Downloads
{
    // Every download in one place, SABnzbd style: a pill tab per client (All first) with the client's icon buttons, its figures with the speed line and limit, its queue and history, and below them what belongs to that client only.
    public sealed class DownloadsModel(ReadDownloadClientsV1 readClients, ReadActivityV1 read, ApplyActivityDownloadsV1 apply, PauseActivityQueueV1 pauseQueue, PauseActivityDownloadV1 pause,
        RemoveActivityDownloadV1 remove, RetryActivityV1 retry, RetryActivityImportV1 retryImport, SaveActivitySourceLinkV1 sourceLink, PrioritizeActivityDownloadsV1 prioritize,
        MoveActivityDownloadV1 move, DeleteActivityHistoryV1 deleteHistory, ClearActivityHistoryV1 clearHistory, ReadDownloadSpeedLimitV1 readSpeedLimit, SetDownloadSpeedLimitV1 setSpeedLimit,
        AddActivityDownloadV1 add, FindManualImportWorksV1 findTitles, ReadNewsServersV1 newsServers, ReadAcquisitionConnectionsV1 connections, TestAcquisitionConnectionV1 test, ReadTorrentEngineV1 torrentEngine,
        ReadVpnProfilesV1 vpnProfiles, ReadDownloadStorageSettingsV1 storageSettings, ReadStorageMountV1 storageMount, InspectDownloadClientV1 inspect, IAntiforgery antiforgery, UiText texts,
        FrontendAssets assets) : JularrPage
    {
        private const string BulkForm = "downloads-bulk";
        private const string MoveForm = "downloads-move";
        private const string HistoryKey = "history";
        private const string BulkRemoveDialog = "downloads-remove-selected";
        private const string PriorityDialog = "downloads-priority";
        private const string ClearDialog = "downloads-clear";

        // The limits offered beside the speed, in megabytes per second; a limit set elsewhere joins them.
        private static readonly long[] Limits = [1, 2, 5, 10, 20, 50, 100, 250];

        private IReadOnlyList<DownloadClientV1> _clients = [];
        private DownloadClientV1? _client;
        private PageResult<ActivityItemV1>? _queue;
        private PageResult<ActivityItemV1>? _history;
        private IReadOnlyList<AcquisitionConnectionDetailsV1> _connections = [];
        private IReadOnlyList<NewsServerV1> _newsServers = [];
        private IReadOnlyList<ManualImportWorkV1>? _titles;
        private DownloadSpeedLimitV1? _limit;
        private TorrentEngineV1? _engine;
        private VpnProfileV1? _vpn;
        private DownloadStorageSettingsV1? _storage;
        private DownloadClientDashboardV1? _dashboard;

        // The free space where the shown downloads land, with the reserve kept there: a native workspace's last measure, SABnzbd's own report; on All the fullest workspace.
        private (long Free, long? Reserve)? _space;

        [BindProperty(SupportsGet = true)]
        public DownloadClientKind? Client { get; set; }

        [BindProperty(SupportsGet = true)]
        public long? ConnectionId { get; set; }

        [BindProperty(SupportsGet = true)]
        public MediaType? Category { get; set; }

        [BindProperty(SupportsGet = true)]
        public DownloadPriorityType? Priority { get; set; }

        [BindProperty(SupportsGet = true)]
        public bool FailedOnly { get; set; }

        [BindProperty(SupportsGet = true)]
        public int PageNumber { get; set; } = 1;

        [BindProperty(SupportsGet = true, Name = HistoryKey)]
        public int HistoryPage { get; set; } = 1;

        // The download whose removal the dialog asks about.
        [BindProperty(SupportsGet = true, Name = "remove")]
        public long? RemoveId { get; set; }

        // The add dialog is open, with what the person searched for its title.
        [BindProperty(SupportsGet = true, Name = "add")]
        public bool Adding { get; set; }

        [BindProperty(SupportsGet = true, Name = "addq")]
        public string? AddQuery { get; set; }

        protected override void Define(Page page)
        {
            page.Title("admin.downloads.title");
            page.Navigation("nav.admin");
            page.NoPageRow();
            page.Asset("Pages/Admin/Downloads/Downloads.entry.scss");
            page.Asset("Pages/Admin/Downloads/Downloads.entry.ts");
            page.Asset("Framework/Components/LiveProgress.entry.ts");
            page.Asset("Framework/Components/Truncated.entry.ts");
            page.Asset("Framework/Components/ActionDock.entry.ts");
            page.Asset("Framework/Components/ListFilter.entry.ts");
            page.Asset("Framework/Components/ChoiceSelect.entry.ts");
            page.Asset("Framework/Components/Modal.entry.ts");
            var csrf = antiforgery.GetAndStoreTokens(HttpContext).RequestToken;

            // The heading with the client's icon buttons, the clients as pill tabs under it; then the figures, the queue, the history and the client's own panels.
            page.Content.Add(new Div([new H1(new Text("admin.downloads.title")), new Span().Class("downloads-panel__spacer"), .. HeadButtons(csrf)]).Class("downloads-head"));
            page.Content.Add(new Div(Tabs()).Class("downloads-tabs"));
            page.Content.Add(Figures(csrf));
            if (_queue is { } queue) page.Content.Add(QueuePanel(queue, csrf));
            if (_history is { } history) page.Content.Add(HistoryPanel(history, csrf));
            page.Content.AddRange(ClientPanels(csrf));

            if (FormError is not null) page.Content.Add(new P(new Text(FormError.ResourceKey)).Attribute("role", "alert"));
            if (RemoveId is { } removing && _queue?.Items.FirstOrDefault(item => item.Id == removing) is { } chosen) page.Content.Add(RemoveDialog(chosen, csrf));
            if (Adding && AddLabel() is { } addLabel) page.Content.Add(AddDialog(addLabel, csrf));
            page.Content.Add(BulkRemove());
            if (Shown().Any(client => DownloadClientKinds.HasPriorities(client.DownloadClientKindId))) page.Content.Add(PriorityChoice());
            if (_history is { Items.Count: > 0 }) page.Content.Add(ClearHistory(csrf));
            page.Content.Add(new PostForm(Address(handler: "Bulk"), csrf, []).Attribute("id", BulkForm));
            page.Content.Add(new PostForm(Address(handler: "Move"), csrf, [("operationId", ""), ("beforeOperationId", "")]).Attribute("id", MoveForm));
            page.Content.Add(Dock());
        }

        // All first, then each client with its open downloads; SABnzbd and qBittorrent go by their own names.
        private NavigationTabs Tabs()
        {
            var tabs = new List<NavigationTab> { new("downloads.all", ClientAddress(null), _client is null, Count: _clients.Sum(client => client.QueueCount)) };
            tabs.AddRange(_clients.Select(client => new NavigationTab(client.AcquisitionConnectionId is null ? "downloads.client." + client.DownloadClientKindId : client.Name, ClientAddress(client),
                client == _client, IsValue: client.AcquisitionConnectionId is not null, Count: client.QueueCount)));
            return new NavigationTabs(tabs, "downloads.clients");
        }

        // Warnings, pause or resume this client (on All: everything), its settings and adding a download; a client that takes no download by hand has no add button.
        private IEnumerable<IUiComponent> HeadButtons(string? csrf)
        {
            var shown = Shown();
            yield return IconLink("bell", string.Format(CultureInfo.CurrentCulture, texts["downloads.warnings"], shown.Sum(client => client.WarningCount)), "/Admin/Activity?Filter=Errors");
            var paused = shown.Sum(client => client.PausedCount);
            var open = shown.Sum(client => client.QueueCount);
            yield return paused > 0 && paused == open
                ? IconPost("ResumeClient", "play", _client is null ? "downloads.resumeAll" : "downloads.resumeClient", csrf)
                : IconPost("PauseClient", "pause", _client is null ? "downloads.pauseAll" : "downloads.pauseClient", csrf);
            yield return IconLink("settings", texts["downloads.settings"], _client is { } client ? SettingsAddress(client) : "/Admin/Downloads/Clients");
            if (AddLabel() is { } label)
            {
                yield return new A(Glyph("plus", 16), new Text(label)).Class("button button--primary downloads-add-button").Attribute("href", Address(add: true)).Attribute("title", texts[label]);
            }
        }

        // What the add button says: an NZB for a Usenet client, a torrent or magnet for a torrent client, plainly Add on All; a client that cannot take one has none.
        private string? AddLabel() => _client?.DownloadClientKindId is { } kind
            ? DownloadClientKinds.AddsNzb(kind) ? "downloads.addNzb" : DownloadClientKinds.AddsTorrent(kind) ? "downloads.addTorrent" : null
            : _clients.Any(client => DownloadClientKinds.AddsNzb(client.DownloadClientKindId) || DownloadClientKinds.AddsTorrent(client.DownloadClientKindId)) ? "downloads.add" : null;

        // Speed with its short line and the client's limit, what is left and how long, what runs and what waits, and the free space where the downloads land; the stream keeps them current.
        private IUiComponent Figures(string? csrf)
        {
            var shown = Shown();
            var figures = LiveEntities.Downloads(texts, shown).Text;
            var key = LiveKeys.Downloads(_client is { } client ? ReadDownloadClientsV1.Key(client.DownloadClientKindId, client.AcquisitionConnectionId) : LiveKeys.AllDownloads);
            var speed = new Div(new Span(new Text("downloads.speed")).Class("downloads-stat__label"),
                LiveFigure(key, "speed", figures, "downloads-stat__value downloads-stat__value--large"),
                new Div(new Sparkline([.. LiveEntities.SpeedLine(shown).Select(value => (double)value)], "downloads.speedLine", height: LiveEntities.SpeedLineHeight, livePoints: "line"))
                    .Class("downloads-stat__line").Attribute("data-live", key),
                SpeedLimit(csrf)).Class("downloads-stat downloads-stat--speed");
            var cards = new List<IUiComponent>
            {
                speed,
                Stat("downloads.remaining", LiveFigure(key, "remaining", figures, "downloads-stat__value"), LiveFigure(key, "eta", figures, "downloads-stat__foot")),
                Stat("downloads.active", LiveFigure(key, "active", figures, "downloads-stat__value"), LiveFigure(key, "waiting", figures, "downloads-stat__foot"))
            };
            if (_space is { } space)
            {
                cards.Add(Stat("downloads.free", new Strong(new ValueText(ByteSize.Format(space.Free))).Class("downloads-stat__value"), space.Reserve is { } reserve
                    ? new Small(new ValueText(string.Format(CultureInfo.CurrentCulture, texts["downloads.reserve"], ByteSize.Format(reserve)))).Class("downloads-stat__foot") : new Span()));
            }

            return new Div([.. cards]).Class(cards.Count == 4 ? "downloads-figures" : "downloads-figures downloads-figures--three");
        }

        private static IUiComponent LiveFigure(string key, string name, IReadOnlyDictionary<string, string> figures, string className)
        {
            var text = figures.TryGetValue(name, out var value) ? value : "";
            var figure = new Span(new ValueText(text)).Class(className).Attribute("data-live", key).Attribute("data-live-text", name);
            return text.Length == 0 ? figure.Attribute("hidden", "hidden") : figure;
        }

        private static IUiComponent Stat(string labelKey, IUiComponent value, IUiComponent foot) =>
            new Div(new Span(new Text(labelKey)).Class("downloads-stat__label"), value, foot).Class("downloads-stat");

        // The limit of the shown client, chosen from a short list; the choice is the submit. All and a client that cannot be held back have none.
        private IUiComponent SpeedLimit(string? csrf)
        {
            if (_client is null || _limit is null || !DownloadClientKinds.LimitsSpeed(_client.DownloadClientKindId)) return new Span();
            var current = _limit.MaxBytesPerSecond;
            var values = Limits.Select(megabytes => megabytes * 1_000_000).ToList();
            if (current is { } custom && !values.Contains(custom)) values.Add(custom);
            var options = new List<IUiComponent> { Option(new Text("downloads.unlimited"), "", current is null) };
            options.AddRange(values.Order().Select(bytes => Option(new ValueText(ByteSize.Format(bytes) + "/s"), bytes.ToString(CultureInfo.InvariantCulture), current == bytes)));
            var select = new Select([.. options]).Class("choice-select__control").Attribute("name", "maxBytesPerSecond").Attribute("id", "downloads-limit").TextAttribute("aria-label", "downloads.speedLimit");
            return new Small(new Text("downloads.limit"), new ValueText(" "),
                new PostForm(Address(handler: "SpeedLimit"), csrf, [], select, new Button(new Text("common.apply")).Compact().Submit().Class("button button--compact choice-select__apply"))
                    .Class("choice-select downloads-limit")).Class("downloads-stat__foot");
        }

        private static IUiComponent Option(IUiComponent label, string value, bool selected)
        {
            var option = new Option(label).Attribute("value", value);
            return selected ? option.Attribute("selected", "selected") : option;
        }

        // The open downloads in their client's order: chosen with their box for the dock (shift-click for a range), dragged by their handle where the client reorders, filtered by category and priority.
        private IUiComponent QueuePanel(PageResult<ActivityItemV1> queue, string? csrf)
        {
            var all = _client is null;
            var columns = new List<ListColumn> { new("downloads.name", ListRole.Main) };
            if (all) columns.Add(new ListColumn("downloads.client", ListRole.Data, 2));
            columns.AddRange([new ListColumn("downloads.category", ListRole.Data, 2), new ListColumn("table.status", ListRole.Status), new ListColumn("downloads.progress"),
                new ListColumn("downloads.percent", ListRole.Data, Head: new Span()), new ListColumn("activity.size", ListRole.Data, 1), new ListColumn("downloads.speed"),
                new ListColumn("downloads.eta", ListRole.Data, 1), new ListColumn("common.actions", ListRole.Actions, Head: new Span())]);
            var empty = new Div(new IconTile(Glyph("download", 22), UiTone.Neutral), new P(new Text("downloads.queue.empty")));
            var reorders = _client is { } client && DownloadClientKinds.Reorders(client.DownloadClientKindId) && Category is null && Priority is null;
            var list = new ListPanel("downloads.queue", columns, [.. queue.Items.Select(item => QueueRow(item, all, reorders, csrf))], empty)
                .Modifier("downloads-list downloads-queue" + (all ? " downloads-list--all" : "") + (reorders ? " downloads-queue--sortable" : "")).Paged(primary: true);
            var filters = new List<IUiComponent> { new H2(new Text("downloads.queue")), new Span().Class("downloads-panel__spacer"), CategoryFilter() };
            if (Shown().Any(shown => DownloadClientKinds.HasPriorities(shown.DownloadClientKindId))) filters.Add(PriorityFilter());
            return new Section(new Div([.. filters]).Class("downloads-panel__head"), list).Class("downloads-panel");
        }

        private ListRow QueueRow(ActivityItemV1 item, bool all, bool reorders, string? csrf)
        {
            var phase = Phase(item);
            var says = Says(item);
            var id = item.Id.ToString(CultureInfo.InvariantCulture);
            IUiComponent pick = ListIdentity.Select("operationIds", id, BulkForm, "downloads.select");
            if (reorders)
            {
                pick = new Span(pick, new Button(Glyph("grip", 16)).IconOnly(texts["downloads.drag"]).Class("downloads-grip").Attribute("data-move-id", id).Attribute("title", texts["downloads.drag"]))
                    .Class("downloads-pick");
            }

            var cells = new List<IUiComponent> { Name(item, pick) };
            if (all) cells.Add(ClientCell(item));
            cells.Add(CategoryCell(item));
            cells.Add(StatusCell(item, phase));
            // A waiting or failed entry says why instead of how far it is.
            var progress = says ? null : item.ProgressPercent;
            cells.Add(ProgressCell(item, phase, progress));
            cells.Add(new Span(new Span(new ValueText(LiveEntities.Percent(progress))).Attribute("data-live-text", "percent")).Class("list-value")
                .Attribute("data-live", LiveKeys.Operation(item.Id)));
            cells.Add(Value(item.SizeBytes is { } size ? ByteSize.Format(size) : null));
            cells.Add(LiveValue(item, "speed", says ? "" : LiveEntities.OperationSpeed(texts, item.OperationStatusTypeId, item.DownloadStateTypeId, item.BytesPerSecond)));
            cells.Add(LiveValue(item, "eta", says ? "" : LiveFacts.Eta(item.EtaSeconds)));
            cells.Add(QueueActions(item, csrf));
            return new ListRow(cells, progress is null ? [] : [ListPanel.Fact("downloads.progress", new ValueText(LiveEntities.Percent(progress)))], (item.WorkTitle ?? "") + " " + item.Title);
        }

        // Pause or go on, put it first, take it out (after a question).
        private IUiComponent QueueActions(ActivityItemV1 item, string? csrf)
        {
            var actions = new List<IUiComponent>();
            var id = item.Id.ToString(CultureInfo.InvariantCulture);
            if (item.CanPause)
            {
                actions.Add(item.DownloadStateTypeId == DownloadStateType.Paused ? RowPost("Resume", id, "play", "activity.resume", csrf) : RowPost("Pause", id, "pause", "activity.pause", csrf));
            }

            if (item.DownloadClientKindId is not null) actions.Add(RowPost("MoveToTop", id, "arrow-up", "downloads.moveToTop", csrf));
            actions.Add(IconLink("trash", texts["downloads.remove"], Address(remove: item.Id), danger: true));
            return new Div([.. actions]).Class("list-actions downloads-actions");
        }

        // The finished downloads, newest first: failed ones can be tried again, completed ones lead to their title, every one can leave the history, and the completed ones all at once.
        private IUiComponent HistoryPanel(PageResult<ActivityItemV1> history, string? csrf)
        {
            var all = _client is null;
            var columns = new List<ListColumn> { new("downloads.name", ListRole.Main) };
            if (all) columns.Add(new ListColumn("downloads.client", ListRole.Data, 2));
            columns.AddRange([new ListColumn("table.status", ListRole.Status), new ListColumn("activity.reason", ListRole.Data, 1), new ListColumn("activity.size", ListRole.Data, 1),
                new ListColumn("downloads.finished"), new ListColumn("common.actions", ListRole.Actions, Head: new Span())]);
            var empty = new Div(new IconTile(Glyph("history", 22), UiTone.Neutral), new P(new Text("downloads.history.empty")));
            var list = new ListPanel("downloads.history", columns, [.. history.Items.Select(item => HistoryRow(item, all, csrf))], empty)
                .Modifier("downloads-list downloads-history" + (all ? " downloads-list--all" : "")).Paged(key: HistoryKey);
            var failedOnly = new A(new Text("downloads.failedOnly")).Class(FailedOnly ? "downloads-toggle downloads-toggle--on" : "downloads-toggle").Attribute("href", Address(failedOnly: !FailedOnly, historyPage: 1));
            if (FailedOnly) failedOnly.Attribute("aria-current", "true");
            var head = new List<IUiComponent> { new H2(new Text("downloads.history")), new Span().Class("downloads-panel__spacer"), failedOnly };
            if (history.Items.Count > 0)
            {
                head.Add(Modal.Opener(new Button(Glyph("trash", 16)).IconOnly(texts["downloads.clearCompleted"]).Attribute("title", texts["downloads.clearCompleted"]).Class("button button--icon downloads-icon"),
                    ClearDialog));
            }

            return new Section(new Div([.. head]).Class("downloads-panel__head"), list).Class("downloads-panel");
        }

        private ListRow HistoryRow(ActivityItemV1 item, bool all, string? csrf)
        {
            var phase = Phase(item);
            var id = item.Id.ToString(CultureInfo.InvariantCulture);
            var finished = item.FinishedAt ?? item.CreatedAt;
            var cells = new List<IUiComponent> { new Span(new Truncated(item.WorkTitle ?? item.Title)).Class("downloads-name downloads-name--plain") };
            if (all) cells.Add(ClientCell(item));
            cells.Add(new Div(new Span(new Text("operation.status." + item.OperationStatusTypeId)).Class("downloads-pill")).Class("downloads-phase").Attribute("data-phase", phase));
            cells.Add(item.ReasonKey is { } key && LiveEntities.Reason(texts, item.OperationTypeId, key) is { Length: > 0 } reason
                ? new Span(new Truncated(reason)).Class("list-value list-value--dim") : new Span());
            cells.Add(Value(item.SizeBytes is { } size ? ByteSize.Format(size) : null));
            cells.Add(new Span(new ValueText(Ago(finished))).Class("list-value list-value--dim").Attribute("title", LocalTime.Fallback(finished)));

            var actions = new List<IUiComponent>();
            if (item.CanRetry) actions.Add(RowPost("Retry", id, "sync", "activity.retry", csrf));
            else if (item.WorkId is { } workId) actions.Add(IconLink("info", texts["downloads.details"], "/Admin/Works/" + workId.ToString(CultureInfo.InvariantCulture)));
            actions.Add(RowPost("DeleteHistory", id, "trash", "downloads.deleteEntry", csrf, danger: true));
            cells.Add(new Div([.. actions]).Class("list-actions downloads-actions"));
            return new ListRow(cells, [ListPanel.Fact("downloads.finished", new ValueText(Ago(finished)), Glyph("clock", 14))]);
        }

        // How long ago a download ended, as the history says it.
        private string Ago(DateTime instant)
        {
            var minutes = (int)Math.Max(0, (DateTime.UtcNow - DateTime.SpecifyKind(instant, DateTimeKind.Utc)).TotalMinutes);
            return minutes < 1 ? texts["downloads.ago.now"]
                : minutes < 60 ? string.Format(CultureInfo.CurrentCulture, texts["downloads.ago.minutes"], minutes)
                : minutes < 1440 ? string.Format(CultureInfo.CurrentCulture, texts["downloads.ago.hours"], minutes / 60)
                : string.Format(CultureInfo.CurrentCulture, texts["downloads.ago.days"], minutes / 1440);
        }

        // The box (and the handle) and the title in bold; the title leads to its management page.
        private static IUiComponent Name(ActivityItemV1 item, IUiComponent pick)
        {
            IUiComponent title = item.WorkId is { } workId
                ? new A(new Truncated(item.WorkTitle ?? item.Title)).Attribute("href", "/Admin/Works/" + workId.ToString(CultureInfo.InvariantCulture)).Attribute("title", item.Title)
                : new Truncated(item.Title);
            return new Div(pick, new Strong(title).Class("downloads-name")).Class("downloads-namecell");
        }

        private IUiComponent ClientCell(ActivityItemV1 item) => item.DownloadClientKindId is { } kind
            ? new Span(kind is DownloadClientKind.Sabnzbd or DownloadClientKind.Qbittorrent ? new ValueText(item.ConnectionName ?? "") : new Text("downloads.client." + kind)).Class("downloads-kind")
            : Value(null);

        private IUiComponent CategoryCell(ActivityItemV1 item) => item.MediaTypeId is { } type ? new Span(new Text("media.type." + type)).Class("list-value list-value--dim") : Value(null);

        // The state pill follows the phase the stream sets, in the phase's colour.
        private IUiComponent StatusCell(ActivityItemV1 item, string phase) =>
            new Div(new Span(new Span(new ValueText(LiveEntities.OperationStatus(texts, item.IsWaiting, item.OperationStatusTypeId, Says(item) ? null : item.DownloadStateTypeId))).Attribute("data-live-text", "status"))
                .Class("downloads-pill")).Class("downloads-phase downloads-status").Attribute("data-phase", phase).Attribute("data-live", LiveKeys.Operation(item.Id));

        private IUiComponent ProgressCell(ActivityItemV1 item, string phase, int? progress)
        {
            var bar = new Progress().Attribute("max", "100").TextAttribute("aria-label", "live.progress").Class("downloads-bar").Attribute("data-live-value", "percent");
            if (progress is { } value) bar.Attribute("value", value.ToString(CultureInfo.InvariantCulture));
            return new Div(bar).Class("downloads-phase downloads-progress").Attribute("data-phase", phase).Attribute("data-live", LiveKeys.Operation(item.Id));
        }

        private static IUiComponent LiveValue(ActivityItemV1 item, string name, string text)
        {
            var field = new Span(new ValueText(text)).Attribute("data-live-text", name);
            if (text.Length == 0) field.Attribute("hidden", "hidden");
            return new Span(field).Class("list-value").Attribute("data-live", LiveKeys.Operation(item.Id));
        }

        private static IUiComponent Value(string? text) => text is null ? new Span(new ValueText("–")).Class("list-value list-value--dim") : new Span(new ValueText(text)).Class("list-value");

        // The phase as the stream names it: waiting for a person, else what the client does, else how the download ended.
        private static string Phase(ActivityItemV1 item)
        {
            var running = item.OperationStatusTypeId is OperationStatusType.Queued or OperationStatusType.Running;
            return (item.IsWaiting ? "waiting" : (running && item.DownloadStateTypeId is { } state ? state.ToString() : item.OperationStatusTypeId.ToString())).ToLowerInvariant();
        }

        private static bool Says(ActivityItemV1 item) => item.ReasonKey is not null && (item.IsWaiting || item.OperationStatusTypeId is OperationStatusType.Failed or OperationStatusType.Queued or OperationStatusType.Running);

        // The category and priority menus of the queue.
        private IUiComponent CategoryFilter() => new ChoiceSelect(ClientAddress(_client), "Category", "downloads.category",
            [new ChoiceOption("downloads.category.all", "", Category is null), .. Enum.GetValues<MediaType>().Select(type => new ChoiceOption("media.type." + type, ((byte)type).ToString(CultureInfo.InvariantCulture), Category == type))],
            Scope(withCategory: false));

        private IUiComponent PriorityFilter() => new ChoiceSelect(ClientAddress(_client), "Priority", "downloads.priority",
            [new ChoiceOption("downloads.priority.all", "", Priority is null), .. Enum.GetValues<DownloadPriorityType>().Select(priority => new ChoiceOption("downloads.priority." + priority, priority.ToString(), Priority == priority))],
            Scope(withPriority: false));

        // The chosen rows: pause, go on, put first, set a priority, read again, take out (asked first) and clear the choice.
        private ActionDock Dock()
        {
            var bulk = new List<IUiComponent>
            {
                BulkButton(nameof(DownloadBulkActionType.Pause), "pause", "downloads.pauseSelected"),
                BulkButton(nameof(DownloadBulkActionType.Resume), "play", "downloads.resumeSelected"),
                BulkButton(nameof(DownloadBulkActionType.MoveToTop), "arrow-up", "downloads.moveToTop")
            };
            if (Shown().Any(client => DownloadClientKinds.HasPriorities(client.DownloadClientKindId)))
            {
                bulk.Add(Modal.Opener(new Button(Glyph("star", 18)).IconOnly(texts["downloads.setPriority"]).Attribute("title", texts["downloads.setPriority"]), PriorityDialog));
            }

            bulk.Add(BulkButton(nameof(DownloadBulkActionType.Retry), "sync", "downloads.retrySelected"));
            bulk.Add(Modal.Opener(new Button(Glyph("trash", 18)).IconOnly(texts["downloads.removeSelected"]).Attribute("title", texts["downloads.removeSelected"]).Class("button button--icon button--danger"), BulkRemoveDialog));
            bulk.Add(new Button(Glyph("close", 18)).IconOnly(texts["downloads.clearSelection"]).Attribute("title", texts["downloads.clearSelection"]).Attribute("data-dock-clear", ""));
            return new ActionDock(assets, "downloads.actions", [], bulk);
        }

        private IUiComponent BulkButton(string value, string icon, string labelKey) =>
            new Button(Glyph(icon, 18)).IconOnly(texts[labelKey]).Submit().Attribute("title", texts[labelKey]).Attribute("form", BulkForm).Attribute("name", "bulk").Attribute("value", value);

        // Taking downloads out deletes their partial files, so it asks first; the release may also be blocked.
        private IUiComponent RemoveDialog(ActivityItemV1 item, string? csrf)
        {
            var block = new Button(new Text("activity.removeAndBlock"));
            block.Attribute("name", "blocksRelease").Attribute("value", "true");
            var only = new Button(new Text("activity.remove")).Warn();
            only.Attribute("name", "blocksRelease").Attribute("value", "false");
            return new Modal(assets, "downloads-remove", "downloads.removeConfirm",
                [new Form<RemoveActivityDownloadDataV1>(form => form.Cancel().Submit(block).Submit(only),
                    Address(handler: "Remove") + "&operationId=" + item.Id.ToString(CultureInfo.InvariantCulture), csrf, new RemoveActivityDownloadDataV1(false))],
                open: true, returnTo: Address());
        }

        private IUiComponent BulkRemove() => new Modal(assets, BulkRemoveDialog, "downloads.removeSelectedConfirm",
            [new Div(new Button(new Text("common.cancel")).Attribute("data-dialog-close", ""),
                new Button(new Text("activity.removeAndBlock")).Submit().Attribute("form", BulkForm).Attribute("name", "bulk").Attribute("value", nameof(DownloadBulkActionType.RemoveAndBlock)),
                new Button(new Text("activity.remove")).Warn().Submit().Attribute("form", BulkForm).Attribute("name", "bulk").Attribute("value", nameof(DownloadBulkActionType.Remove))).Class("ui-form__actions")]);

        // The chosen rows take one of the four priorities; a client without priorities leaves its rows as they are.
        private IUiComponent PriorityChoice() => new Modal(assets, PriorityDialog, "downloads.setPriority",
            [new Div([new Button(new Text("common.cancel")).Attribute("data-dialog-close", ""), .. Enum.GetValues<DownloadPriorityType>().Reverse().Select(priority =>
                (IUiComponent)new Button(new Text("downloads.priority." + priority)).Submit().Attribute("form", BulkForm)
                    .Attribute("name", "priority").Attribute("value", priority.ToString()))]).Class("ui-form__actions")]);

        // Clearing the completed downloads of the shown client (of all on All) asks first.
        private IUiComponent ClearHistory(string? csrf) => new Modal(assets, ClearDialog, "downloads.clearConfirm",
            [new PostForm(Address(handler: "ClearHistory"), csrf, [], new Div(new Button(new Text("common.cancel")).Attribute("data-dialog-close", ""),
                new Button(new Text("downloads.clearCompleted")).Warn().Submit()).Class("ui-form__actions"))]);

        // A download by hand: find the wanted title, then give the NZB, torrent or magnet address; it goes to the shown client (on All to the best one for the address).
        private IUiComponent AddDialog(string labelKey, string? csrf)
        {
            // The title search keeps its own field name, apart from the header's search.
            var fields = new List<IUiComponent>([.. Scope().Select(entry => (IUiComponent)new Input().Attribute("type", "hidden").Attribute("name", entry.Key).Attribute("value", entry.Value))]);
            fields.Add(new Input().Attribute("type", "hidden").Attribute("name", "add").Attribute("value", "true"));
            fields.Add(new Input().Attribute("type", "search").Attribute("name", "addq").Attribute("value", AddQuery ?? "").Attribute("id", "downloads-add-query").Class("search-box__input")
                .TextAttribute("placeholder", "downloads.add.findTitle").TextAttribute("aria-label", "downloads.add.findTitle"));
            var search = new FormElement([.. fields]).Class("search-box downloads-add__search").Attribute("action", "/Admin/Downloads").Attribute("method", "get").Attribute("role", "search");
            var body = new List<IUiComponent> { search };
            if (_titles is { Count: > 0 } titles)
            {
                var choices = titles.Select((title, index) => (IUiComponent)new Checkbox(new ValueText(title.CanonicalTitle + (title.ReleaseYear is { } year ? " (" + year.ToString(CultureInfo.InvariantCulture) + ")" : "")),
                    "wantedItemId", title.WantedItemId.ToString(CultureInfo.InvariantCulture), index == 0, radio: true)).ToList();
                var address = new Input().Attribute("type", "text").Attribute("name", "downloadUrl").Attribute("id", "downloads-add-url").Attribute("required", "required").Class("ui-field__control downloads-add__address")
                    .TextAttribute("placeholder", _client?.DownloadClientKindId is not { } kind ? "downloads.add.anyAddress" : DownloadClientKinds.AddsTorrent(kind) ? "downloads.add.torrentAddress" : "downloads.add.nzbAddress");
                body.Add(new PostForm(Address(handler: "Add"), csrf, [],
                    new Div([.. choices]).Class("downloads-add__titles"),
                    new Label(new Text("downloads.add.addressLabel")).Attribute("for", "downloads-add-url").Class("downloads-add__label"),
                    address,
                    new Div(new Button(new Text("common.cancel")).Attribute("data-dialog-close", ""), new Button(new Text(labelKey)).Primary().Submit()).Class("ui-form__actions")).Class("downloads-add"));
            }
            else if (!string.IsNullOrWhiteSpace(AddQuery))
            {
                body.Add(new P(new Text("downloads.add.none")).Class("downloads-add__none"));
            }

            return new Modal(assets, "downloads-add", labelKey, body, open: true, returnTo: Address());
        }

        // What belongs to the chosen client only: its news servers, its connection and route, its tunnel and workspace, or its sources.
        private IEnumerable<IUiComponent> ClientPanels(string? csrf)
        {
            switch (_client?.DownloadClientKindId)
            {
                case DownloadClientKind.Usenet:
                    yield return NewsServers();
                    break;
                case DownloadClientKind.Sabnzbd or DownloadClientKind.Qbittorrent:
                    if (_connections.FirstOrDefault(item => item.Id == _client.SettingsConnectionId) is { } connection)
                    {
                        yield return new Div(ClientConnection(connection, csrf), Route(connection)).Class("downloads-pair");
                    }

                    break;
                case DownloadClientKind.Torrent:
                    var panels = new List<IUiComponent>();
                    if (_engine is { } engine) panels.Add(TunnelPanel(engine));
                    if (_storage is { } storage) panels.Add(WorkspacePanel(storage));
                    yield return new Div([.. panels]).Class("downloads-pair");
                    break;
                case DownloadClientKind.Direct:
                    yield return SourcesPanel(csrf);
                    break;
            }
        }

        // Each news server with its state and what its last pass measured, kept current by the stream.
        private IUiComponent NewsServers()
        {
            var columns = new List<ListColumn> { new("downloads.server", ListRole.Main), new("downloads.state", ListRole.Status), new("downloads.connections"), new("downloads.speed"),
                new("downloads.missingArticles", ListRole.Data, 1) };
            var rows = _connections.Where(item => item.AcquisitionConnectionTypeId == AcquisitionConnectionType.Usenet).Select(item =>
            {
                var key = LiveKeys.NewsServer(item.Id);
                var figures = (_newsServers.FirstOrDefault(server => server.AcquisitionConnectionId == item.Id) is { } server
                    ? LiveEntities.NewsServer(texts, server) : LiveEntities.NewsServer(texts, new NewsServerV1(item.Id, 0, 0, 0, 0, item.MaxConnections ?? 8))).Text;
                return new ListRow(
                [
                    new Span(new Truncated(item.Name)).Class("downloads-name downloads-name--plain"),
                    ConnectionState(item),
                    LiveFigure(key, "connections", figures, "list-value"),
                    LiveFigure(key, "speed", figures, "list-value"),
                    LiveFigure(key, "missing", figures, "list-value")
                ]);
            }).ToList();
            var add = new A(new Text("downloads.addServer")).Class("downloads-small").Attribute("href", "/Admin/Downloads/Servers?PageNumber=1&Type=" + AcquisitionConnectionType.Usenet);
            return new Section(new Div(new H2(new Text("downloads.newsServers")), new Span().Class("downloads-panel__spacer"), add).Class("downloads-panel__head"),
                new ListPanel("downloads.newsServers", columns, rows).Modifier("downloads-list downloads-servers")).Class("downloads-panel");
        }

        // A SABnzbd or qBittorrent: its state, where it is, the categories it files downloads under and its completed folder; Test asks it now.
        private IUiComponent ClientConnection(AcquisitionConnectionDetailsV1 connection, string? csrf)
        {
            var facts = new List<(string, IUiComponent)> { ("downloads.address", new ValueText(connection.BaseUrl ?? "")) };
            var categories = _dashboard is { Categories.Length: > 0 } dashboard ? string.Join(", ", dashboard.Categories) : connection.DownloadCategory;
            if (categories is not null) facts.Add(("downloads.categoryMapping", new ValueText(categories)));
            if ((connection.LocalPath ?? connection.RemotePath) is { } folder) facts.Add(("downloads.completedFolder", new ValueText(folder)));
            var testButton = new PostForm(Address(handler: "Test"), csrf, [], new Button(Glyph("pulse", 16), new Text("downloads.test")).Compact().Submit().Class("button button--compact downloads-small"));
            return new Section(new Div(new H2(new ValueText(connection.Name)), new Span().Class("downloads-panel__spacer"), ConnectionState(connection)).Class("downloads-panel__head"),
                Facts(facts, testButton)).Class("downloads-panel");
        }

        // What Jularr sends to the client and in which order: Usenet or torrent releases, its priority among the clients; SABnzbd seeds nothing.
        private IUiComponent Route(AcquisitionConnectionDetailsV1 connection)
        {
            var sabnzbd = connection.AcquisitionConnectionTypeId == AcquisitionConnectionType.Sabnzbd;
            var facts = new List<(string, IUiComponent)>
            {
                ("downloads.usedFor", new Text(sabnzbd ? "downloads.usedFor.usenet" : "downloads.usedFor.torrent")),
                ("downloads.clientPriority", new ValueText(connection.Priority.ToString(CultureInfo.CurrentCulture)))
            };
            if (sabnzbd) facts.Add(("downloads.seeding", new Text("downloads.seeding.none")));
            return new Section(new Div(new H2(new Text("downloads.route"))).Class("downloads-panel__head"), Facts(facts)).Class("downloads-panel");
        }

        // The torrent engine's way out: the tunnel it is bound to, where it leaves, and the upload lock it proved at this read.
        private IUiComponent TunnelPanel(TorrentEngineV1 engine)
        {
            var facts = new List<(string, IUiComponent)>();
            if (_vpn is { } profile)
            {
                facts.Add(("downloads.vpnProfile", new ValueText(profile.Name)));
                if (profile.Tunnel.ExitAddress is { } exit) facts.Add(("downloads.exitAddress", new ValueText(exit + (profile.Tunnel.ExitCountryCode is { } country ? " · " + country : ""))));
            }

            facts.Add(("acquisition.torrent.lock", new Span(new Text(engine.IsLockVerified ? "acquisition.torrent.lockVerified" : "acquisition.torrent.lockFailed"),
                new ValueText(" · " + ByteSize.Format(engine.SentPieceBytes) + " "), new Text("acquisition.torrent.fileData"))));
            if (engine.HasVerifiedTunnel) facts.Add(("downloads.withoutTunnel", new Text("downloads.withoutTunnel.nothing")));
            var state = engine.HasVerifiedTunnel ? Pill("downloads.tunnel.connected", "completed") : Pill("acquisition.torrent.vpnNone", "failed");
            var check = texts.Can("instance.settings.read", "Vpn") ? new A(new Text("downloads.checkTunnel")).Class("downloads-small").Attribute("href", "/Admin/Network") : null;
            return new Section(new Div(new H2(new Text("downloads.vpnPath")), new Span().Class("downloads-panel__spacer"), state).Class("downloads-panel__head"), Facts(facts, check))
                .Class("downloads-panel");
        }

        private IUiComponent WorkspacePanel(DownloadStorageSettingsV1 storage) => new Section(
            new Div(new H2(new Text("downloads.workspace"))).Class("downloads-panel__head"),
            Facts([
                ("downloadStorage.incomplete", new ValueText(storage.IncompletePath ?? "–")),
                ("downloadStorage.complete", new ValueText(storage.CompletePath ?? "–")),
                ("downloads.parallel", new ValueText(storage.MaxParallelJobs.ToString(CultureInfo.CurrentCulture))),
                ("downloads.speedLimit", storage.MaxBytesPerSecond is { } limit ? new ValueText(ByteSize.Format(limit) + "/s") : new Text("downloads.unlimited"))
            ])).Class("downloads-panel");

        // Each direct source with its state, what it waits for and its last result; a download that needs a person's link takes the link in a dialog and names its source page.
        private IUiComponent SourcesPanel(string? csrf)
        {
            var columns = new List<ListColumn> { new("downloads.source", ListRole.Main), new("downloads.state", ListRole.Status), new("downloads.waitingFor"), new("downloads.lastResult") };
            var open = _queue?.Items ?? [];
            var finished = _history?.Items ?? [];
            var rows = _connections.Where(item => DownloadClientKinds.Of(item.AcquisitionConnectionTypeId) == DownloadClientKind.Direct).Select(item =>
            {
                var waiting = open.FirstOrDefault(download => download.ConnectionName == item.Name && download.IsWaiting && download.ReasonKey is not null);
                var last = finished.FirstOrDefault(download => download.ConnectionName == item.Name);
                return new ListRow(
                [
                    new Span(new Truncated(item.Name)).Class("downloads-name downloads-name--plain"),
                    waiting is null ? ConnectionState(item) : Pill("activity.waiting", "queued"),
                    waiting is { ReasonKey: { } reason } ? new Span(new Truncated(LiveEntities.Reason(texts, waiting.OperationTypeId, reason) ?? "")).Class("list-value") : Value(null),
                    last is null ? Value(null) : new Span(new Truncated((last.WorkTitle ?? last.Title) + ", " + Ago(last.FinishedAt ?? last.CreatedAt))).Class("list-value")
                ]);
            }).ToList();
            rows.AddRange(open.Where(item => item.ManualUrl is not null).Select(item => new ListRow(
            [
                new Span(new Truncated(item.WorkTitle ?? item.Title)).Class("downloads-name downloads-name--plain"),
                Pill("downloads.needsLink", "failed"),
                new Div(Modal.Opener(new Button(new Text("downloads.pasteLink")).Primary().Class("button button--primary downloads-small"), "downloads-link-" + item.Id.ToString(CultureInfo.InvariantCulture)),
                    new A(new Text("downloads.openPage")).Class("downloads-small").Attribute("href", item.ManualUrl!).Attribute("target", "_blank").Attribute("rel", "noopener noreferrer"),
                    LinkDialog(item, csrf)).Class("downloads-source-link"),
                new Span(new Text("downloads.checkBrowser")).Class("list-value list-value--dim")
            ])));
            var storage = new A(new Text("downloadStorage.title")).Class("downloads-small").Attribute("href", "/Admin/DownloadStorage");
            return new Section(new Div(new H2(new Text("downloads.sources")), new Span().Class("downloads-panel__spacer"), storage).Class("downloads-panel__head"),
                new ListPanel("downloads.sources", columns, rows).Modifier("downloads-list downloads-sources")).Class("downloads-panel");
        }

        // The link a person found for a download, given in a dialog.
        private IUiComponent LinkDialog(ActivityItemV1 item, string? csrf) => new Modal(assets, "downloads-link-" + item.Id.ToString(CultureInfo.InvariantCulture), "downloads.pasteLink",
            [new Form<SaveActivitySourceLinkDataV1>(form =>
            {
                form.Text(data => data.SourceLinkUrl, "activity.fileLink");
                form.Cancel();
                form.Submit("downloads.pasteLink");
            }, Address(handler: "SourceLink") + "&operationId=" + item.Id.ToString(CultureInfo.InvariantCulture), csrf, new SaveActivitySourceLinkDataV1(""),
                FormError?.Field == nameof(SaveActivitySourceLinkDataV1.SourceLinkUrl) ? FormError : null)]);

        // A connection's state as a pill in the colour of its meaning: switched off, failing and waiting to try again, failing, not tested yet or reachable.
        private IUiComponent ConnectionState(AcquisitionConnectionDetailsV1 item) =>
            !item.IsEnabled ? Pill("acquisition.connection.off", "queued")
            : item is { IsBackedOff: true, RetryAt: not null } ? Pill("acquisition.connection.state.backoff", "paused")
            : item.ErrorKey is not null ? Pill("acquisition.connection.state.error", "failed")
            : item.CheckedAt is null ? Pill("acquisition.connection.unchecked", "queued")
            : Pill("acquisition.connection.healthy", "completed");

        private static IUiComponent Pill(string key, string phase) => new Span(new Span(new Text(key)).Class("downloads-pill")).Class("downloads-phase").Attribute("data-phase", phase);

        // A client's settings at a glance: label above a quiet box, two columns; an action under them.
        private static IUiComponent Facts(IReadOnlyList<(string LabelKey, IUiComponent Value)> facts, IUiComponent? action = null) => new Div([.. facts.Select(fact =>
            (IUiComponent)new Div(new Span(new Text(fact.LabelKey)).Class("downloads-facts__label"), new Div(fact.Value).Class("downloads-facts__value"))),
            .. action is null ? [] : new[] { new Div(action).Class("downloads-facts__action") }]).Class("downloads-facts");

        private IUiComponent IconLink(string icon, string label, string href, bool danger = false) =>
            new A(Glyph(icon, 16)).Class(danger ? "button button--icon button--danger downloads-icon" : "button button--icon downloads-icon").Attribute("href", href).Attribute("aria-label", label).Attribute("title", label);

        private IUiComponent IconPost(string handler, string icon, string labelKey, string? csrf) =>
            new PostForm(Address(handler: handler), csrf, [], new Button(Glyph(icon, 16)).IconOnly(texts[labelKey]).Submit().Attribute("title", texts[labelKey]).Class("button button--icon downloads-icon"));

        private IUiComponent RowPost(string handler, string id, string icon, string labelKey, string? csrf, bool danger = false)
        {
            var button = new Button(Glyph(icon, 16)).IconOnly(texts[labelKey]).Submit().Attribute("title", texts[labelKey]);
            button.Class(danger ? "button button--icon button--danger downloads-icon" : "button button--icon downloads-icon");
            return new PostForm(Address(handler: handler) + "&operationId=" + id, csrf, [], button);
        }

        private IUiComponent Glyph(string name, int size = 20) => new Icon(name, assets, size);

        private IReadOnlyList<DownloadClientV1> Shown() => _client is { } client ? [client] : _clients;

        // SABnzbd, qBittorrent and the torrent engine open their card among the external clients; Jularr's Usenet downloader its news servers, its direct sources the indexers that list them.
        private static string SettingsAddress(DownloadClientV1 client) => client.DownloadClientKindId switch
        {
            DownloadClientKind.Usenet => "/Admin/Downloads/Servers",
            DownloadClientKind.Direct => "/Admin/Acquisition",
            _ => "/Admin/Downloads/Clients?PageNumber=1&AcquisitionConnectionId=" + client.SettingsConnectionId.ToString(CultureInfo.InvariantCulture)
        };

        public async Task<IActionResult> OnGetAsync()
        {
            await LoadDataAsync();
            PreparePage();
            return Page();
        }

        public async Task<IActionResult> OnPostPauseAsync([BindRequired] long operationId) => await PauseAsync(operationId, true);

        public async Task<IActionResult> OnPostResumeAsync([BindRequired] long operationId) => await PauseAsync(operationId, false);

        private async Task<IActionResult> PauseAsync(long operationId, bool isPaused)
        {
            await pause.ExecuteAsync<UpdateResult>(new ActivityOperationParametersV1(operationId), new PauseActivityDownloadDataV1(isPaused), HttpContext.RequestAborted);
            return LocalRedirect(Address());
        }

        public async Task<IActionResult> OnPostRemoveAsync([BindRequired] long operationId, bool blocksRelease)
        {
            await remove.ExecuteAsync<UpdateResult>(new ActivityOperationParametersV1(operationId), new RemoveActivityDownloadDataV1(blocksRelease), HttpContext.RequestAborted);
            return LocalRedirect(Address());
        }

        public async Task<IActionResult> OnPostRetryAsync([BindRequired] long operationId)
        {
            await retry.ExecuteAsync<UpdateResult>(new ActivityOperationParametersV1(operationId), default, HttpContext.RequestAborted);
            return LocalRedirect(Address());
        }

        public async Task<IActionResult> OnPostRetryImportAsync([BindRequired] long operationId)
        {
            await retryImport.ExecuteAsync<UpdateResult>(new ActivityOperationParametersV1(operationId), default, HttpContext.RequestAborted);
            return LocalRedirect(Address());
        }

        public async Task<IActionResult> OnPostSourceLinkAsync([BindRequired] long operationId, string? sourceLinkUrl)
        {
            await sourceLink.ExecuteAsync<UpdateResult>(new ActivityOperationParametersV1(operationId), new SaveActivitySourceLinkDataV1(sourceLinkUrl ?? ""), HttpContext.RequestAborted);
            return LocalRedirect(Address());
        }

        // One row first in its client's queue.
        public async Task<IActionResult> OnPostMoveToTopAsync([BindRequired] long operationId)
        {
            await apply.ExecuteAsync<UpdateResult>(new ApplyActivityDownloadsParametersV1(), new ApplyActivityDownloadsDataV1([operationId], DownloadBulkActionType.MoveToTop), HttpContext.RequestAborted);
            return LocalRedirect(Address());
        }

        // A row dragged before another (or to the end of the list).
        public async Task<IActionResult> OnPostMoveAsync([BindRequired] long operationId, long? beforeOperationId)
        {
            await move.ExecuteAsync<UpdateResult>(new ActivityOperationParametersV1(operationId), new MoveActivityDownloadDataV1(beforeOperationId), HttpContext.RequestAborted);
            return LocalRedirect(Address());
        }

        // The chosen rows of the queue get an action or a priority; none chosen does nothing.
        public async Task<IActionResult> OnPostBulkAsync(long[]? operationIds, DownloadBulkActionType? bulk, DownloadPriorityType? priority)
        {
            if (operationIds is { Length: > 0 } && priority is { } level && Enum.IsDefined(level))
            {
                await prioritize.ExecuteAsync<UpdateResult>(new ApplyActivityDownloadsParametersV1(), new PrioritizeActivityDownloadsDataV1(operationIds, level), HttpContext.RequestAborted);
            }
            else if (operationIds is { Length: > 0 } && bulk is { } action && Enum.IsDefined(action))
            {
                await apply.ExecuteAsync<UpdateResult>(new ApplyActivityDownloadsParametersV1(), new ApplyActivityDownloadsDataV1(operationIds, action), HttpContext.RequestAborted);
            }

            return LocalRedirect(Address());
        }

        public async Task<IActionResult> OnPostDeleteHistoryAsync([BindRequired] long operationId)
        {
            await deleteHistory.ExecuteAsync<UpdateResult>(new ApplyActivityDownloadsParametersV1(), new DeleteActivityHistoryDataV1([operationId]), HttpContext.RequestAborted);
            return LocalRedirect(Address(historyPage: 1));
        }

        public async Task<IActionResult> OnPostClearHistoryAsync()
        {
            await clearHistory.ExecuteAsync<UpdateResult>(new DownloadClientParametersV1(Client, ConnectionId), default, HttpContext.RequestAborted);
            return LocalRedirect(Address(historyPage: 1));
        }

        // The shown client waits or goes on; on All the whole queue.
        public async Task<IActionResult> OnPostPauseClientAsync() => await PauseClientAsync(true);

        public async Task<IActionResult> OnPostResumeClientAsync() => await PauseClientAsync(false);

        private async Task<IActionResult> PauseClientAsync(bool isPaused)
        {
            await pauseQueue.ExecuteAsync<UpdateResult>(new PauseActivityQueueParametersV1(Client, Client is DownloadClientKind.Sabnzbd or DownloadClientKind.Qbittorrent ? ConnectionId : null),
                new PauseActivityDownloadDataV1(isPaused), HttpContext.RequestAborted);
            return LocalRedirect(Address());
        }

        public async Task<IActionResult> OnPostSpeedLimitAsync(long? maxBytesPerSecond)
        {
            await setSpeedLimit.ExecuteAsync<UpdateResult>(new DownloadClientParametersV1(Client, ConnectionId), new SetDownloadSpeedLimitDataV1(maxBytesPerSecond is > 0 ? maxBytesPerSecond : null),
                HttpContext.RequestAborted);
            return LocalRedirect(Address());
        }

        public async Task<IActionResult> OnPostAddAsync([BindRequired] long wantedItemId, string? downloadUrl)
        {
            await add.ExecuteAsync<UpdateResult>(new DownloadClientParametersV1(Client, ConnectionId), new AddActivityDownloadDataV1(wantedItemId, downloadUrl ?? ""), HttpContext.RequestAborted);
            return LocalRedirect(Address());
        }

        public async Task<IActionResult> OnPostTestAsync()
        {
            await LoadClientsAsync();
            if (_client is not { DownloadClientKindId: DownloadClientKind.Sabnzbd or DownloadClientKind.Qbittorrent } client) return LocalRedirect(Address());
            var result = await test.ExecuteAsync<AcquisitionConnectionTestV1>(new AcquisitionConnectionParametersV1(client.SettingsConnectionId), default, HttpContext.RequestAborted);
            HintText(result.IsAvailable
                ? string.Join(" · ", new[] { texts["acquisition.connection.healthy"], result.ServerVersion is { } version ? texts["table.version"] + " " + version : null }.OfType<string>())
                : texts["errors." + (result.ErrorKey ?? "acquisition_unavailable")]);
            return LocalRedirect(Address());
        }

        protected override async Task LoadDataAsync()
        {
            var ct = HttpContext.RequestAborted;
            await LoadClientsAsync();
            var kind = _client?.DownloadClientKindId;
            var connectionId = _client?.AcquisitionConnectionId;
            _queue = await read.ExecuteAsync<PageResult<ActivityItemV1>>(new ReadActivityParametersV1(ActivityFilterType.All, Math.Max(1, PageNumber), downloadsOnly: true,
                downloadClientKindId: kind, acquisitionConnectionId: connectionId, mediaTypeId: Category, downloadPriorityTypeId: Priority), ct);
            _history = await read.ExecuteAsync<PageResult<ActivityItemV1>>(new ReadActivityParametersV1(ActivityFilterType.History, Math.Max(1, HistoryPage), downloadsOnly: true, failedOnly: FailedOnly,
                downloadClientKindId: kind, acquisitionConnectionId: connectionId), ct);

            // The add dialog's titles: every wanted title whose name holds the search.
            if (Adding && !string.IsNullOrWhiteSpace(AddQuery))
            {
                _titles = await findTitles.ExecuteAsync<BaseDataList<ManualImportWorkV1>>(new ManualImportWorksParametersV1(AddQuery, anyMediaType: true), ct);
            }

            // The client's speed limit; a client that does not answer shows none.
            if (_client is { } client && DownloadClientKinds.LimitsSpeed(client.DownloadClientKindId))
            {
                try
                {
                    _limit = await readSpeedLimit.ExecuteAsync<DownloadSpeedLimitV1>(new DownloadClientParametersV1(client.DownloadClientKindId, client.AcquisitionConnectionId), ct);
                }
                catch (ServiceUnavailableException)
                {
                    _limit = null;
                }
            }

            // The chosen client's own panel.
            switch (kind)
            {
                case DownloadClientKind.Usenet or DownloadClientKind.Sabnzbd or DownloadClientKind.Qbittorrent:
                    _connections = (await connections.ExecuteAsync<PageResult<AcquisitionConnectionDetailsV1>>(new ReadAcquisitionConnectionsParametersV1(1, 100, AcquisitionConnectionRole.Download), ct)).Items;
                    if (kind == DownloadClientKind.Usenet)
                    {
                        _newsServers = await newsServers.ExecuteAsync<BaseDataList<NewsServerV1>>(new ReadDownloadClientsParametersV1(), ct);
                        await LoadWorkspaceAsync(DownloadStorageArea.Usenet, ct);
                    }
                    else if (kind == DownloadClientKind.Sabnzbd && _client is { AcquisitionConnectionId: { } sabnzbd })
                    {
                        // SABnzbd reports its own free space and categories; a SABnzbd that does not answer shows neither.
                        try
                        {
                            _dashboard = await inspect.ExecuteAsync<DownloadClientDashboardV1>(new AcquisitionConnectionParametersV1(sabnzbd), default, ct);
                            if (_dashboard.FreeBytes is { } free) _space = (free, null);
                        }
                        catch (ServiceUnavailableException)
                        {
                            _dashboard = null;
                        }
                    }

                    break;
                case DownloadClientKind.Direct:
                    _connections = (await connections.ExecuteAsync<PageResult<AcquisitionConnectionDetailsV1>>(new ReadAcquisitionConnectionsParametersV1(1, 100, AcquisitionConnectionRole.Search), ct)).Items;
                    await LoadWorkspaceAsync(DownloadStorageArea.Direct, ct);
                    break;
                case DownloadClientKind.Torrent:
                    _engine = await torrentEngine.ExecuteAsync<TorrentEngineV1>(new ReadTorrentEngineParametersV1(), ct);
                    if (texts.Can("instance.settings.read", "Vpn"))
                    {
                        var profiles = await vpnProfiles.ExecuteAsync<BaseDataList<VpnProfileV1>>(new ReadVpnProfilesParametersV1(), ct);
                        _vpn = profiles.FirstOrDefault(profile => profile.Tunnel.Leases > 0) ?? profiles.FirstOrDefault(profile => profile.Tunnel.Phase == VpnTunnelPhase.Connected);
                    }

                    await LoadWorkspaceAsync(DownloadStorageArea.Torrent, ct);
                    break;
                case null:
                    // All: the fullest of the workspaces of Jularr's own downloaders that are shown.
                    foreach (var area in _clients.Select(client => client.DownloadClientKindId switch
                    {
                        DownloadClientKind.Usenet => DownloadStorageArea.Usenet,
                        DownloadClientKind.Direct => DownloadStorageArea.Direct,
                        DownloadClientKind.Torrent => DownloadStorageArea.Torrent,
                        _ => (DownloadStorageArea?)null
                    }).OfType<DownloadStorageArea>().Distinct())
                    {
                        await LoadWorkspaceAsync(area, ct);
                    }

                    _storage = null;
                    break;
            }
        }

        // Every switched-on client the account may see (the torrent engine only with its module); the address names the chosen one, else All.
        private async Task LoadClientsAsync()
        {
            var clients = await readClients.ExecuteAsync<BaseDataList<DownloadClientV1>>(new ReadDownloadClientsParametersV1(), HttpContext.RequestAborted);
            _clients = [.. clients.Where(client => client.DownloadClientKindId != DownloadClientKind.Torrent || texts.Can(null, "Torrent"))];
            _client = Client is { } kind ? _clients.FirstOrDefault(client => client.DownloadClientKindId == kind && (client.AcquisitionConnectionId is null || client.AcquisitionConnectionId == ConnectionId)) : null;
            Client = _client?.DownloadClientKindId;
            ConnectionId = _client?.AcquisitionConnectionId;
        }

        // The client's own workspace: its folders, limits and the free space last measured where it downloads (the least of those read).
        private async Task LoadWorkspaceAsync(DownloadStorageArea area, CancellationToken ct)
        {
            _storage = await storageSettings.ExecuteAsync<DownloadStorageSettingsV1>(new DownloadStorageParametersV1(area), ct);
            if (_storage is { IsConfigured: true, IncompleteStorageMountId: { } mountId } && texts.Can("storage.read")
                && await storageMount.ExecuteAsync<StorageMountDetailsV1>(new StorageMountParametersV1(mountId), ct) is { StorageHealthTypeId: StorageHealthType.Online, FreeBytes: { } free }
                && (_space is null || free < _space.Value.Free))
            {
                _space = (free, _storage.FreeSpaceReserveBytes);
            }
        }

        // The scope of the address (client, filters) that every link and form keeps.
        private IReadOnlyDictionary<string, string> Scope(bool withCategory = true, bool withPriority = true)
        {
            var scope = new Dictionary<string, string>();
            if (Client is { } kind) scope["Client"] = kind.ToString();
            if (ConnectionId is { } id) scope["ConnectionId"] = id.ToString(CultureInfo.InvariantCulture);
            if (withCategory && Category is { } category) scope["Category"] = ((byte)category).ToString(CultureInfo.InvariantCulture);
            if (withPriority && Priority is { } priority) scope["Priority"] = priority.ToString();
            if (FailedOnly) scope["FailedOnly"] = "true";
            return scope;
        }

        private static string ClientAddress(DownloadClientV1? client) => client is null ? "/Admin/Downloads"
            : "/Admin/Downloads?Client=" + client.DownloadClientKindId + (client.AcquisitionConnectionId is { } id ? "&ConnectionId=" + id.ToString(CultureInfo.InvariantCulture) : "");

        private string Address(string? handler = null, long? remove = null, bool? failedOnly = null, int? historyPage = null, bool add = false)
        {
            var query = Scope().Where(entry => entry.Key != "FailedOnly").Select(entry => entry.Key + "=" + Uri.EscapeDataString(entry.Value)).ToList();
            if (failedOnly ?? FailedOnly) query.Add("FailedOnly=true");
            if (PageNumber > 1) query.Add("PageNumber=" + PageNumber.ToString(CultureInfo.InvariantCulture));
            if ((historyPage ?? HistoryPage) > 1) query.Add(HistoryKey + "=" + (historyPage ?? HistoryPage).ToString(CultureInfo.InvariantCulture));
            if (handler is not null) query.Add("handler=" + handler);
            if (remove is { } id) query.Add("remove=" + id.ToString(CultureInfo.InvariantCulture));
            if (add) query.Add("add=true");
            return "/Admin/Downloads" + (query.Count > 0 ? "?" + string.Join("&", query) : "");
        }
    }
}
