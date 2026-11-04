using System.Globalization;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Activity.V1;
using Jularr.Backend.Data.Admin.Browser.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Operation;
using Jularr.Backend.Service.Admin.Activity.V1;
using Jularr.Frontend.Framework;
using Jularr.Frontend.Modules.Live;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Jularr.Frontend.Pages.Admin.Queue
{
    // The queue on the list kit, as Sonarr's: the media-type bar on top, the entry's views (Queue, History, Blocked), then the open downloads in four groups, what needs the owner first (needs help, to import),
    // then what downloads and what waits in the downloader; each entry a row with what a person can do with it and a drawer of details.
    // Scans, imports and background jobs are no downloads: they and their failures show on the Jobs page and the Dashboard.
    public sealed class QueueModel(ReadActivityV1 read, ReadActivitySummaryV1 summary, RetryActivityV1 retry, DismissActivityV1 dismiss, SaveActivitySourceLinkV1 sourceLink, RemoveActivityDownloadV1 remove,
        PauseActivityDownloadV1 pause, RetryActivityImportV1 retryImport, PauseActivityQueueV1 pauseQueue, ReadTorrentFilesV1 torrentFiles, IAntiforgery antiforgery, UiText texts, FrontendAssets assets) : JularrPage
    {
        private PageResult<ActivityItemV1>? _items;
        private ActivitySummaryV1? _summary;
        private BaseDataList<TorrentFileV1>? _openedTorrentFiles;
        private bool _torrentFileLookupFailed;

        [BindProperty(SupportsGet = true)]
        public int PageNumber { get; set; } = 1;

        // Addresses of the former filters (a notice's `?Filter=Waiting`) still lead here; only the history moved to its own page.
        [BindProperty(SupportsGet = true)]
        public ActivityFilterType Filter { get; set; } = ActivityFilterType.All;

        // Media types as slugs of the type bar, repeated or separated by commas; none shows all of them.
        [BindProperty(SupportsGet = true, Name = "type")]
        public string[] Types { get; set; } = [];

        // The entry whose drawer is open: its address works without script, with script the drawer opens in place.
        [BindProperty(SupportsGet = true, Name = "operation")]
        public long? OpenOperationId { get; set; }

        [BindProperty(SupportsGet = true, Name = "files")]
        public bool ShowFiles { get; set; }

        private QueueViews? _views;

        private QueueViews Views => _views ??= new QueueViews(Types, texts, Request);

        // The queue holds what is open, which is a few dozen entries: one read takes up to the most a read allows and sets it into its groups, what needs a person first.
        private const int QueueSize = PageRequest.MaximumPageSize;

        protected override void Define(Page page)
        {
            page.Title("admin.queue.title");
            page.Navigation("nav.admin");
            page.Asset("Pages/Admin/Queue/Queue.entry.scss");
            page.Asset("Framework/Components/LiveProgress.entry.ts");
            page.Asset("Framework/Components/Truncated.entry.ts");
            page.Asset("Framework/Components/Drawer.entry.ts");
            page.Asset("Framework/Components/ActionDock.entry.ts");
            var csrf = antiforgery.GetAndStoreTokens(HttpContext).RequestToken;

            // The media-type bar, then the row of the entry's views with the files switch, then the groups (or, in the files view, the torrent downloads).
            page.NoPageRow();
            page.Content.Add(new MediaTypeBar(assets, Views.Scopes.ListedSlugs, Views.Scopes.ChosenSlugs, "library.scope.", types => Address(types: types)));
            page.Content.Add(new ListToolbar(Views.Tabs(), Modes()));

            if (_items is { } items)
            {
                IReadOnlyList<ActivityItemV1> downloads = [.. items.Items.Where(item => item.OperationTypeId == OperationType.Download)];
                if (ShowFiles)
                {
                    var empty = new Div(new IconTile(Glyph("search", 22), UiTone.Neutral), new P(new Text("activity.files.noJobs")));
                    page.Content.Add(new ListPanel("activity.list", QueueColumns, [.. downloads.Select(item => QueueRow(item, csrf))], empty).Modifier("activity-list activity-queue").Paged());
                }
                else if (downloads.Count == 0)
                {
                    page.Content.Add(new Div(new IconTile(Glyph("search", 22), UiTone.Neutral), new P(new Text("activity.empty.Queue"))).Class("list-empty"));
                }
                else
                {
                    var groups = Groups(downloads);
                    page.Content.Add(Counts(groups));
                    foreach (var group in groups.Where(entry => entry.Items.Count > 0))
                    {
                        page.Content.Add(GroupSection(group, csrf));
                    }

                    if (items.Items.Count == QueueSize) page.Content.Add(new P(new Text("activity.limit")).Class("list-notice"));
                }

                page.Content.AddRange(downloads.Select(item => DrawerOf(item, csrf)));
            }

            if (FormError is not null) page.Content.Add(new P(new Text(FormError.ResourceKey)).Attribute("role", "alert"));
            page.Content.Add(Dock(csrf));
        }

        private static readonly ListColumn[] QueueColumns = [new("activity.item", ListRole.Main), new("table.status", ListRole.Status), new("activity.source", ListRole.Data, 2), new("activity.transfer", ListRole.Data, 1), new("common.actions", ListRole.Actions)];

        // The four groups of the queue, what needs the owner first: a download that waits for a person or failed, what is downloaded and must be imported (also by hand), what downloads, what waits in the downloader.
        private enum QueueGroup { Help, Import, Running, Queued }

        private static QueueGroup GroupOf(ActivityItemV1 item)
        {
            if (item.OperationStatusTypeId == OperationStatusType.Failed) return QueueGroup.Help;

            // Waiting for a person: the files are ready to be placed by hand or the import is read again; anything else (a check at the source) needs help.
            if (item.IsWaiting) return item.ManualUrl is null && (item.CanImportManually || item.CanRetryImport) ? QueueGroup.Import : QueueGroup.Help;
            if (item.DownloadStateTypeId == DownloadStateType.PostProcessing) return QueueGroup.Import;
            if (item.DownloadStateTypeId is null or DownloadStateType.Queued or DownloadStateType.Paused) return QueueGroup.Queued;
            return QueueGroup.Running;
        }

        private static (QueueGroup Group, string Icon, UiTone Tone, List<ActivityItemV1> Items)[] Groups(IReadOnlyList<ActivityItemV1> items) =>
        [
            (QueueGroup.Help, "alert", UiTone.Danger, [.. items.Where(item => GroupOf(item) == QueueGroup.Help)]),
            (QueueGroup.Import, "sync", UiTone.Warning, [.. items.Where(item => GroupOf(item) == QueueGroup.Import)]),
            (QueueGroup.Running, "download", UiTone.Info, [.. items.Where(item => GroupOf(item) == QueueGroup.Running)]),
            (QueueGroup.Queued, "clock", UiTone.Neutral, [.. items.Where(item => GroupOf(item) == QueueGroup.Queued)])
        ];

        // The figures of the groups.
        private IUiComponent Counts((QueueGroup Group, string Icon, UiTone Tone, List<ActivityItemV1> Items)[] groups) => new KpiTiles([.. groups.Select(group =>
            new KpiTile("activity.group." + group.Group, new ValueText(group.Items.Count.ToString(CultureInfo.CurrentCulture)), Glyph(group.Icon), group.Tone))]).Strip();

        // One group: its heading with the count (the running one with the live speed) and its entries.
        private IUiComponent GroupSection((QueueGroup Group, string Icon, UiTone Tone, List<ActivityItemV1> Items) group, string? csrf)
        {
            IUiComponent count = new Span(new ValueText(group.Items.Count.ToString(CultureInfo.CurrentCulture))).Class("activity-group__count");
            IUiComponent action = group.Group == QueueGroup.Running && _summary is { } numbers ? new Span(count, Live("speed", LiveFacts.Speed(numbers.BytesPerSecond))).Class("activity-group__facts") : count;
            return new Div(new UiSection("activity.group." + group.Group, [new ListPanel("activity.group." + group.Group, QueueColumns, [.. group.Items.Select(item => QueueRow(item, csrf))]).Modifier("activity-list activity-queue")], Glyph(group.Icon, 18), action))
                .Class("activity-group").Attribute("id", "activity-group-" + group.Group);
        }

        // The stream keeps the pace current.
        private static IUiComponent Live(string name, string text) => new Span(new ValueText(text)).Attribute("data-live", LiveKeys.Queue).Attribute("data-live-text", name);

        // The queue's own switch: the queue or the torrent files of its downloads.
        private ViewSwitch Modes() => new("activity.views",
        [
            new ViewChoice("activity.view.queue", Address(), Glyph("download", 16), !ShowFiles),
            new ViewChoice("activity.files", Address(files: true), Glyph("folder", 16), ShowFiles)
        ]);

        // The whole queue's pause and resume float at the bottom with the paging, as on every admin list.
        private ActionDock Dock(string? csrf)
        {
            var actions = new List<IUiComponent>();
            if (!ShowFiles && _items is { } open && open.Items.Any(item => item.OperationTypeId == OperationType.Download))
            {
                actions.Add(QueueButton("PauseAll", "pause", "activity.pauseAll", csrf));
                actions.Add(QueueButton("ResumeAll", "play", "activity.resumeAll", csrf));
            }

            return new ActionDock(assets, "activity.actions", actions);
        }

        private IUiComponent QueueButton(string handler, string icon, string labelKey, string? csrf) =>
            new PostForm(Address(handler: handler), csrf, [], new Button(Glyph(icon, 16), new Span(new Text(labelKey)).Class("action-dock__text")).Submit());

        // The work first, then the release under it.
        private IUiComponent Identity(ActivityItemV1 item) => item.WorkId is { } workId
            ? new ListIdentity(new IconTile(Glyph("download"), UiTone.Info), new A(new Truncated(item.WorkTitle ?? "")).Class("list-identity__title").Attribute("href", "/Admin/Works/" + workId.ToString(CultureInfo.InvariantCulture)), new Truncated(item.Title))
            : new ListIdentity(new IconTile(Glyph("download"), UiTone.Info), new Truncated(item.Title), new Text("operation.type.Download"));

        // The client and the indexer of a download.
        private static IUiComponent Source(ActivityItemV1 item) => new Span(new Truncated(LiveFacts.Join(item.ConnectionName, item.IndexerName))).Class("list-value list-value--fill");

        // The pace above, the size and time left below.
        private IUiComponent Transfer(ActivityItemV1 item)
        {
            var says = OperationState.Says(item);
            return new Div(
                OperationState.LivePart("speed", says ? "" : LiveEntities.OperationSpeed(texts, item.OperationStatusTypeId, item.DownloadStateTypeId, item.BytesPerSecond), "activity-transfer__speed"),
                OperationState.LivePart("detail", says ? "" : LiveEntities.OperationDetail(item.ProgressPercent, item.SizeBytes, item.RemainingBytes, item.EtaSeconds), "activity-transfer__detail"))
                .Class("activity-transfer").Attribute("data-live", LiveKeys.Operation(item.Id));
        }

        private ListRow QueueRow(ActivityItemV1 item, string? csrf)
        {
            // A download without a known client or indexer has no source line on the phone card.
            var source = LiveFacts.Join(item.ConnectionName, item.IndexerName);
            var row = new ListRow([Identity(item), OperationState.Status(item, texts, assets), Source(item), Transfer(item), RowActions(item, csrf)], string.IsNullOrEmpty(source) ? [] : [ListPanel.Fact("activity.source", new ValueText(source))]);
            if (item.OperationStatusTypeId == OperationStatusType.Failed) row.Tinted(UiTone.Danger);
            else if (item.IsWaiting) row.Tinted(UiTone.Warning);
            return row;
        }

        // The usual one click: read again or go on; everything else is in the drawer.
        private IUiComponent RowActions(ActivityItemV1 item, string? csrf)
        {
            var actions = new List<IUiComponent>();
            if (item.OperationStatusTypeId == OperationStatusType.Failed)
            {
                actions.Add(IconAction(item, "Retry", "sync", "activity.retry", csrf));
            }
            else
            {
                if (item.CanRetryImport) actions.Add(IconAction(item, "RetryImport", "sync", "activity.retryImport", csrf));
                if (item.CanImportManually) actions.Add(IconLink("folder", "activity.importManually", "/Admin/ManualImport?operationId=" + item.Id.ToString(CultureInfo.InvariantCulture)));
                if (item.ManualUrl is not null) actions.Add(Drawer.Opener(new A(Glyph("link", 18)).Class("button button--icon").Attribute("href", Address(operation: item.Id)).TextAttribute("aria-label", "activity.source.check").TextAttribute("title", "activity.source.check"), DrawerId(item)));
                if (item.CanPause) actions.Add(item.DownloadStateTypeId == DownloadStateType.Paused ? IconAction(item, "Resume", "play", "activity.resume", csrf) : IconAction(item, "Pause", "pause", "activity.pause", csrf));
                if (item.CanBrowseFiles) actions.Add(IconLink("folder", "activity.files.view", Address(operation: item.Id) + "&files=true"));
            }

            actions.Add(Drawer.Opener(new A(Glyph("dots", 18)).Class("button button--icon").Attribute("href", Address(operation: item.Id)).TextAttribute("aria-label", "activity.details").TextAttribute("title", "activity.details"), DrawerId(item)));
            return new Div([.. actions]).Class("list-actions");
        }

        private static string DrawerId(ActivityItemV1 item) => "activity-" + item.Id.ToString(CultureInfo.InvariantCulture);

        private IUiComponent IconLink(string icon, string labelKey, string href) =>
            new A(Glyph(icon, 18)).Class("button button--icon").Attribute("href", href).TextAttribute("aria-label", labelKey).TextAttribute("title", labelKey);

        private IUiComponent IconAction(ActivityItemV1 item, string handler, string icon, string labelKey, string? csrf) =>
            new PostForm(Address(handler: handler) + "&operationId=" + item.Id.ToString(CultureInfo.InvariantCulture), csrf, [], new Button(Glyph(icon, 18)).IconOnly(texts[labelKey]).Submit().Attribute("title", texts[labelKey]));

        private IUiComponent Glyph(string name, int size = 20) => new Icon(name, assets, size);

        private string FileReason(string? reason) => reason is not null && texts.Has("acquisition.file." + reason) ? texts["acquisition.file." + reason] : texts["acquisition.file.unknown"];

        // One entry from the right: how far it is, what it is, and everything a person can do with it; the drawer of the entry its address names stands open.
        private IUiComponent DrawerOf(ActivityItemV1 item, string? csrf)
        {
            var says = OperationState.Says(item);
            var phase = OperationState.Phase(item);
            var state = new Div(new StateChip(OperationState.LivePart("status", LiveEntities.OperationStatus(texts, item.IsWaiting, item.OperationStatusTypeId, says ? null : item.DownloadStateTypeId), "state-chip__live"), UiTone.Neutral, Glyph(OperationState.PhaseIcon(phase), 14)),
                OperationState.LiveBar(says ? null : item.ProgressPercent),
                OperationState.LivePart("speed", says ? "" : LiveEntities.OperationSpeed(texts, item.OperationStatusTypeId, item.DownloadStateTypeId, item.BytesPerSecond), "live-progress__facts"),
                OperationState.LivePart("detail", says ? "" : LiveEntities.OperationDetail(item.ProgressPercent, item.SizeBytes, item.RemainingBytes, item.EtaSeconds), "live-progress__facts"),
                OperationState.LivePart("reason", says ? LiveEntities.Reason(texts, item.OperationTypeId, item.ReasonKey) : "", "live-progress__facts"))
                .Class("live-progress list-phase activity-status activity-status--drawer").Attribute("data-live", LiveKeys.Operation(item.Id)).Attribute("data-phase", phase);

            var facts = new List<(string, IUiComponent)>();
            // The drawer has room: the release wraps whole instead of carrying its text twice for a tip.
            if (item.WorkId is not null) facts.Add(("activity.release", new ValueText(item.Title)));
            if (item.ConnectionName is { } client) facts.Add(("activity.client", new ValueText(client)));
            if (item.IndexerName is { } indexer) facts.Add(("activity.indexer", new ValueText(indexer)));
            if (item.SizeBytes is { } size) facts.Add(("activity.size", new ValueText(ByteSize.Format(size))));
            facts.Add(("operation.created", new LocalTime(item.CreatedAt)));

            var body = new List<IUiComponent> { Drawer.State("table.status", state), Drawer.Facts([.. facts]) };
            if (item.UnmatchedFiles is { Length: > 0 } unmatched)
            {
                // Each file with why it was not placed.
                body.Add(Drawer.Block("activity.unmatched", new Div([.. unmatched.Select((path, index) => new Div(new Truncated(path[(path.LastIndexOf('/') + 1)..]),
                    new Span(new ValueText(FileReason(item.UnmatchedFileReasons?.ElementAtOrDefault(index)))).Class("list-value list-value--dim")).Class("activity-file-row activity-file-row--reason"))])));
            }

            if (item.OperationStatusTypeId != OperationStatusType.Failed)
            {
                if (item.ManualUrl is { } manual) body.Add(Drawer.Block("activity.source.check", ManualLink(item, manual, csrf)));
                body.Add(new Div(Confirmed(item, "Remove", "activity.remove", "activity.removeConfirm", csrf), Confirmed(item, "RemoveAndBlock", "activity.removeAndBlock", "activity.removeAndBlockConfirm", csrf)).Class("activity-confirms"));
            }

            if (item.CanBrowseFiles)
            {
                if (item.SabnzbdConnectionId is { } sabnzbdConnectionId)
                {
                    body.Add(Drawer.Block("activity.files",
                        new A(Glyph("folder", 16), new Text("console.files")).Class("button button--soft")
                            .Attribute("href", "/Admin/Downloads/Clients?PageNumber=1&AcquisitionConnectionId="
                                + sabnzbdConnectionId.ToString(CultureInfo.InvariantCulture) + "&Pane=files")));
                }
                else if (OpenOperationId == item.Id && ShowFiles)
                {
                    IUiComponent files = _torrentFileLookupFailed
                        ? new P(new Text("activity.files.unavailable"))
                        : _openedTorrentFiles is { Count: > 0 } available
                            ? new Div([.. available.Select(file =>
                                new Div(new Truncated(file.Name),
                                    new Span(new ValueText(ByteSize.Format(file.SizeBytes) + " · "
                                        + file.ProgressPercent.ToString(CultureInfo.InvariantCulture) + "%"))
                                        .Class("list-value")).Class("activity-file-row"))])
                            : new P(new Text("activity.files.empty"));
                    body.Add(Drawer.Block("activity.files", files));
                }
                else
                {
                    body.Add(new A(new Text("activity.files.view")).Class("button button--soft")
                        .Attribute("href", Address(operation: item.Id) + "&files=true"));
                }
            }

            return new Drawer(assets, DrawerId(item), [new DrawerPane("details", "activity.details", "activity.details", "info", body, [.. PanelActions(item, csrf)], null, item.WorkTitle ?? item.Title)],
                sheet: true, closeHref: Address(), open: OpenOperationId == item.Id);
        }

        // A failure is retried or put aside; a waiting download is read again, assigned by hand or taken out; a running one is told to wait or taken out.
        private IEnumerable<IUiComponent> PanelActions(ActivityItemV1 item, string? csrf)
        {
            if (item.OperationStatusTypeId == OperationStatusType.Failed)
            {
                yield return Action(item, "Retry", "activity.retry", csrf, primary: true);
                yield return Action(item, "Dismiss", "activity.dismiss", csrf);
                yield break;
            }

            if (item.CanImportManually) yield return new A(new Text("activity.importManually")).Class("button").Attribute("href", "/Admin/ManualImport?operationId=" + item.Id.ToString(CultureInfo.InvariantCulture));
            if (item.CanRetryImport) yield return Action(item, "RetryImport", "activity.retryImport", csrf);
            if (item.CanPause) yield return item.DownloadStateTypeId == DownloadStateType.Paused ? Action(item, "Resume", "activity.resume", csrf, primary: true) : Action(item, "Pause", "activity.pause", csrf, primary: true);
        }

        private IUiComponent Action(ActivityItemV1 item, string handler, string labelKey, string? csrf, bool primary = false)
        {
            var button = new Button(new Text(labelKey)).Submit();
            return new PostForm(Address(handler: handler) + "&operationId=" + item.Id.ToString(CultureInfo.InvariantCulture), csrf, [], primary ? button.Primary() : button);
        }

        // Taking a job out deletes its partial files, so the button first asks: it opens the one that does it.
        private IUiComponent Confirmed(ActivityItemV1 item, string handler, string labelKey, string confirmKey, string? csrf) =>
            new Details(new Summary(new Text(labelKey)), Action(item, handler, confirmKey, csrf)).Class("activity-confirm");

        // The shared browser resumes automatic links; pasting a file URL remains an optional fallback.
        private IUiComponent ManualLink(ActivityItemV1 item, string manual, string? csrf) => new Div(
            new Form<BrowserParametersV1>(form => form.Submit("admin.browser.manualOpen"),
                "/Admin/Browser?handler=OpenDownloadView&operationId=" + item.Id.ToString(CultureInfo.InvariantCulture), csrf, new BrowserParametersV1()),
            new A(new Text("activity.openSource")).Class("button").Attribute("href", manual).Attribute("target", "_blank").Attribute("rel", "noopener noreferrer"),
            new Form<SaveActivitySourceLinkDataV1>(form =>
            {
                form.Text(data => data.SourceLinkUrl, "activity.fileLink");
                form.Submit("activity.continueDownload");
            }, Address(handler: "SourceLink") + "&operationId=" + item.Id.ToString(CultureInfo.InvariantCulture), csrf, new SaveActivitySourceLinkDataV1(""),
                FormError?.Field == nameof(SaveActivitySourceLinkDataV1.SourceLinkUrl) ? FormError : null)).Class("activity-source-check");

        public async Task<IActionResult> OnGetAsync()
        {
            // The finished downloads are the History view now.
            if (Filter == ActivityFilterType.History) return LocalRedirect(Views.Address("/Admin/Queue/History"));
            await LoadDataAsync();
            PreparePage();
            return Page();
        }

        public async Task<IActionResult> OnPostRetryAsync([BindRequired] long operationId)
        {
            await retry.ExecuteAsync<UpdateResult>(new ActivityOperationParametersV1(operationId), default, HttpContext.RequestAborted);
            return LocalRedirect(Address());
        }

        public async Task<IActionResult> OnPostRemoveAsync([BindRequired] long operationId) => await RemoveAsync(operationId, false);

        public async Task<IActionResult> OnPostRemoveAndBlockAsync([BindRequired] long operationId) => await RemoveAsync(operationId, true);

        private async Task<IActionResult> RemoveAsync(long operationId, bool blocks)
        {
            await remove.ExecuteAsync<UpdateResult>(new ActivityOperationParametersV1(operationId), new RemoveActivityDownloadDataV1(blocks), HttpContext.RequestAborted);
            return LocalRedirect(Address());
        }

        public async Task<IActionResult> OnPostPauseAsync([BindRequired] long operationId) => await PauseAsync(operationId, true);

        public async Task<IActionResult> OnPostResumeAsync([BindRequired] long operationId) => await PauseAsync(operationId, false);

        private async Task<IActionResult> PauseAsync(long operationId, bool isPaused)
        {
            await pause.ExecuteAsync<UpdateResult>(new ActivityOperationParametersV1(operationId), new PauseActivityDownloadDataV1(isPaused), HttpContext.RequestAborted);
            return LocalRedirect(Address());
        }

        public async Task<IActionResult> OnPostPauseAllAsync() => await PauseQueueAsync(true);

        public async Task<IActionResult> OnPostResumeAllAsync() => await PauseQueueAsync(false);

        private async Task<IActionResult> PauseQueueAsync(bool isPaused)
        {
            await pauseQueue.ExecuteAsync<UpdateResult>(new PauseActivityQueueParametersV1(), new PauseActivityDownloadDataV1(isPaused), HttpContext.RequestAborted);
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

        public async Task<IActionResult> OnPostDismissAsync([BindRequired] long operationId)
        {
            await dismiss.ExecuteAsync<UpdateResult>(new ActivityOperationParametersV1(operationId), default, HttpContext.RequestAborted);
            return LocalRedirect(Address());
        }

        protected override async Task LoadDataAsync()
        {
            var (types, subtypes) = Views.Filter;
            _items = await read.ExecuteAsync<PageResult<ActivityItemV1>>(ShowFiles
                ? new ReadActivityParametersV1(page: Math.Max(1, PageNumber), torrentOnly: true, mediaTypeIds: types, mediaSubtypeIds: subtypes)
                : new ReadActivityParametersV1(pageSize: QueueSize, mediaTypeIds: types, mediaSubtypeIds: subtypes), HttpContext.RequestAborted);
            _summary = await summary.ExecuteAsync<ActivitySummaryV1>(new ReadActivitySummaryParametersV1(), HttpContext.RequestAborted);
            if (ShowFiles && OpenOperationId is { } id
                && _items.Items.Any(item => item.Id == id && item.CanBrowseFiles && item.SabnzbdConnectionId is null))
            {
                try
                {
                    _openedTorrentFiles = await torrentFiles.ExecuteAsync<BaseDataList<TorrentFileV1>>(
                        new ActivityOperationParametersV1(id), HttpContext.RequestAborted);
                }
                catch (ServiceUnavailableException)
                {
                    _torrentFileLookupFailed = true;
                }
            }
        }

        // The page's own address with what was chosen (the media types, the paging and view of the files); defaults stay out of it.
        private string Address(int? pageNumber = null, IReadOnlyList<string>? types = null, string? handler = null, long? operation = null, bool? files = null)
        {
            var more = new List<string>();
            var number = pageNumber ?? (types is null && files is null ? Math.Max(1, PageNumber) : 1);
            if (number > 1) more.Add("PageNumber=" + number.ToString(CultureInfo.InvariantCulture));
            if (files ?? (types is null && ShowFiles)) more.Add("files=true");
            if (handler is not null) more.Add("handler=" + handler);
            if (operation is { } open) more.Add("operation=" + open.ToString(CultureInfo.InvariantCulture));
            return Views.Address("/Admin/Queue", types, [.. more]);
        }
    }
}
