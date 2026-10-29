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
using Jularr.Frontend.Framework;
using Jularr.Frontend.Modules.Live;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Jularr.Frontend.Pages.Admin.Downloads
{
    // Every download in one place, SABnzbd style: a pill tab per client (All first) with the client's icon buttons, its figures, its queue and history, and below them what belongs to that client only.
    public sealed class DownloadsModel(ReadDownloadClientsV1 readClients, ReadActivityV1 read, ApplyActivityDownloadsV1 apply, PauseActivityQueueV1 pauseQueue, PauseActivityDownloadV1 pause,
        RemoveActivityDownloadV1 remove, RetryActivityV1 retry, RetryActivityImportV1 retryImport, SaveActivitySourceLinkV1 sourceLink, ReadAcquisitionConnectionsV1 connections,
        TestAcquisitionConnectionV1 test, ReadTorrentEngineV1 torrentEngine, ReadVpnProfilesV1 vpnProfiles, ReadDownloadStorageSettingsV1 storageSettings, ReadStorageMountV1 storageMount,
        IAntiforgery antiforgery, UiText texts, FrontendAssets assets) : JularrPage
    {
        private const string BulkForm = "downloads-bulk";
        private const string HistoryKey = "history";
        private const string BulkRemoveDialog = "downloads-remove-selected";

        private IReadOnlyList<DownloadClientV1> _clients = [];
        private DownloadClientV1? _client;
        private PageResult<ActivityItemV1>? _queue;
        private PageResult<ActivityItemV1>? _history;
        private IReadOnlyList<AcquisitionConnectionDetailsV1> _connections = [];
        private TorrentEngineV1? _engine;
        private VpnProfileV1? _vpn;
        private DownloadStorageSettingsV1? _storage;
        private StorageMountDetailsV1? _storageMount;

        [BindProperty(SupportsGet = true)]
        public DownloadClientKind? Client { get; set; }

        [BindProperty(SupportsGet = true)]
        public long? ConnectionId { get; set; }

        [BindProperty(SupportsGet = true)]
        public MediaType? Category { get; set; }

        [BindProperty(SupportsGet = true)]
        public bool FailedOnly { get; set; }

        [BindProperty(SupportsGet = true)]
        public int PageNumber { get; set; } = 1;

        [BindProperty(SupportsGet = true, Name = HistoryKey)]
        public int HistoryPage { get; set; } = 1;

        // The download whose removal the dialog asks about.
        [BindProperty(SupportsGet = true, Name = "remove")]
        public long? RemoveId { get; set; }

        protected override void Define(Page page)
        {
            page.Title("admin.downloads.title");
            page.Navigation("nav.admin");
            page.NoPageRow();
            page.Asset("Pages/Admin/Downloads/Downloads.entry.scss");
            page.Asset("Framework/Components/LiveProgress.entry.ts");
            page.Asset("Framework/Components/Truncated.entry.ts");
            page.Asset("Framework/Components/ActionDock.entry.ts");
            page.Asset("Framework/Components/ListFilter.entry.ts");
            page.Asset("Framework/Components/ChoiceSelect.entry.ts");
            page.Asset("Framework/Components/Modal.entry.ts");
            var csrf = antiforgery.GetAndStoreTokens(HttpContext).RequestToken;

            // One row: the clients as pill tabs, the client's icon buttons at its end; then the figures, the queue, the history and the client's own panels.
            page.Content.Add(new Div(Tabs(), new Div([.. HeadButtons(csrf)]).Class("downloads-head__actions")).Class("downloads-head"));
            page.Content.Add(Figures());
            if (_queue is { } queue) page.Content.Add(QueuePanel(queue, csrf));
            if (_history is { } history) page.Content.Add(HistoryPanel(history, csrf));
            page.Content.AddRange(ClientPanels(csrf));

            if (FormError is not null) page.Content.Add(new P(new Text(FormError.ResourceKey)).Attribute("role", "alert"));
            if (RemoveId is { } removing && _queue?.Items.FirstOrDefault(item => item.Id == removing) is { } chosen) page.Content.Add(RemoveDialog(chosen, csrf));
            page.Content.Add(BulkRemove());
            page.Content.Add(new PostForm(Address(handler: "Bulk"), csrf, []).Attribute("id", BulkForm));
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

        // Warnings, pause or resume this client (on All: everything), and its settings; a button the client has no use for is left out.
        private IEnumerable<IUiComponent> HeadButtons(string? csrf)
        {
            var shown = Shown();
            var warnings = shown.Sum(client => client.WarningCount);
            if (warnings > 0)
            {
                yield return IconLink("alert", string.Format(CultureInfo.CurrentCulture, texts["downloads.warnings"], warnings), "/Admin/Activity?Filter=Errors");
            }

            var paused = shown.Sum(client => client.PausedCount);
            var open = shown.Sum(client => client.QueueCount);
            if (open > paused)
            {
                yield return IconPost("PauseClient", "pause", _client is null ? "downloads.pauseAll" : "downloads.pauseClient", csrf);
            }
            else if (paused > 0)
            {
                yield return IconPost("ResumeClient", "play", _client is null ? "downloads.resumeAll" : "downloads.resumeClient", csrf);
            }

            if (_client is { } client)
            {
                yield return IconLink("settings", texts["downloads.settings"], SettingsAddress(client));
            }
        }

        // Speed, what is left and how long, what runs and what waits, and the free space of the client's own workspace.
        private IUiComponent Figures()
        {
            var shown = Shown();
            var speed = shown.Sum(client => client.BytesPerSecond);
            var remaining = shown.Sum(client => client.RemainingBytes);
            var active = shown.Sum(client => client.ActiveCount);
            var open = shown.Sum(client => client.QueueCount);
            var cards = new List<IUiComponent>
            {
                Stat("downloads.speed", LiveFacts.Speed(speed), null, true),
                Stat("downloads.remaining", ByteSize.Format(remaining), speed > 0 && remaining > 0
                    ? string.Format(CultureInfo.CurrentCulture, texts["downloads.etaIn"], LiveFacts.Eta((int)Math.Min(int.MaxValue, remaining / speed))) : null),
                Stat("downloads.active", active.ToString(CultureInfo.CurrentCulture), string.Format(CultureInfo.CurrentCulture, texts["downloads.waiting"], Math.Max(0, open - active)))
            };
            if (_storageMount is { StorageHealthTypeId: StorageHealthType.Online, FreeBytes: { } free })
            {
                cards.Add(Stat("downloads.free", ByteSize.Format(free), _storage?.FreeSpaceReserveBytes is { } reserve
                    ? string.Format(CultureInfo.CurrentCulture, texts["downloads.reserve"], ByteSize.Format(reserve)) : null));
            }

            return new Div([.. cards]).Class(cards.Count == 4 ? "downloads-figures" : "downloads-figures downloads-figures--three");
        }

        private static IUiComponent Stat(string labelKey, string value, string? foot, bool large = false) => new Div(
            new Span(new Text(labelKey)).Class("downloads-stat__label"),
            new Strong(new ValueText(value)).Class(large ? "downloads-stat__value downloads-stat__value--large" : "downloads-stat__value"),
            foot is null ? new Span() : new Small(new ValueText(foot)).Class("downloads-stat__foot")).Class("downloads-stat");

        // The open downloads: chosen with their box for the dock, filtered by category and narrowed by name as one types.
        private IUiComponent QueuePanel(PageResult<ActivityItemV1> queue, string? csrf)
        {
            var all = _client is null;
            var columns = new List<ListColumn> { new("downloads.name", ListRole.Main, Head: ListIdentity.SelectAllHead(new Text("downloads.name"), "downloads.selectAll")) };
            if (all) columns.Add(new ListColumn("downloads.client", ListRole.Data, 2));
            columns.AddRange([new ListColumn("downloads.category", ListRole.Data, 2), new ListColumn("table.status", ListRole.Status), new ListColumn("downloads.progress"),
                new ListColumn("activity.size", ListRole.Data, 1), new ListColumn("downloads.speed"), new ListColumn("downloads.eta", ListRole.Data, 1), new ListColumn("common.actions", ListRole.Actions)]);
            var empty = new Div(new IconTile(Glyph("download", 22), UiTone.Neutral), new P(new Text("downloads.queue.empty")));
            var list = new ListPanel("downloads.queue", columns, [.. queue.Items.Select(item => QueueRow(item, all, csrf))], empty)
                .Modifier("downloads-list downloads-queue" + (all ? " downloads-list--all" : "")).Paged(primary: true);
            var head = new Div(new H2(new Text("downloads.queue")), new Span().Class("downloads-panel__spacer"), CategoryFilter(),
                new SearchBox(ClientAddress(_client), "downloads.search", null, Scope(), ".downloads-queue", Glyph("search", 16))).Class("downloads-panel__head");
            return new Section(head, list).Class("downloads-panel");
        }

        private ListRow QueueRow(ActivityItemV1 item, bool all, string? csrf)
        {
            var phase = Phase(item);
            var says = Says(item);
            var cells = new List<IUiComponent> { Identity(item, phase, ListIdentity.Select("operationIds", item.Id.ToString(CultureInfo.InvariantCulture), BulkForm, "downloads.select")) };
            if (all) cells.Add(ClientCell(item));
            cells.Add(CategoryCell(item));
            cells.Add(StatusCell(item, phase));
            cells.Add(ProgressCell(item, phase));
            cells.Add(Value(item.SizeBytes is { } size ? ByteSize.Format(size) : null));
            cells.Add(LiveValue(item, "speed", says ? "" : LiveEntities.OperationSpeed(texts, item.OperationStatusTypeId, item.DownloadStateTypeId, item.BytesPerSecond)));
            cells.Add(LiveValue(item, "eta", says ? "" : LiveFacts.Eta(item.EtaSeconds)));
            cells.Add(QueueActions(item, csrf));
            return new ListRow(cells, [ListPanel.Fact("downloads.progress", new ValueText(LiveEntities.Percent(item.ProgressPercent)))], (item.WorkTitle ?? "") + " " + item.Title);
        }

        // Pause or go on, read a waiting one again, import it by hand, take it out (after a question); move and priority are left out until a client supports them here.
        private IUiComponent QueueActions(ActivityItemV1 item, string? csrf)
        {
            var actions = new List<IUiComponent>();
            var id = item.Id.ToString(CultureInfo.InvariantCulture);
            if (item.CanPause)
            {
                actions.Add(item.DownloadStateTypeId == DownloadStateType.Paused ? RowPost("Resume", id, "play", "activity.resume", csrf) : RowPost("Pause", id, "pause", "activity.pause", csrf));
            }

            if (item.CanRetryImport) actions.Add(RowPost("RetryImport", id, "sync", "activity.retryImport", csrf));
            if (item.CanImportManually) actions.Add(IconLink("folder", texts["activity.importManually"], "/Admin/ManualImport?operationId=" + id));
            actions.Add(IconLink("trash", texts["downloads.remove"], Address(remove: item.Id), danger: true));
            return new Div([.. actions]).Class("list-actions");
        }

        // The finished downloads, newest first; failed ones can be tried again.
        private IUiComponent HistoryPanel(PageResult<ActivityItemV1> history, string? csrf)
        {
            var all = _client is null;
            var columns = new List<ListColumn> { new("downloads.name", ListRole.Main) };
            if (all) columns.Add(new ListColumn("downloads.client", ListRole.Data, 2));
            columns.AddRange([new ListColumn("table.status", ListRole.Status), new ListColumn("activity.reason", ListRole.Data, 1), new ListColumn("activity.size", ListRole.Data, 1),
                new ListColumn("downloads.finished"), new ListColumn("common.actions", ListRole.Actions)]);
            var empty = new Div(new IconTile(Glyph("history", 22), UiTone.Neutral), new P(new Text("downloads.history.empty")));
            var list = new ListPanel("downloads.history", columns, [.. history.Items.Select(item => HistoryRow(item, all, csrf))], empty)
                .Modifier("downloads-list downloads-history" + (all ? " downloads-list--all" : "")).Paged(key: HistoryKey);
            var failedOnly = new A(new Text("downloads.failedOnly")).Class(FailedOnly ? "downloads-toggle downloads-toggle--on" : "downloads-toggle").Attribute("href", Address(failedOnly: !FailedOnly, historyPage: 1));
            if (FailedOnly) failedOnly.Attribute("aria-current", "true");
            var head = new Div(new H2(new Text("downloads.history")), new Span().Class("downloads-panel__spacer"), failedOnly).Class("downloads-panel__head");
            return new Section(head, list).Class("downloads-panel");
        }

        private ListRow HistoryRow(ActivityItemV1 item, bool all, string? csrf)
        {
            var phase = Phase(item);
            var cells = new List<IUiComponent> { Identity(item, phase) };
            if (all) cells.Add(ClientCell(item));
            cells.Add(new Div(new Span(new Text("operation.status." + item.OperationStatusTypeId)).Class("downloads-pill")).Class("downloads-phase").Attribute("data-phase", phase));
            cells.Add(item.ReasonKey is { } key && LiveEntities.Reason(texts, item.OperationTypeId, key) is { Length: > 0 } reason ? new Span(new Truncated(reason)).Class("list-value list-value--fill") : Value(null));
            cells.Add(Value(item.SizeBytes is { } size ? ByteSize.Format(size) : null));
            cells.Add(new Span(new LocalTime(item.FinishedAt ?? item.CreatedAt)).Class("list-value"));

            var actions = new List<IUiComponent>();
            if (item.CanRetry) actions.Add(RowPost("Retry", item.Id.ToString(CultureInfo.InvariantCulture), "sync", "activity.retry", csrf));
            if (item.WorkId is { } workId) actions.Add(IconLink("info", texts["downloads.details"], "/Admin/Works/" + workId.ToString(CultureInfo.InvariantCulture)));
            cells.Add(new Div([.. actions]).Class("list-actions"));
            return new ListRow(cells, [ListPanel.Fact("downloads.finished", new LocalTime(item.FinishedAt ?? item.CreatedAt), Glyph("clock", 14))]);
        }

        // The work first, the release under it; the tile wears the phase's colour.
        private IUiComponent Identity(ActivityItemV1 item, string phase, IUiComponent? select = null)
        {
            IUiComponent title = item.WorkId is { } workId
                ? new A(new Truncated(item.WorkTitle ?? item.Title)).Class("list-identity__title").Attribute("href", "/Admin/Works/" + workId.ToString(CultureInfo.InvariantCulture))
                : new Truncated(item.Title);
            IUiComponent subline = item.WorkId is null ? new ValueText(item.ConnectionName ?? "") : new Truncated(item.Title);
            var tile = new Span(new IconTile(Glyph(PhaseIcon(phase)), UiTone.Neutral)).Class("downloads-phase downloads-phase--tile").Attribute("data-phase", phase);
            if (item.OperationStatusTypeId is OperationStatusType.Queued or OperationStatusType.Running) tile.Attribute("data-live", LiveKeys.Operation(item.Id));
            return new ListIdentity(tile, title, subline, select: select);
        }

        private IUiComponent ClientCell(ActivityItemV1 item) => item.DownloadClientKindId is { } kind
            ? new Span(kind is DownloadClientKind.Sabnzbd or DownloadClientKind.Qbittorrent ? new ValueText(item.ConnectionName ?? "") : new Text("downloads.client." + kind)).Class("downloads-kind")
            : Value(null);

        private IUiComponent CategoryCell(ActivityItemV1 item) => item.MediaTypeId is { } type ? new Span(new Text("media.type." + type)).Class("list-value list-value--dim") : Value(null);

        // The state pill and the bar follow the phase the stream sets; the colour is the phase's.
        private IUiComponent StatusCell(ActivityItemV1 item, string phase) => new Div(
            new Span(new Span(new ValueText(LiveEntities.OperationStatus(texts, item.IsWaiting, item.OperationStatusTypeId, Says(item) ? null : item.DownloadStateTypeId))).Attribute("data-live-text", "status")).Class("downloads-pill"))
            .Class("downloads-phase").Attribute("data-phase", phase).Attribute("data-live", LiveKeys.Operation(item.Id));

        private IUiComponent ProgressCell(ActivityItemV1 item, string phase)
        {
            var bar = new Progress().Attribute("max", "100").TextAttribute("aria-label", "live.progress").Class("downloads-bar").Attribute("data-live-value", "percent");
            if (item.ProgressPercent is { } value) bar.Attribute("value", value.ToString(CultureInfo.InvariantCulture));
            var percent = new Span(new ValueText(LiveEntities.Percent(item.ProgressPercent))).Class("downloads-percent").Attribute("data-live-text", "percent");
            return new Div(bar, percent).Class("downloads-phase downloads-progress").Attribute("data-phase", phase).Attribute("data-live", LiveKeys.Operation(item.Id));
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

        private static string PhaseIcon(string phase) => phase switch
        {
            "downloading" or "running" => "download",
            "paused" => "pause",
            "postprocessing" => "layers",
            "completed" => "check",
            "failed" => "alert",
            _ => "clock"
        };

        private static bool Says(ActivityItemV1 item) => item.ReasonKey is not null && (item.IsWaiting || item.OperationStatusTypeId == OperationStatusType.Failed);

        // The category menu of the queue: every media type, or all of them.
        private IUiComponent CategoryFilter() => new ChoiceSelect(ClientAddress(_client), "Category", "downloads.category",
            [new ChoiceOption("downloads.category.all", "", Category is null), .. Enum.GetValues<MediaType>().Select(type => new ChoiceOption("media.type." + type, ((byte)type).ToString(CultureInfo.InvariantCulture), Category == type))],
            Scope());

        // The chosen rows: pause, go on, read again, take out (asked first) and clear the choice.
        private ActionDock Dock() => new(assets, "downloads.actions", [],
        [
            BulkButton("Pause", "pause", "downloads.pauseSelected"),
            BulkButton("Resume", "play", "downloads.resumeSelected"),
            BulkButton("Retry", "sync", "downloads.retrySelected"),
            Modal.Opener(new Button(Glyph("trash", 18)).IconOnly(texts["downloads.removeSelected"]).Attribute("title", texts["downloads.removeSelected"]).Class("button button--icon button--danger"), BulkRemoveDialog),
            new Button(Glyph("close", 18)).IconOnly(texts["downloads.clearSelection"]).Attribute("title", texts["downloads.clearSelection"]).Attribute("data-dock-clear", "")
        ]);

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

        // What belongs to the chosen client only: its news servers, its connection, its tunnel and workspace, or its sources.
        private IEnumerable<IUiComponent> ClientPanels(string? csrf)
        {
            switch (_client?.DownloadClientKindId)
            {
                case DownloadClientKind.Usenet:
                    yield return NewsServers();
                    break;
                case DownloadClientKind.Sabnzbd or DownloadClientKind.Qbittorrent:
                    if (_connections.FirstOrDefault(item => item.Id == _client.SettingsConnectionId) is { } connection) yield return ClientConnection(connection, csrf);
                    break;
                case DownloadClientKind.Torrent:
                    if (_engine is { } engine) yield return TunnelPanel(engine);
                    if (_storage is { } storage) yield return WorkspacePanel(storage);
                    break;
                case DownloadClientKind.Direct:
                    yield return SourcesPanel(csrf);
                    break;
            }
        }

        private IUiComponent NewsServers()
        {
            var columns = new List<ListColumn> { new("downloads.server", ListRole.Main), new("table.status", ListRole.Status), new("downloads.connections") };
            var rows = _connections.Where(item => item.AcquisitionConnectionTypeId == AcquisitionConnectionType.Usenet).Select(item => new ListRow(
            [
                new ListIdentity(new IconTile(Glyph("server"), UiTone.Info), new ValueText(item.Name), new ValueText(item.Host + (item.Port is { } port ? ":" + port.ToString(CultureInfo.InvariantCulture) : ""))),
                ConnectionState(item),
                Value(item.MaxConnections?.ToString(CultureInfo.CurrentCulture))
            ])).ToList();
            var add = new A(new Text("downloads.addServer")).Class("button button--compact").Attribute("href", "/Admin/Acquisition/Downloads?PageNumber=1&Type=" + AcquisitionConnectionType.Usenet);
            return new Section(new Div(new H2(new Text("downloads.newsServers")), new Span().Class("downloads-panel__spacer"), add).Class("downloads-panel__head"),
                new ListPanel("downloads.newsServers", columns, rows).Modifier("downloads-list downloads-servers")).Class("downloads-panel");
        }

        // A SABnzbd or qBittorrent: its state, where it is, what it files downloads under and its priority; Test asks it now.
        private IUiComponent ClientConnection(AcquisitionConnectionDetailsV1 connection, string? csrf)
        {
            var facts = new List<(string, IUiComponent)> { ("downloads.address", new ValueText(connection.BaseUrl ?? "")) };
            if (connection.DownloadCategory is { } category) facts.Add(("downloads.categoryMapping", new ValueText(category)));
            if ((connection.LocalPath ?? connection.RemotePath) is { } folder) facts.Add(("downloads.completedFolder", new ValueText(folder)));
            facts.Add(("downloads.usedFor", new Text(connection.AcquisitionConnectionTypeId == AcquisitionConnectionType.Sabnzbd ? "downloads.usedFor.usenet" : "downloads.usedFor.torrent")));
            facts.Add(("downloads.priority", new ValueText(connection.Priority.ToString(CultureInfo.CurrentCulture))));
            var testButton = new PostForm(Address(handler: "Test"), csrf, [], new Button(Glyph("sync", 16), new Text("downloads.test")).Compact().Submit());
            return new Section(new Div(new H2(new ValueText(connection.Name)), new Span().Class("downloads-panel__spacer"), ConnectionState(connection)).Class("downloads-panel__head"),
                Facts(facts), new Div(testButton).Class("downloads-panel__foot")).Class("downloads-panel");
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
            var state = engine.HasVerifiedTunnel ? new StateChip(new Text("downloads.tunnel.connected"), UiTone.Success, Glyph("check", 14)) : new StateChip(new Text("acquisition.torrent.vpnNone"), UiTone.Warning, Glyph("alert", 14));
            var check = texts.Can("instance.settings.read", "Vpn") ? new A(new Text("downloads.checkTunnel")).Class("button button--compact").Attribute("href", "/Admin/Network") : null;
            return new Section(new Div(new H2(new Text("downloads.vpnPath")), new Span().Class("downloads-panel__spacer"), state).Class("downloads-panel__head"),
                Facts(facts), check is null ? new Span() : new Div(check).Class("downloads-panel__foot")).Class("downloads-panel");
        }

        private IUiComponent WorkspacePanel(DownloadStorageSettingsV1 storage) => new Section(
            new Div(new H2(new Text("downloads.workspace"))).Class("downloads-panel__head"),
            Facts([
                ("downloadStorage.incomplete", new ValueText(storage.IncompletePath ?? "–")),
                ("downloadStorage.complete", new ValueText(storage.CompletePath ?? "–")),
                ("downloads.parallel", new ValueText(storage.MaxParallelJobs.ToString(CultureInfo.CurrentCulture)))
            ])).Class("downloads-panel");

        // Each direct source with its state; a download that needs a person's link names its source page and takes the link here.
        private IUiComponent SourcesPanel(string? csrf)
        {
            var columns = new List<ListColumn> { new("downloads.source", ListRole.Main), new("table.status", ListRole.Status), new("common.actions", ListRole.Actions) };
            var rows = _connections.Where(item => item.AcquisitionConnectionTypeId is AcquisitionConnectionType.Gutenberg or AcquisitionConnectionType.Opds or AcquisitionConnectionType.AnnasArchive or AcquisitionConnectionType.Narou)
                .Select(item => new ListRow([new ListIdentity(new IconTile(Glyph("books"), UiTone.Accent), new ValueText(item.Name), new Text("acquisition.connection.type." + item.AcquisitionConnectionTypeId)),
                    ConnectionState(item), new Span()])).ToList();
            rows.AddRange((_queue?.Items ?? []).Where(item => item.ManualUrl is not null).Select(item => new ListRow(
            [
                new ListIdentity(new IconTile(Glyph("link"), UiTone.Danger), new Truncated(item.WorkTitle ?? item.Title), new ValueText(item.ConnectionName ?? "")),
                new StateChip(new Text("downloads.needsLink"), UiTone.Danger, Glyph("alert", 14)),
                new Div(new A(new Text("activity.openSource")).Class("button button--compact").Attribute("href", item.ManualUrl!).Attribute("target", "_blank").Attribute("rel", "noopener noreferrer"),
                    new Form<SaveActivitySourceLinkDataV1>(form =>
                    {
                        form.Text(data => data.SourceLinkUrl, "activity.fileLink");
                        form.Submit("downloads.pasteLink");
                    }, Address(handler: "SourceLink") + "&operationId=" + item.Id.ToString(CultureInfo.InvariantCulture), csrf, new SaveActivitySourceLinkDataV1(""),
                        FormError?.Field == nameof(SaveActivitySourceLinkDataV1.SourceLinkUrl) ? FormError : null)).Class("downloads-source-link")
            ])));
            var storage = new A(new Text("downloadStorage.title")).Class("button button--compact").Attribute("href", "/Admin/DownloadStorage");
            return new Section(new Div(new H2(new Text("downloads.sources")), new Span().Class("downloads-panel__spacer"), storage).Class("downloads-panel__head"),
                new ListPanel("downloads.sources", columns, rows).Modifier("downloads-list downloads-sources")).Class("downloads-panel");
        }

        // A connection's state: switched off, failing and waiting to try again, failing, not tested yet or reachable.
        private IUiComponent ConnectionState(AcquisitionConnectionDetailsV1 item)
        {
            var (tone, icon, key) = !item.IsEnabled ? (UiTone.Neutral, "power", "acquisition.connection.off")
                : item is { IsBackedOff: true, RetryAt: not null } ? (UiTone.Warning, "pause", "acquisition.connection.state.backoff")
                : item.ErrorKey is not null ? (UiTone.Danger, "alert", "acquisition.connection.state.error")
                : item.CheckedAt is null ? (UiTone.Accent, "clock", "acquisition.connection.unchecked")
                : (UiTone.Success, "check", "acquisition.connection.healthy");
            return new StateChip(new Text(key), tone, Glyph(icon, 14));
        }

        private static IUiComponent Facts(IReadOnlyList<(string LabelKey, IUiComponent Value)> facts) => new Div([.. facts.Select(fact =>
            (IUiComponent)new Div(new Span(new Text(fact.LabelKey)).Class("downloads-facts__label"), new Div(fact.Value).Class("downloads-facts__value")))]).Class("downloads-facts");

        private IUiComponent IconLink(string icon, string label, string href, bool danger = false) =>
            new A(Glyph(icon, 18)).Class(danger ? "button button--icon button--danger" : "button button--icon").Attribute("href", href).Attribute("aria-label", label).Attribute("title", label);

        private IUiComponent IconPost(string handler, string icon, string labelKey, string? csrf) =>
            new PostForm(Address(handler: handler), csrf, [], new Button(Glyph(icon, 18)).IconOnly(texts[labelKey]).Submit().Attribute("title", texts[labelKey]));

        private IUiComponent RowPost(string handler, string id, string icon, string labelKey, string? csrf) =>
            new PostForm(Address(handler: handler) + "&operationId=" + id, csrf, [], new Button(Glyph(icon, 18)).IconOnly(texts[labelKey]).Submit().Attribute("title", texts[labelKey]));

        private IUiComponent Glyph(string name, int size = 20) => new Icon(name, assets, size);

        private IReadOnlyList<DownloadClientV1> Shown() => _client is { } client ? [client] : _clients;

        // SABnzbd, qBittorrent and the torrent engine open their own card; Jularr's news servers the download clients, its direct sources the indexers that list them.
        private static string SettingsAddress(DownloadClientV1 client) => client.DownloadClientKindId switch
        {
            DownloadClientKind.Usenet => "/Admin/Acquisition/Downloads",
            DownloadClientKind.Direct => "/Admin/Acquisition",
            _ => "/Admin/Acquisition/Downloads?PageNumber=1&AcquisitionConnectionId=" + client.SettingsConnectionId.ToString(CultureInfo.InvariantCulture)
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

        // The chosen rows of the queue; none chosen does nothing.
        public async Task<IActionResult> OnPostBulkAsync(long[]? operationIds, [BindRequired] DownloadBulkActionType bulk)
        {
            if (operationIds is { Length: > 0 } && Enum.IsDefined(bulk))
            {
                await apply.ExecuteAsync<UpdateResult>(new ApplyActivityDownloadsParametersV1(), new ApplyActivityDownloadsDataV1(operationIds, bulk), HttpContext.RequestAborted);
            }

            return LocalRedirect(Address());
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
                downloadClientKindId: kind, acquisitionConnectionId: connectionId, mediaTypeId: Category), ct);
            _history = await read.ExecuteAsync<PageResult<ActivityItemV1>>(new ReadActivityParametersV1(ActivityFilterType.History, Math.Max(1, HistoryPage), downloadsOnly: true, failedOnly: FailedOnly,
                downloadClientKindId: kind, acquisitionConnectionId: connectionId), ct);

            // The chosen client's own panel.
            switch (kind)
            {
                case DownloadClientKind.Usenet or DownloadClientKind.Sabnzbd or DownloadClientKind.Qbittorrent:
                    _connections = (await connections.ExecuteAsync<PageResult<AcquisitionConnectionDetailsV1>>(new ReadAcquisitionConnectionsParametersV1(1, 100, AcquisitionConnectionRole.Download), ct)).Items;
                    if (kind == DownloadClientKind.Usenet) await LoadWorkspaceAsync(DownloadStorageArea.Usenet, ct);
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

        // The client's own workspace: its folders, limits and the free space last measured where it downloads.
        private async Task LoadWorkspaceAsync(DownloadStorageArea area, CancellationToken ct)
        {
            _storage = await storageSettings.ExecuteAsync<DownloadStorageSettingsV1>(new DownloadStorageParametersV1(area), ct);
            if (_storage is { IsConfigured: true, IncompleteStorageMountId: { } mountId } && texts.Can("storage.read"))
            {
                _storageMount = await storageMount.ExecuteAsync<StorageMountDetailsV1>(new StorageMountParametersV1(mountId), ct);
            }
        }

        // The scope of the address (client, filters, pages) that every link and form keeps.
        private IReadOnlyDictionary<string, string> Scope()
        {
            var scope = new Dictionary<string, string>();
            if (Client is { } kind) scope["Client"] = kind.ToString();
            if (ConnectionId is { } id) scope["ConnectionId"] = id.ToString(CultureInfo.InvariantCulture);
            if (FailedOnly) scope["FailedOnly"] = "true";
            return scope;
        }

        private static string ClientAddress(DownloadClientV1? client) => client is null ? "/Admin/Downloads"
            : "/Admin/Downloads?Client=" + client.DownloadClientKindId + (client.AcquisitionConnectionId is { } id ? "&ConnectionId=" + id.ToString(CultureInfo.InvariantCulture) : "");

        private string Address(string? handler = null, long? remove = null, bool? failedOnly = null, int? historyPage = null)
        {
            var query = Scope().Where(entry => entry.Key != "FailedOnly").Select(entry => entry.Key + "=" + entry.Value).ToList();
            if (Category is { } category) query.Add("Category=" + ((byte)category).ToString(CultureInfo.InvariantCulture));
            if (failedOnly ?? FailedOnly) query.Add("FailedOnly=true");
            if (PageNumber > 1) query.Add("PageNumber=" + PageNumber.ToString(CultureInfo.InvariantCulture));
            if ((historyPage ?? HistoryPage) > 1) query.Add(HistoryKey + "=" + (historyPage ?? HistoryPage).ToString(CultureInfo.InvariantCulture));
            if (handler is not null) query.Add("handler=" + handler);
            if (remove is { } id) query.Add("remove=" + id.ToString(CultureInfo.InvariantCulture));
            return "/Admin/Downloads" + (query.Count > 0 ? "?" + string.Join("&", query) : "");
        }
    }
}
