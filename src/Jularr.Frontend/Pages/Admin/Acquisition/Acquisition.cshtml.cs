using System.Globalization;
using System.Text.Encodings.Web;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Acquisition.V1;
using Jularr.Backend.Data.Admin.Storage.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Enums.Instance;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Enums.Storage;
using Jularr.Backend.Service.Admin.Acquisition.V1;
using Jularr.Backend.Service.Admin.Storage.V1;
using Jularr.Frontend.Framework;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Jularr.Frontend.Pages.Admin.Acquisition
{
    // Indexers, download clients and subtitle providers, each kind on its own page: a card per connection whose settings are inputs inside it, saved by the page's pill; adding opens a dialog.
    public sealed partial class AcquisitionModel(ReadAcquisitionConnectionsV1 connections, ReadAcquisitionConnectionV1 connection, CreateAcquisitionConnectionV1 create,
        UpdateAcquisitionConnectionV1 update, DeleteAcquisitionConnectionV1 delete, TestAcquisitionConnectionV1 test, SearchAcquisitionConnectionV1 search, ReadDownloadStorageSettingsV1 downloadStorage, FrontendAssets assets,
        IAntiforgery antiforgery, UiText texts, ReadStorageMountsV1 mounts, ReadStorageDirectoriesV1 directories, InspectDownloadClientV1 inspect, ManageDownloadClientJobV1 manage,
        BrowseDownloadClientFilesV1 browseFiles, PreviewDownloadClientFileDeleteV1 previewDelete, DeleteDownloadClientFilesV1 deleteFiles, ReadTorrentEngineV1 torrentEngine) : JularrPage
    {
        // The kinds each page adds.
        private static readonly IReadOnlyDictionary<AcquisitionConnectionRole, (AcquisitionConnectionType Type, string LabelKey)[]> Additions =
            new Dictionary<AcquisitionConnectionRole, (AcquisitionConnectionType, string)[]>
            {
                [AcquisitionConnectionRole.Search] = [(AcquisitionConnectionType.Newznab, "acquisition.connection.addIndexer"), (AcquisitionConnectionType.Nyaa, "acquisition.connection.addNyaa")],
                [AcquisitionConnectionRole.Download] = [(AcquisitionConnectionType.Sabnzbd, "acquisition.connection.addClient"), (AcquisitionConnectionType.Qbittorrent, "acquisition.connection.addClient"), (AcquisitionConnectionType.Usenet, "acquisition.connection.addUsenet"),
                    (AcquisitionConnectionType.Torrent, "acquisition.connection.addTorrent")],
                [AcquisitionConnectionRole.Subtitles] = []
            };

        private PageResult<AcquisitionConnectionDetailsV1>? _connections;
        private AcquisitionConnectionDetailsV1? _selected;
        private SaveAcquisitionConnectionDataV1? _input;
        private SearchAcquisitionConnectionDataV1? _searchInput;
        private IReadOnlyList<IndexerReleaseV1>? _releases;
        private readonly List<StorageMountDetailsV1> _storageMounts = [];
        private bool _directStorageReady = true;
        private bool _usenetStorageReady = true;
        private bool _torrentStorageReady = true;
        private TorrentEngineV1? _torrentEngine;

        // The kinds this page offers now: Jularr's own torrent engine only while its module is on.
        private IEnumerable<(AcquisitionConnectionType Type, string LabelKey)> Offered =>
            Additions[Role].Where(addition => addition.Type != AcquisitionConnectionType.Torrent || texts.Can(null, "Torrent"));
        private DownloadClientDashboardV1? _downloadDashboard;
        private DownloadClientBrowseResultV1? _fileBrowser;
        private DownloadClientDeletionPreviewV1? _deletePreview;

        [BindProperty(SupportsGet = true)]
        public long? AcquisitionConnectionId { get; set; }

        [BindProperty(SupportsGet = true)]
        public AcquisitionConnectionType? Type { get; set; }

        // Indexers live at the area's address, download clients and subtitles at their own; an opened connection keeps its own kind.
        private AcquisitionConnectionRole? _role;

        private AcquisitionConnectionRole Role
        {
            get => _role ?? (Request.Path.Value?.EndsWith("/Downloads", StringComparison.OrdinalIgnoreCase) is true ? AcquisitionConnectionRole.Download
                : Request.Path.Value?.EndsWith("/Subtitles", StringComparison.OrdinalIgnoreCase) is true ? AcquisitionConnectionRole.Subtitles
                : AcquisitionConnectionRole.Search);
            set => _role = value;
        }

        // Deleting asks in a dialog; the test search of an indexer is a dialog too.
        [BindProperty(SupportsGet = true, Name = "delete")]
        public bool ConfirmDelete { get; set; }

        [BindProperty(SupportsGet = true, Name = "search")]
        public bool ShowSearch { get; set; }

        // Defaults handed over by the source catalog.
        [BindProperty(SupportsGet = true, Name = "name")]
        public string? SuggestedName { get; set; }

        [BindProperty(SupportsGet = true, Name = "url")]
        public string? SuggestedUrl { get; set; }

        [BindProperty(SupportsGet = true)]
        public int PageNumber { get; set; } = 1;

        [BindProperty(SupportsGet = true)]
        public string? Pane { get; set; } = "downloads";

        [BindProperty(SupportsGet = true)]
        public string? Area { get; set; } = "complete";

        [BindProperty(SupportsGet = true)]
        public string? FolderPath { get; set; }

        [BindProperty(SupportsGet = true)]
        public int FilePage { get; set; } = 1;

        [BindProperty(SupportsGet = true)]
        public string? DeleteArea { get; set; }

        [BindProperty(SupportsGet = true)]
        public string? DeletePath { get; set; }

        [BindProperty(SupportsGet = true)]
        public string? ConfirmJobId { get; set; }

        protected override void Define(Page page)
        {
            page.Title("acquisition.role." + Role);
            page.Navigation("nav.admin");
            page.Asset("Framework/Components/Modal.entry.ts");
            page.Asset("Pages/Admin/Acquisition/Acquisition.entry.scss");
            page.Asset("Pages/Admin/Acquisition/DownloadConsole.entry.ts");
            page.Asset("Framework/Components/ActionDock.entry.ts");
            page.Asset("Framework/Components/Truncated.entry.ts");
            page.Asset("Framework/Components/Form.entry.ts");
            if (Role == AcquisitionConnectionRole.Download) page.Asset("Framework/Components/FolderPicker.entry.ts");
            var csrf = antiforgery.GetAndStoreTokens(HttpContext).RequestToken;
            var isSetup = SetupWizard.IsActive(Request);
            var items = _connections?.Items ?? [];

            // The page's one row of navigation, then the figures.
            page.NoPageRow();
            if (isSetup)
            {
                DefineCards(page, csrf, items);
                return;
            }

            page.Content.Add(new ListToolbar(AdminMenu.Row(texts, Request)));
            if (!_directStorageReady && items.Any(item => NativeDownloadStorageNotice.RequiresDirectStorage(item.AcquisitionConnectionTypeId)))
            {
                page.Content.Add(StorageNotice(DownloadStorageArea.Direct));
            }
            if (!_usenetStorageReady && items.Any(item => item.AcquisitionConnectionTypeId == AcquisitionConnectionType.Usenet))
            {
                page.Content.Add(StorageNotice(DownloadStorageArea.Usenet));
            }
            if (!_torrentStorageReady && items.Any(item => item.AcquisitionConnectionTypeId == AcquisitionConnectionType.Torrent))
            {
                page.Content.Add(StorageNotice(DownloadStorageArea.Torrent));
            }
            var reachable = items.Count(item => State(item) == "healthy");
            var failing = items.Count(item => State(item) is "error" or "backoff");
            var figures = new KpiTiles(
                new KpiTile("acquisition.kpi.total", Number(items.Count), Glyph("layers"), UiTone.Accent),
                new KpiTile("acquisition.kpi.reachable", Number(reachable), Glyph("check"), UiTone.Success),
                new KpiTile("acquisition.kpi.failing", Number(failing), Glyph("alert"), UiTone.Danger),
                new KpiTile("acquisition.kpi.disabled", Number(items.Count(item => !item.IsEnabled)), Glyph("power"), UiTone.Neutral)).Strip();

            // The connections as cards with their settings inside; an action on a card swaps the figures and the cards together.
            if (_connections is { } list)
            {
                IUiComponent cards = items.Count == 0
                    ? new Div(new Div(new IconTile(Glyph("search", 22), UiTone.Neutral), new P(new Text("acquisition.connection.none." + Role)).Attribute("role", "status")).Class("list-empty")).Class("list")
                    : new CardGrid([.. items.Select(item => Card(item, csrf))]);
                page.Content.Add(new Region("connections", figures, cards).Class("connections__cards").Paged(items.Count, list.PageSize));
            }

            AddDialogs(page, csrf);
            if (Role == AcquisitionConnectionRole.Download && _selected is { AcquisitionConnectionTypeId: AcquisitionConnectionType.Sabnzbd } client)
            {
                page.Content.Add(DownloadClientTabs(client.Id));
                page.Content.Add(DownloadClientPane(client.Id, csrf));
            }

            // The page's actions and the paging float at the bottom.
            var actions = new List<IUiComponent>();
            foreach (var (addition, label) in Offered)
            {
                actions.Add(new A(Glyph("plus", 16), new Span(new Text(label)).Class("action-dock__text")).Class("button").Attribute("href", Address(type: addition)));
            }

            if (Role == AcquisitionConnectionRole.Search)
            {
                actions.Add(new A(Glyph("swap", 16), new Span(new Text("acquisition.connection.importProwlarr")).Class("action-dock__text")).Class("button button--soft")
                    .Attribute("href", "/Admin/Acquisition/Takeover"));
            }
            else if (Role == AcquisitionConnectionRole.Subtitles)
            {
                actions.Add(new A(Glyph("plus", 16), new Span(new Text("acquisition.connection.addSubtitles")).Class("action-dock__text")).Class("button").Attribute("href", "/Admin/Acquisition/Sources"));
            }
            else
            {
                actions.Add(new A(Glyph("drive", 16), new Span(new Text("downloadStorage.configure")).Class("action-dock__text")).Class("button button--soft")
                    .Attribute("href", "/Admin/DownloadStorage"));
            }

            page.Content.Add(new ActionDock(assets, "acquisition.actions", actions));
        }

        // A new connection is added in a dialog; deleting and the test search are dialogs of the opened connection.
        private void AddDialogs(Page page, string? csrf)
        {
            if (_selected is null && Type is { } addedType) page.Content.Add(AddDialog(addedType, csrf));
            if (_selected is { } opened)
            {
                if (ConfirmDelete) page.Content.Add(DeleteDialog(opened, csrf));
                else if ((opened.AcquisitionConnectionTypeId is AcquisitionConnectionType.Newznab or AcquisitionConnectionType.Nyaa) && (ShowSearch || _searchInput is not null)) page.Content.Add(SearchDialog(opened, csrf));
            }

            if (FormError is not null && _searchInput is null && _input is null) page.Content.Add(new P(ErrorText.Of(FormError)).Attribute("role", "alert"));
        }

        // The setup shows the same cards as the admin page, their settings inline and saved by the pill; a new one is added in a dialog.
        private void DefineCards(Page page, string? csrf, IReadOnlyList<AcquisitionConnectionDetailsV1> items)
        {
            var cards = new List<IUiComponent>([.. items.Select(item => Card(item, csrf))]);
            if (!_directStorageReady && items.Any(item => NativeDownloadStorageNotice.RequiresDirectStorage(item.AcquisitionConnectionTypeId)))
            {
                page.Content.Add(StorageNotice(DownloadStorageArea.Direct));
            }
            if (!_usenetStorageReady && items.Any(item => item.AcquisitionConnectionTypeId == AcquisitionConnectionType.Usenet))
            {
                page.Content.Add(StorageNotice(DownloadStorageArea.Usenet));
            }
            if (!_torrentStorageReady && items.Any(item => item.AcquisitionConnectionTypeId == AcquisitionConnectionType.Torrent))
            {
                page.Content.Add(StorageNotice(DownloadStorageArea.Torrent));
            }

            // The card that adds one: each kind opens its dialog.
            var adds = Offered.Select(addition => (IUiComponent)new A(Glyph("plus", 14), new Text(addition.LabelKey)).Class("button button--compact button--soft").Attribute("href", Address(type: addition.Type))).ToList();
            adds.Add(Role == AcquisitionConnectionRole.Search
                ? new A(Glyph("swap", 14), new Text("acquisition.connection.importProwlarr")).Class("button button--compact").Attribute("href", SetupWizard.Paths[SetupStepType.Takeover])
                : new A(Glyph("drive", 14), new Text("downloadStorage.configure")).Class("button button--compact").Attribute("href", SetupWizard.Paths[SetupStepType.DownloadStorage]));
            cards.Add(new Div(new IconTile(Glyph("plus", 18), UiTone.Accent), new Div([.. adds]).Class("feature-card__group feature-card__group--wrap")).Class("feature-card feature-card--add"));
            page.Content.Add(new CardGrid([.. cards]).Class("card-grid setup-connections"));
            AddDialogs(page, csrf);
        }

        // One connection: its state in the head, the facts of its use, its settings as inputs and its direct actions (test, search, delete, the switch).
        private IUiComponent Card(AcquisitionConnectionDetailsV1 item, string? csrf)
        {
            var (icon, tone) = item.AcquisitionConnectionTypeId switch
            {
                AcquisitionConnectionType.Usenet => ("server", UiTone.Info),
                _ when Role == AcquisitionConnectionRole.Download => ("download", UiTone.Info),
                _ when Role == AcquisitionConnectionRole.Subtitles => ("subtitles", UiTone.Accent),
                _ => ("search", UiTone.Info)
            };
            var state = State(item);
            var body = new List<IUiComponent>();
            var note = state switch
            {
                "backoff" => texts["acquisition.connection.backoff"],
                "error" => texts["errors." + item.ErrorKey],
                _ => null
            };
            if (note is not null)
            {
                body.Add(new Div(new Span(new Truncated(note), state == "backoff" ? new Span(new ValueText(" "), new LocalTime(item.RetryAt!.Value)) : new Span())
                    .Class("feature-card__note")).Class("feature-card__row"));
            }
            body.Add(new Div(new Span(new Text("acquisition.connection.lastTest")).Class("feature-card__note"), Time(item.CheckedAt)).Class("feature-card__row"));
            if (item.AcquisitionConnectionTypeId == AcquisitionConnectionType.Torrent && _torrentEngine is { } engine) body.Add(TorrentFacts(engine));
            if (Role == AcquisitionConnectionRole.Search && (item.AcquisitionConnectionTypeId is AcquisitionConnectionType.Newznab or AcquisitionConnectionType.Nyaa))
            {
                body.Add(new Div(new Span(new Text("acquisition.connection.today")).Class("feature-card__note"), Usage(item)).Class("feature-card__row"));
            }

            if (item.AcquisitionConnectionTypeId == AcquisitionConnectionType.Sabnzbd)
                body.Add(new A(Glyph("download", 14), new Text("console.open")).Class("button button--compact button--soft")
                    .Attribute("href", Address(item.Id) + "&Pane=downloads"));

            // The settings are inputs of the card; the page's pill saves them. A key or password is never shown again.
            body.Add(ConnectionForm(item.AcquisitionConnectionTypeId, csrf, item));

            var start = new List<IUiComponent>
            {
                Post("Test", item.Id, csrf, [], new Button(Glyph("pulse", 14), new Text("acquisition.connection.test")).Compact().Soft().Submit())
            };
            if (item.AcquisitionConnectionTypeId is AcquisitionConnectionType.Newznab or AcquisitionConnectionType.Nyaa)
            {
                start.Add(new A(Glyph("search", 14), new Text("acquisition.connection.search")).Class("button button--compact button--soft").Attribute("href", Address(item.Id) + "&search=true"));
            }

            start.Add(new A(Glyph("trash", 14), new Text("acquisition.connection.delete")).Class("button button--compact button--warn").Attribute("href", Address(item.Id) + "&delete=true"));
            var end = new List<IUiComponent> { Post("Toggle", item.Id, csrf, [("isEnabled", item.IsEnabled ? "false" : "true")], new SwitchButton(item.IsEnabled, "acquisition.connection.active")) };
            return new FeatureCard(new IconTile(Glyph(icon, 18), tone), item.Name, texts["acquisition.connection.type." + item.AcquisitionConnectionTypeId], StateChipOf(item, state), body, start, end).Wide();
        }

        // The engine as measured in this process: the upload lock proven at this read, what went out to peers (never file data) and what came in.
        private static IUiComponent TorrentFacts(TorrentEngineV1 engine) => new FactList(
        [
            ("acquisition.torrent.lock", new Text(engine.IsLockVerified ? "acquisition.torrent.lockVerified" : "acquisition.torrent.lockFailed")),
            ("acquisition.torrent.uploaded", new Span(new Text("acquisition.torrent.fileData"), new ValueText(" " + ByteSize.Format(engine.SentPieceBytes) + " · "), new Text("acquisition.torrent.control"),
                new ValueText(" " + ByteSize.Format(engine.SentBytes) + " · " + engine.RefusedFrames.ToString("N0", CultureInfo.CurrentCulture) + " "), new Text("acquisition.torrent.refused"))),
            ("acquisition.torrent.received", new ValueText(ByteSize.Format(engine.ReceivedBytes))),
            ("acquisition.torrent.peers", new ValueText(engine.ConnectedPeers.ToString("N0", CultureInfo.CurrentCulture))),
            ("acquisition.torrent.vpn", new Text(engine.HasVerifiedTunnel ? "acquisition.torrent.vpnVerified" : "acquisition.torrent.vpnNone"))
        ]);

        private IUiComponent StorageNotice(DownloadStorageArea area) =>
            NativeDownloadStorageNotice.Message(area, SetupWizard.IsActive(Request) ? SetupWizard.Paths[SetupStepType.DownloadStorage] : "/Admin/DownloadStorage");

        // The state of a connection: switched off, failing and paused, failing, not tested yet or reachable.
        private static string State(AcquisitionConnectionDetailsV1 item) => !item.IsEnabled ? "off" : item is { IsBackedOff: true, RetryAt: not null } ? "backoff"
            : item.ErrorKey is not null ? "error" : item.CheckedAt is null ? "unchecked" : "healthy";

        // The icon and the tone say the state as well as the colour does.
        private IUiComponent StateChipOf(AcquisitionConnectionDetailsV1 item, string state)
        {
            var (tone, icon, key) = state switch
            {
                "off" => (UiTone.Neutral, "power", "acquisition.connection.off"),
                "backoff" => (UiTone.Warning, "pause", "acquisition.connection.state.backoff"),
                "error" => (UiTone.Danger, "alert", "acquisition.connection.state.error"),
                "unchecked" => (UiTone.Accent, "clock", "acquisition.connection.unchecked"),
                _ => (UiTone.Success, "check", "acquisition.connection.healthy")
            };
            return new StateChip(new Text(key), tone, Glyph(icon, 14));
        }

        // A new connection is the one editor left in a dialog; Cancel closes it.
        private IUiComponent AddDialog(AcquisitionConnectionType type, string? csrf) =>
            new Modal(assets, "connection-editor", "acquisition.connection.type." + type, [ConnectionForm(type, csrf, null)], open: true, returnTo: Address());

        // Deleting asks once more, in a dialog with Cancel.
        private IUiComponent DeleteDialog(AcquisitionConnectionDetailsV1 selected, string? csrf) =>
            new Modal(assets, "connection-delete", "acquisition.connection.delete", [new P(new Text("acquisition.connection.deleteConfirm")),
                new Form<DeleteAcquisitionConnectionParametersV1>(form =>
                {
                    form.Hidden(item => item.Version);
                    form.Cancel();
                    form.Submit("acquisition.connection.delete");
                }, Address(selected.Id, handler: "Delete"), csrf, new DeleteAcquisitionConnectionParametersV1(selected.Id, selected.Version))], open: true, returnTo: Address());

        // A free-text query shows what the indexer returns; it counts towards the daily queries.
        private IUiComponent SearchDialog(AcquisitionConnectionDetailsV1 selected, string? csrf)
        {
            var content = new List<IUiComponent>
            {
                new Form<SearchAcquisitionConnectionDataV1>(form =>
                {
                    form.Text(item => item.Query, "acquisition.connection.searchQuery");
                    form.Submit("acquisition.connection.search");
                }, Address(selected.Id, handler: "Search"), csrf, _searchInput, _searchInput is null ? null : FormError)
            };
            if (_releases is { } releases)
            {
                content.Add(releases.Count == 0 ? new P(new Text("wanted.search.empty")).Attribute("role", "status") : new Table<IndexerReleaseV1>(releases)
                    .Text(item => item.Title, "wanted.search.release")
                    .Content("wanted.search.age", item => new ValueText(ReleaseFacts.AgeDays(item.PublishedAt)))
                    .Content("wanted.search.size", item => new ValueText(ReleaseFacts.Size(item.SizeBytes))));
            }

            return new Modal(assets, "connection-search", "acquisition.connection.searchTitle", content, open: true, returnTo: Address());
        }


        public async Task<IActionResult> OnGetAsync()
        {
            await LoadDataAsync();
            PreparePage();
            return Page();
        }

        public async Task<IActionResult> OnPostAsync([BindRequired] AcquisitionConnectionType acquisitionConnectionTypeId, string? name, string? baseUrl, string? apiKey,
            bool? isEnabled, [BindRequired] int priority, string? categories, string? downloadCategory, string? remotePath, string? localPath, string? username,
            int? maxConnections, string? password, int[]? mediaTypeIds, bool? searchesAutomatically, int? queryLimit, int? grabLimit, string? host, int? port, bool? useSsl,
            long? downloadStorageMountId, bool? removesCompleted, string? incompleteRemotePath, string? incompleteLocalPath, bool? removesFailed)
        {
            // A card's inputs leave out the switch, which is its own action: the stored state stays.
            var ct = HttpContext.RequestAborted;
            var enabled = isEnabled ?? (AcquisitionConnectionId is { } stored ? (await connection.ExecuteAsync<AcquisitionConnectionDetailsV1>(new AcquisitionConnectionParametersV1(stored), ct)).IsEnabled : true);
            _input = new SaveAcquisitionConnectionDataV1(name ?? "", acquisitionConnectionTypeId, baseUrl, apiKey, enabled, priority, Categories(categories),
                downloadCategory, remotePath, localPath, username, maxConnections, password, mediaTypeIds, searchesAutomatically ?? true, queryLimit, grabLimit, host, port, useSsl,
                downloadStorageMountId, removesCompleted ?? true, incompleteRemotePath, incompleteLocalPath, removesFailed ?? true);
            if (AcquisitionConnectionId is { } existing)
            {
                await update.ExecuteAsync<UpdateResult>(new AcquisitionConnectionParametersV1(existing), _input, ct);
            }
            else
            {
                AcquisitionConnectionId = (await create.ExecuteAsync<CreateAcquisitionConnectionResultV1>(_input, ct)).Id;
            }

            return LocalRedirect(Address());
        }

        // An authorized SSR fragment: polling never reloads forms, other tabs, or the whole document.
        public async Task<IActionResult> OnGetConsoleAsync()
        {
            if (Pane is not ("downloads" or "history"))
                return BadRequest();
            var selected = await connection.ExecuteAsync<AcquisitionConnectionDetailsV1>(
                new AcquisitionConnectionParametersV1(SelectedId()), HttpContext.RequestAborted);
            if (selected.AcquisitionConnectionTypeId != AcquisitionConnectionType.Sabnzbd)
                return BadRequest();

            _downloadDashboard = await inspect.ExecuteAsync<DownloadClientDashboardV1>(
                new AcquisitionConnectionParametersV1(selected.Id), default, HttpContext.RequestAborted);
            var csrf = antiforgery.GetAndStoreTokens(HttpContext).RequestToken;
            using var writer = new StringWriter(CultureInfo.InvariantCulture);
            DownloadClientPane(selected.Id, csrf).WriteTo(writer, HtmlEncoder.Default, texts);
            Response.Headers.CacheControl = "no-store";
            return Content(writer.ToString(), "text/html");
        }

        public async Task<IActionResult> OnPostJobAsync(string jobId, string action, bool confirmed = false)
        {
            await manage.ExecuteAsync<UpdateResult>(new AcquisitionConnectionParametersV1(SelectedId()),
                new DownloadClientJobCommandV1(jobId, action, confirmed), HttpContext.RequestAborted);
            return LocalRedirect(Address(SelectedId()) + "&Pane=" + (action == "removeHistory" ? "history" : "downloads"));
        }

        public async Task<IActionResult> OnPostDeleteFileAsync(string area, string? relativePath, bool confirmed, string? fingerprint)
        {
            await deleteFiles.ExecuteAsync<UpdateResult>(new AcquisitionConnectionParametersV1(SelectedId()),
                new DownloadClientFolderRequestV1(area, relativePath, 1, confirmed, fingerprint), HttpContext.RequestAborted);
            var parent = relativePath?.LastIndexOf('/') ?? -1;
            return LocalRedirect(FileAddress(SelectedId(), area, parent < 0 ? "" : relativePath![..parent]));
        }

        // A row's switch changes only whether the connection is used; its key and password stay.
        public async Task<IActionResult> OnPostToggleAsync(bool isEnabled)
        {
            var ct = HttpContext.RequestAborted;
            var current = await connection.ExecuteAsync<AcquisitionConnectionDetailsV1>(new AcquisitionConnectionParametersV1(SelectedId()), ct);
            await update.ExecuteAsync<UpdateResult>(new AcquisitionConnectionParametersV1(current.Id), InputOf(current, isEnabled), ct);
            return LocalRedirect(Address());
        }

        public async Task<IActionResult> OnGetFoldersAsync([BindRequired] long storageMountId, string? path, string? afterName)
        {
            var result = await directories.ExecuteAsync<BaseDataList<StorageDirectoryV1>>(new StorageMountParametersV1(storageMountId),
                new ReadStorageDirectoriesDataV1(path ?? "", afterName), HttpContext.RequestAborted);
            var address = Microsoft.AspNetCore.WebUtilities.QueryHelpers.AddQueryString(Request.Path, new Dictionary<string, string?>
            {
                ["handler"] = "Folders", ["storageMountId"] = storageMountId.ToString(CultureInfo.InvariantCulture)
            });
            using var writer = new StringWriter(CultureInfo.InvariantCulture);
            FolderPicker.Directories(address, path ?? "", result.Select(item => item.Name).ToArray(), texts, afterName, result.Count == StorageDirectoryV1.PageSize)
                .WriteTo(writer, HtmlEncoder.Default, texts);
            Response.Headers.CacheControl = "no-store";
            return Content(writer.ToString(), "text/html");
        }

        public async Task<IActionResult> OnPostTestAsync()
        {
            // A successful test also ends a pause after failures.
            var result = await test.ExecuteAsync<AcquisitionConnectionTestV1>(new AcquisitionConnectionParametersV1(SelectedId()), default, HttpContext.RequestAborted);
            HintText(result.IsAvailable
                ? string.Join(" · ", new[] { texts["acquisition.connection.healthy"], result.ServerVersion is { } version ? texts["table.version"] + " " + version : null,
                    result.SearchModes.Length > 0 ? texts["acquisition.connection.searchModes"] + " " + string.Join(", ", result.SearchModes) : null }.OfType<string>())
                : texts["errors." + (result.ErrorKey ?? "acquisition_unavailable")]);
            return LocalRedirect(Address());
        }

        public async Task<IActionResult> OnPostSearchAsync(string? query)
        {
            _searchInput = new SearchAcquisitionConnectionDataV1(query ?? "");
            _releases = await search.ExecuteAsync<BaseDataList<IndexerReleaseV1>>(new AcquisitionConnectionParametersV1(SelectedId()), _searchInput, HttpContext.RequestAborted);
            await LoadDataAsync();
            PreparePage();
            return Page();
        }

        public async Task<IActionResult> OnPostDeleteAsync([BindRequired] long version)
        {
            await delete.ExecuteAsync<UpdateResult>(new DeleteAcquisitionConnectionParametersV1(SelectedId(), version), default, HttpContext.RequestAborted);
            AcquisitionConnectionId = null;
            return LocalRedirect(Address());
        }

        protected override async Task LoadDataAsync()
        {
            if (AcquisitionConnectionId is { } id)
            {
                _selected = await connection.ExecuteAsync<AcquisitionConnectionDetailsV1>(new AcquisitionConnectionParametersV1(id), HttpContext.RequestAborted);
                Role = _selected.AcquisitionConnectionRoleId;
            }

            _connections = await connections.ExecuteAsync<PageResult<AcquisitionConnectionDetailsV1>>(new ReadAcquisitionConnectionsParametersV1(PageNumber, acquisitionConnectionRoleId: Role),
                HttpContext.RequestAborted);
            if (Type is { } requested && NativeDownloadStorageNotice.RequiresDirectStorage(requested)
                || _selected is { } selected && NativeDownloadStorageNotice.RequiresDirectStorage(selected.AcquisitionConnectionTypeId)
                || _connections.Items.Any(item => NativeDownloadStorageNotice.RequiresDirectStorage(item.AcquisitionConnectionTypeId)))
            {
                var settings = await downloadStorage.ExecuteAsync<DownloadStorageSettingsV1>(
                    new DownloadStorageParametersV1(DownloadStorageArea.Direct), HttpContext.RequestAborted);
                _directStorageReady = settings.IsConfigured;
            }
            if (Role == AcquisitionConnectionRole.Download)
            {
                var settings = await downloadStorage.ExecuteAsync<DownloadStorageSettingsV1>(
                    new DownloadStorageParametersV1(DownloadStorageArea.Usenet), HttpContext.RequestAborted);
                _usenetStorageReady = settings.IsConfigured;
            }
            if (Role == AcquisitionConnectionRole.Download && texts.Can(null, "Torrent"))
            {
                _torrentStorageReady = (await downloadStorage.ExecuteAsync<DownloadStorageSettingsV1>(
                    new DownloadStorageParametersV1(DownloadStorageArea.Torrent), HttpContext.RequestAborted)).IsConfigured;
                if (_connections.Items.Any(item => item.AcquisitionConnectionTypeId == AcquisitionConnectionType.Torrent))
                    _torrentEngine = await torrentEngine.ExecuteAsync<TorrentEngineV1>(new ReadTorrentEngineParametersV1(), HttpContext.RequestAborted);
            }
            if (_selected is { AcquisitionConnectionTypeId: AcquisitionConnectionType.Sabnzbd } client && !SetupWizard.IsActive(Request))
            {
                _downloadDashboard = await inspect.ExecuteAsync<DownloadClientDashboardV1>(
                    new AcquisitionConnectionParametersV1(client.Id), default, HttpContext.RequestAborted);
                if (Pane == "files")
                {
                    try
                    {
                        _fileBrowser = await browseFiles.ExecuteAsync<DownloadClientBrowseResultV1>(new AcquisitionConnectionParametersV1(client.Id),
                            new DownloadClientFolderRequestV1(Area ?? "complete", FolderPath, FilePage), HttpContext.RequestAborted);
                    }
                    catch (DataValidationException) { _fileBrowser = null; }
                    if (DeleteArea is { } deleteArea && DeletePath is { } deletePath)
                        _deletePreview = await previewDelete.ExecuteAsync<DownloadClientDeletionPreviewV1>(new AcquisitionConnectionParametersV1(client.Id),
                            new DownloadClientFolderRequestV1(deleteArea, deletePath), HttpContext.RequestAborted);
                }
            }
            if (Role == AcquisitionConnectionRole.Download)
            {
                _storageMounts.Clear();
                for (var mountPage = 1; texts.Can("storage.read"); mountPage++)
                {
                    var configured = await mounts.ExecuteAsync<PageResult<StorageMountDetailsV1>>(new ReadStorageMountsParametersV1(mountPage, 100), HttpContext.RequestAborted);
                    _storageMounts.AddRange(configured.Items);
                    if (configured.Items.Count < configured.PageSize) break;
                }
            }
        }

        // The stored values of a connection as an input; a key or password is never read back, so it stays as it is.
        private static SaveAcquisitionConnectionDataV1 InputOf(AcquisitionConnectionDetailsV1 selected, bool? isEnabled = null) =>
            new(selected.Name, selected.AcquisitionConnectionTypeId, selected.BaseUrl, null, isEnabled ?? selected.IsEnabled, selected.Priority, selected.Categories,
                selected.DownloadCategory, selected.RemotePath, selected.LocalPath, selected.Username, selected.MaxConnections, null, selected.MediaTypeIds,
                selected.SearchesAutomatically, selected.QueryLimit, selected.GrabLimit, selected.Host, selected.Port, selected.UseSsl, selected.DownloadStorageMountId, selected.RemovesCompleted, selected.IncompleteRemotePath, selected.IncompleteLocalPath, selected.RemovesFailed);

        // The inputs of a connection: a card's own (saved by the pill) or, without one, the new connection's in its dialog.
        private Form<SaveAcquisitionConnectionDataV1> ConnectionForm(AcquisitionConnectionType type, string? csrf, AcquisitionConnectionDetailsV1? existing)
        {
            var submitted = _input is not null && AcquisitionConnectionId == existing?.Id;
            var input = submitted ? _input! : existing is not null ? InputOf(existing)
                : (_input ?? new SaveAcquisitionConnectionDataV1(SuggestedName ?? "", type, type is AcquisitionConnectionType.Usenet or AcquisitionConnectionType.Torrent ? null : SuggestedUrl ?? "", null,
                    !(NativeDownloadStorageNotice.RequiresDirectStorage(type) && !_directStorageReady || type == AcquisitionConnectionType.Usenet && !_usenetStorageReady
                        || type == AcquisitionConnectionType.Torrent && !_torrentStorageReady),
                    type is AcquisitionConnectionType.Newznab or AcquisitionConnectionType.Sabnzbd ? 25 : 50, [], null, null, null, useSsl: type == AcquisitionConnectionType.Usenet ? true : null));

            return new Form<SaveAcquisitionConnectionDataV1>(form =>
            {
                form.Hidden(item => item.AcquisitionConnectionTypeId);
                if (NativeDownloadStorageNotice.RequiresDirectStorage(type) && !_directStorageReady)
                {
                    form.Content(StorageNotice(DownloadStorageArea.Direct));
                }
                else if (type == AcquisitionConnectionType.Usenet && !_usenetStorageReady)
                {
                    form.Content(StorageNotice(DownloadStorageArea.Usenet));
                }
                else if (type == AcquisitionConnectionType.Torrent && !_torrentStorageReady)
                {
                    form.Content(StorageNotice(DownloadStorageArea.Torrent));
                }
                form.Text(item => item.Name, "acquisition.connection.name");
                // A news server is entered as in SABnzbd: server, port and SSL; every other connection by its web address.
                if (type == AcquisitionConnectionType.Usenet)
                {
                    form.Text(item => item.Host, "acquisition.connection.host");
                    form.Number(item => item.Port, "acquisition.connection.port");
                    form.Select(item => item.UseSsl, "acquisition.connection.ssl", [new SelectOption("True", texts["acquisition.connection.on"]), new SelectOption("False", texts["acquisition.connection.off"])]);
                }
                else if (type != AcquisitionConnectionType.Torrent)
                {
                    form.Text(item => item.BaseUrl, "acquisition.connection.baseUrl", help: new FieldHelp($"hint.acquisition.{type}.placeholder", $"hint.acquisition.{type}.info"));
                }
                // Catalog logins belong to the instance; keys and passwords are never shown again.
                if (type is AcquisitionConnectionType.Opds or AcquisitionConnectionType.Usenet)
                {
                    form.Text(item => item.Username, "acquisition.connection.username", "username");
                    form.Password(item => item.ApiKey, "acquisition.connection.password", "new-password");
                }
                else if (type == AcquisitionConnectionType.OpenSubtitles)
                {
                    form.Password(item => item.ApiKey, "acquisition.connection.apiKey", "new-password");
                    form.Text(item => item.Username, "acquisition.connection.username", "username");
                    form.Password(item => item.Password, "acquisition.connection.password", "new-password");
                }
                else if (type == AcquisitionConnectionType.Qbittorrent)
                {
                    form.Text(item => item.Username, "acquisition.connection.username", "username");
                    form.Password(item => item.Password, "acquisition.connection.password", "new-password");
                }
                else if (type == AcquisitionConnectionType.AnnasArchive)
                {
                    form.Password(item => item.ApiKey, "acquisition.connection.memberKey", "new-password");
                }
                else if (type is not (AcquisitionConnectionType.Gutenberg or AcquisitionConnectionType.Nyaa or AcquisitionConnectionType.Narou or AcquisitionConnectionType.Torrent))
                {
                    form.Password(item => item.ApiKey, "acquisition.connection.apiKey", "new-password");
                }

                form.Number(item => item.Priority, "acquisition.connection.priority");
                if (existing is null)
                {
                    form.Select(item => item.IsEnabled, "acquisition.connection.enabled", [new SelectOption("True", texts["acquisition.connection.on"]), new SelectOption("False", texts["acquisition.connection.off"])]);
                }

                // Empty media scope serves all types. SABnzbd connections can use separate
                // categories per type while sharing the same SAB server and path mapping.
                if (type is AcquisitionConnectionType.Newznab or AcquisitionConnectionType.Sabnzbd)
                {
                    form.Choices(item => item.MediaTypeIds, "acquisition.connection.mediaTypes", Enum.GetValues<MediaType>()
                        .Select(media => new SelectOption(((int)media).ToString(CultureInfo.InvariantCulture), texts[MediaTypeLabel.Key(media, null)])));
                }

                // Indexer categories (empty uses the media type defaults), automatic searches and daily limits.
                if (type is AcquisitionConnectionType.Newznab or AcquisitionConnectionType.Nyaa)
                {
                    if (type == AcquisitionConnectionType.Newznab)
                        form.Text(item => item.Categories, "acquisition.connection.categories");
                    form.Select(item => item.SearchesAutomatically, "acquisition.connection.searches",
                        [new SelectOption("True", texts["acquisition.connection.automatic"]), new SelectOption("False", texts["acquisition.connection.manualOnly"])]);
                    form.Number(item => item.QueryLimit, "acquisition.connection.queryLimit");
                    form.Number(item => item.GrabLimit, "acquisition.connection.grabLimit");
                }

                // News servers allow a limited number of sessions.
                if (type == AcquisitionConnectionType.Usenet)
                {
                    form.Number(item => item.MaxConnections, "acquisition.connection.maxConnections");
                }

                // Only download clients place files and report their own paths.
                if (type is AcquisitionConnectionType.Sabnzbd or AcquisitionConnectionType.Qbittorrent)
                {
                    if (type == AcquisitionConnectionType.Sabnzbd)
                    {
                        // Existing SABnzbd categories remain as compatibility metadata, not an admin input.
                        // Download routing belongs to Jularr's transport/media/priority selection.
                        if (input.DownloadCategory is not null) form.Hidden(item => item.DownloadCategory);
                        form.Toggle(item => item.RemovesCompleted, "acquisition.connection.removesCompleted");
                        form.Toggle(item => item.RemovesFailed, "acquisition.connection.removesFailed");
                    }
                    // SAB reports its real Complete path; only the Jularr-visible folder is chosen here.
                    if (existing?.RemotePath is { } detected)
                        form.Content(new P(new Strong(new Text("acquisition.connection.remotePath")), new ValueText(": " + detected)));
                    if (texts.Can("storage.read"))
                    {
                        form.Select(item => item.DownloadStorageMountId, "acquisition.connection.downloadStorage", [
                            new SelectOption("", texts["acquisition.connection.containerStorage"]),
                            .. _storageMounts.Select(mount => new SelectOption(mount.Id.ToString(CultureInfo.InvariantCulture), mount.Name))
                        ]);
                    }
                    else form.Hidden(item => item.DownloadStorageMountId);
                    form.Text(item => item.LocalPath, "acquisition.connection.localPath");
                    if (string.IsNullOrWhiteSpace(input.LocalPath) && input.DownloadStorageMountId is null
                        || existing is { RemotePath: null })
                        form.Content(new P(new Text("acquisition.connection.downloadPathWarning")).Attribute("role", "status"));
                    if (type == AcquisitionConnectionType.Sabnzbd)
                    {
                        form.Text(item => item.IncompleteRemotePath, "console.incompleteRemotePath");
                        form.Text(item => item.IncompleteLocalPath, "console.incompleteLocalPath");
                    }
                    var browse = Microsoft.AspNetCore.WebUtilities.QueryHelpers.AddQueryString(Address(existing?.Id, type, "Folders"),
                        "storageMountId", input.DownloadStorageMountId?.ToString(CultureInfo.InvariantCulture) ?? "");
                    if (texts.Can("storage.read"))
                    {
                        form.Content(new FolderPicker(assets, browse, input.LocalPath ?? "", nameof(input.LocalPath), "/Admin/Storage",
                            storageMountFieldName: nameof(input.DownloadStorageMountId), isVisible: input.DownloadStorageMountId is not null));
                        if (type == AcquisitionConnectionType.Sabnzbd)
                            form.Content(new FolderPicker(assets, browse, input.IncompleteLocalPath ?? "", nameof(input.IncompleteLocalPath), "/Admin/Storage",
                                storageMountFieldName: nameof(input.DownloadStorageMountId), isVisible: input.DownloadStorageMountId is not null));
                    }
                }

                if (existing is null)
                {
                    form.Cancel();
                    form.Submit("common.save");
                }
                else form.Save();
            }, Address(existing?.Id, type), csrf, input, _input is not null && (existing is null || submitted) ? FormError : null);
        }

        // A form of one connection's action; posted through htmx, it swaps the page content.
        private IUiComponent Post(string handler, long id, string? csrf, (string Name, string Value)[] hidden, IUiComponent button) => new PostForm(Address(id, handler: handler), csrf, hidden, button);

        private IUiComponent Glyph(string name, int size = 20) => new Icon(name, assets, size);

        private static IUiComponent Number(int value) => new ValueText(value.ToString("N0", CultureInfo.CurrentCulture));

        private static IUiComponent Dim() => new Span(new ValueText("–")).Class("list-value list-value--dim");

        private static IUiComponent Time(DateTime? value) => value is { } time
            ? new Span(new LocalTime(time)).Class("list-value")
            : Dim();

        // Today's queries and grabs of an indexer against its limits.
        private static IUiComponent Usage(AcquisitionConnectionDetailsV1 item) => item.AcquisitionConnectionTypeId != AcquisitionConnectionType.Newznab ? Dim()
            : new Span(new ValueText(Count(item.QueryCount, item.QueryLimit) + " "), new Text("acquisition.connection.queries"),
                new ValueText(" · " + Count(item.GrabCount, item.GrabLimit) + " "), new Text("acquisition.connection.grabs")).Class("list-value");

        private static string Count(int count, int? limit) =>
            count.ToString(CultureInfo.InvariantCulture) + (limit is { } value ? " / " + value.ToString(CultureInfo.InvariantCulture) : "");

        private long SelectedId() => AcquisitionConnectionId ?? throw new KeyNotFoundException("No connection was selected.");

        // Numbers only; anything else is rejected with the field.
        private static int[] Categories(string? value) => value is null ? [] : [.. value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(item => int.TryParse(item, NumberStyles.None, CultureInfo.InvariantCulture, out var category) ? category : throw new DataValidationException("Categories", "invalid_type"))];

        private string Address(long? id = null, AcquisitionConnectionType? type = null, string? handler = null, int? pageNumber = null)
        {
            var home = Role switch
            {
                AcquisitionConnectionRole.Download => SetupWizard.Address(Request, SetupStepType.Downloads, "/Admin/Acquisition/Downloads"),
                AcquisitionConnectionRole.Subtitles => "/Admin/Acquisition/Subtitles",
                _ => SetupWizard.Address(Request, SetupStepType.Indexers, "/Admin/Acquisition")
            };
            var address = home + "?PageNumber=" + (pageNumber ?? PageNumber).ToString(CultureInfo.InvariantCulture);
            if (id is { } connectionId) address += "&AcquisitionConnectionId=" + connectionId.ToString(CultureInfo.InvariantCulture);
            else if (type is { } connectionType) address += "&Type=" + connectionType + (SuggestedUrl is { } url ? "&url=" + Uri.EscapeDataString(url) : "")
                + (SuggestedName is { } name ? "&name=" + Uri.EscapeDataString(name) : "");
            return handler is null ? address : address + "&handler=" + handler;
        }
    }
}
