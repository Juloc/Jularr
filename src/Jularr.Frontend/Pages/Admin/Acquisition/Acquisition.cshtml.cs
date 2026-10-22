using System.Globalization;
using System.Text.Encodings.Web;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Acquisition.V1;
using Jularr.Backend.Data.Admin.Storage.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Enums.Instance;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Service.Admin.Acquisition.V1;
using Jularr.Backend.Service.Admin.Storage.V1;
using Jularr.Frontend.Framework;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Jularr.Frontend.Pages.Admin.Acquisition
{
    // Indexers, download clients and subtitle providers, each kind on its own page: a list of the shared list kit, the editor in a dialog.
    public sealed class AcquisitionModel(ReadAcquisitionConnectionsV1 connections, ReadAcquisitionConnectionV1 connection, CreateAcquisitionConnectionV1 create,
        UpdateAcquisitionConnectionV1 update, DeleteAcquisitionConnectionV1 delete, TestAcquisitionConnectionV1 test, SearchAcquisitionConnectionV1 search, FrontendAssets assets,
        IAntiforgery antiforgery, UiText texts, ReadStorageMountsV1 mounts, ReadStorageDirectoriesV1 directories) : JularrPage
    {
        // The kinds each page adds.
        private static readonly IReadOnlyDictionary<AcquisitionConnectionRole, (AcquisitionConnectionType Type, string LabelKey)[]> Additions =
            new Dictionary<AcquisitionConnectionRole, (AcquisitionConnectionType, string)[]>
            {
                [AcquisitionConnectionRole.Search] = [(AcquisitionConnectionType.Newznab, "acquisition.connection.addIndexer")],
                [AcquisitionConnectionRole.Download] = [(AcquisitionConnectionType.Sabnzbd, "acquisition.connection.addClient"), (AcquisitionConnectionType.Usenet, "acquisition.connection.addUsenet")],
                [AcquisitionConnectionRole.Subtitles] = []
            };

        private PageResult<AcquisitionConnectionDetailsV1>? _connections;
        private AcquisitionConnectionDetailsV1? _selected;
        private SaveAcquisitionConnectionDataV1? _input;
        private SearchAcquisitionConnectionDataV1? _searchInput;
        private IReadOnlyList<IndexerReleaseV1>? _releases;
        private readonly List<StorageMountDetailsV1> _storageMounts = [];

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

        // Deleting asks once more.
        [BindProperty(SupportsGet = true, Name = "delete")]
        public bool ConfirmDelete { get; set; }

        // Defaults handed over by the source catalog.
        [BindProperty(SupportsGet = true, Name = "name")]
        public string? SuggestedName { get; set; }

        [BindProperty(SupportsGet = true, Name = "url")]
        public string? SuggestedUrl { get; set; }

        [BindProperty(SupportsGet = true)]
        public int PageNumber { get; set; } = 1;

        protected override void Define(Page page)
        {
            page.Title("acquisition.role." + Role);
            page.Navigation("nav.admin");
            page.Asset("Pages/Admin/Acquisition/Acquisition.entry.scss");
            page.Asset("Framework/Components/Modal.entry.ts");
            page.Asset("Framework/Components/ActionDock.entry.ts");
            page.Asset("Framework/Components/Truncated.entry.ts");
            page.Asset("Framework/Components/Form.entry.ts");
            if (Role == AcquisitionConnectionRole.Download) page.Asset("Framework/Components/FolderPicker.entry.ts");
            var csrf = antiforgery.GetAndStoreTokens(HttpContext).RequestToken;
            var isSetup = SetupWizard.IsActive(Request);
            if (isSetup) page.Content.Add(SetupWizard.Frame(Role == AcquisitionConnectionRole.Download ? SetupStepType.Downloads : SetupStepType.Indexers));
            var type = _selected?.AcquisitionConnectionTypeId ?? Type;
            var items = _connections?.Items ?? [];

            // The page's one row of navigation, then the figures.
            page.NoPageRow();
            if (!isSetup) page.Content.Add(new ListToolbar(AdminMenu.Row(texts, Request)));
            var reachable = items.Count(item => State(item) == "healthy");
            var failing = items.Count(item => State(item) is "error" or "backoff");
            page.Content.Add(new KpiTiles(
                new KpiTile("acquisition.kpi.total", Number(items.Count), Glyph("layers"), UiTone.Accent),
                new KpiTile("acquisition.kpi.reachable", Number(reachable), Glyph("check"), UiTone.Success),
                new KpiTile("acquisition.kpi.failing", Number(failing), Glyph("alert"), UiTone.Danger),
                new KpiTile("acquisition.kpi.disabled", Number(items.Count(item => !item.IsEnabled)), Glyph("power"), UiTone.Neutral)).Strip());

            // The connections; an action on a row swaps the page content, so the figures follow.
            if (_connections is { } list)
            {
                var empty = new Div(new IconTile(Glyph("search", 22), UiTone.Neutral), new P(new Text("acquisition.connection.none." + Role)).Attribute("role", "status"));
                page.Content.Add(new ListPanel("acquisition.role." + Role, Columns(), [.. items.Select(item => Row(item, csrf))], empty).Modifier("connections-list connections-list--" + Role.ToString().ToLowerInvariant()));
            }

            if (type is { } selectedType) page.Content.Add(Editor(selectedType, csrf));
            if (FormError is not null && _searchInput is null) page.Content.Add(new P(ErrorText.Of(FormError)).Attribute("role", "alert"));

            // The page's actions and the paging float at the bottom.
            var actions = new List<IUiComponent>();
            foreach (var (addition, label) in Additions[Role])
            {
                actions.Add(new A(Glyph("plus", 16), new Span(new Text(label)).Class("action-dock__text")).Class("button").Attribute("href", Address(type: addition)));
            }

            if (Role == AcquisitionConnectionRole.Search)
            {
                actions.Add(new A(Glyph("swap", 16), new Span(new Text("acquisition.connection.importProwlarr")).Class("action-dock__text")).Class("button button--soft")
                    .Attribute("href", isSetup ? SetupWizard.Paths[SetupStepType.Media] : "/Admin/Acquisition/Takeover"));
            }
            else if (Role == AcquisitionConnectionRole.Subtitles)
            {
                actions.Add(new A(Glyph("plus", 16), new Span(new Text("acquisition.connection.addSubtitles")).Class("action-dock__text")).Class("button").Attribute("href", "/Admin/Acquisition/Sources"));
            }
            else
            {
                actions.Add(new A(Glyph("drive", 16), new Span(new Text("downloadStorage.configure")).Class("action-dock__text")).Class("button button--soft")
                    .Attribute("href", isSetup ? "/Setup/DownloadStorage" : "/Admin/DownloadStorage"));
            }

            DockPaging? paging = null;
            if (_connections is { } paged && (PageNumber > 1 || paged.Items.Count == paged.PageSize))
            {
                paging = new DockPaging(PageNumber > 1 ? Address(pageNumber: PageNumber - 1) : null, paged.Items.Count == paged.PageSize ? Address(pageNumber: PageNumber + 1) : null,
                    new ValueText(string.Format(CultureInfo.CurrentCulture, texts["dock.page.single"], PageNumber)));
            }

            page.Content.Add(new ActionDock(assets, "acquisition.actions", actions, paging: paging));
        }

        // The columns each kind shows: what decides its use.
        private List<ListColumn> Columns()
        {
            var columns = new List<ListColumn> { new("acquisition.connection.name", ListRole.Main), new("table.status", ListRole.Status), new("acquisition.connection.lastTest", ListRole.Data, 1) };
            if (Role == AcquisitionConnectionRole.Search) columns.Add(new("acquisition.connection.today", ListRole.Data, 2));
            if (Role == AcquisitionConnectionRole.Download) columns.Add(new("acquisition.connection.category", ListRole.Data, 2));
            if (Role != AcquisitionConnectionRole.Subtitles) columns.Add(new("acquisition.connection.priority", ListRole.Data, 1));
            columns.Add(new("acquisition.connection.active"));
            columns.Add(new("common.actions", ListRole.Actions));
            return columns;
        }

        private ListRow Row(AcquisitionConnectionDetailsV1 item, string? csrf)
        {
            var (icon, tone) = item.AcquisitionConnectionTypeId switch
            {
                AcquisitionConnectionType.Usenet => ("server", UiTone.Info),
                _ when Role == AcquisitionConnectionRole.Download => ("download", UiTone.Info),
                _ when Role == AcquisitionConnectionRole.Subtitles => ("subtitles", UiTone.Accent),
                _ => ("search", UiTone.Info)
            };
            var state = State(item);
            var note = state switch
            {
                "backoff" => new Truncated(texts["acquisition.connection.backoff"] + " " + item.RetryAt!.Value.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)),
                "error" => new Truncated(texts["errors." + item.ErrorKey]),
                _ => null
            };
            var identity = new ListIdentity(new IconTile(Glyph(icon, 18), tone), new A(new ValueText(item.Name)).Class("list-identity__title").Attribute("href", Address(item.Id)),
                new Truncated(texts["acquisition.connection.type." + item.AcquisitionConnectionTypeId] + Endpoint(item)), note);

            var cells = new List<IUiComponent> { identity, StateChipOf(item, state), Time(item.CheckedAt) };
            if (Role == AcquisitionConnectionRole.Search) cells.Add(Usage(item));
            if (Role == AcquisitionConnectionRole.Download) cells.Add(item.DownloadCategory is { } category ? new Span(new ValueText(category)).Class("list-value") : Dim());
            if (Role != AcquisitionConnectionRole.Subtitles) cells.Add(new Span(new ValueText(item.Priority.ToString(CultureInfo.InvariantCulture))).Class("list-value"));
            cells.Add(Post("Toggle", item.Id, csrf, [("isEnabled", item.IsEnabled ? "false" : "true")], new SwitchButton(item.IsEnabled, "acquisition.connection.active")));
            cells.Add(new Div(
                Post("Test", item.Id, csrf, [], new Button(Glyph("pulse", 14), new Text("acquisition.connection.test")).Compact().Soft().Submit()),
                new A(Glyph("pencil", 16)).Class("button button--icon").TextAttribute("aria-label", "common.edit").TextAttribute("title", "common.edit").Attribute("href", Address(item.Id))).Class("list-actions"));

            var caption = new List<IUiComponent> { ListPanel.Fact("acquisition.connection.lastTest", Time(item.CheckedAt), Glyph("clock", 14)) };
            if (Role != AcquisitionConnectionRole.Subtitles) caption.Add(ListPanel.Fact("acquisition.connection.priority", new ValueText(item.Priority.ToString(CultureInfo.InvariantCulture))));
            var row = new ListRow(cells, caption);
            if (state == "backoff") row.Tinted(UiTone.Warning);
            if (state == "error") row.Tinted(UiTone.Danger);
            return row;
        }

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

        // The address a connection answers at, after its kind.
        private static string Endpoint(AcquisitionConnectionDetailsV1 item)
        {
            if (item.Host is { } host) return " · " + host + (item.Port is { } port ? ":" + port.ToString(CultureInfo.InvariantCulture) : "");
            return Uri.TryCreate(item.BaseUrl, UriKind.Absolute, out var address) ? " · " + address.Authority : "";
        }

        // The editor: one dialog for a new or an opened connection, with its test, its search and its deletion beneath the form.
        private IUiComponent Editor(AcquisitionConnectionType type, string? csrf)
        {
            var body = new List<IUiComponent>();
            if (_selected is { } current) body.Add(Actions(current, csrf));
            body.Add(ConnectionForm(type, csrf));
            if (_selected is { AcquisitionConnectionTypeId: AcquisitionConnectionType.Newznab } indexer) body.Add(SearchSection(indexer, csrf));
            return new Modal(assets, "connection-editor", "acquisition.connection.type." + type, body, open: true, returnTo: Address());
        }

        // Test, then delete behind one confirmation.
        private IUiComponent Actions(AcquisitionConnectionDetailsV1 selected, string? csrf)
        {
            if (ConfirmDelete)
            {
                return new Div(new P(new Text("acquisition.connection.deleteConfirm")),
                    new Form<DeleteAcquisitionConnectionParametersV1>(form =>
                    {
                        form.Hidden(item => item.Version);
                        form.Submit("acquisition.connection.delete");
                    }, Address(selected.Id, handler: "Delete"), csrf, new DeleteAcquisitionConnectionParametersV1(selected.Id, selected.Version)),
                    new A(new Text("common.back")).Class("button").Attribute("href", Address(selected.Id))).Class("connection-editor__actions");
            }

            return new Div(Post("Test", selected.Id, csrf, [], new Button(Glyph("pulse", 14), new Text("acquisition.connection.test")).Soft().Submit()),
                new A(Glyph("trash", 14), new Text("acquisition.connection.delete")).Class("button button--warn").Attribute("href", Address(selected.Id) + "&delete=true")).Class("connection-editor__actions");
        }

        // A free-text query shows what the indexer returns; it counts towards the daily queries.
        private UiSection SearchSection(AcquisitionConnectionDetailsV1 selected, string? csrf)
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

            return new UiSection("acquisition.connection.searchTitle", [.. content]);
        }

        public async Task<IActionResult> OnGetAsync()
        {
            await LoadDataAsync();
            PreparePage();
            return Page();
        }

        public async Task<IActionResult> OnPostAsync([BindRequired] AcquisitionConnectionType acquisitionConnectionTypeId, string? name, string? baseUrl, string? apiKey,
            [BindRequired] bool isEnabled, [BindRequired] int priority, string? categories, string? downloadCategory, string? remotePath, string? localPath, string? username,
            int? maxConnections, string? password, int[]? mediaTypeIds, bool? searchesAutomatically, int? queryLimit, int? grabLimit, string? host, int? port, bool? useSsl,
            long? downloadStorageMountId, bool? removesCompleted)
        {
            _input = new SaveAcquisitionConnectionDataV1(name ?? "", acquisitionConnectionTypeId, baseUrl, apiKey, isEnabled, priority, Categories(categories),
                downloadCategory, remotePath, localPath, username, maxConnections, password, mediaTypeIds, searchesAutomatically ?? true, queryLimit, grabLimit, host, port, useSsl,
                downloadStorageMountId, removesCompleted ?? true);
            if (AcquisitionConnectionId is { } existing)
            {
                await update.ExecuteAsync<UpdateResult>(new AcquisitionConnectionParametersV1(existing), _input, HttpContext.RequestAborted);
            }
            else
            {
                AcquisitionConnectionId = (await create.ExecuteAsync<CreateAcquisitionConnectionResultV1>(_input, HttpContext.RequestAborted)).Id;
            }

            return LocalRedirect(Address());
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
                selected.SearchesAutomatically, selected.QueryLimit, selected.GrabLimit, selected.Host, selected.Port, selected.UseSsl, selected.DownloadStorageMountId, selected.RemovesCompleted);

        private Form<SaveAcquisitionConnectionDataV1> ConnectionForm(AcquisitionConnectionType type, string? csrf)
        {
            var input = _input ?? (_selected is { } selected ? InputOf(selected)
                : new SaveAcquisitionConnectionDataV1(SuggestedName ?? "", type, type == AcquisitionConnectionType.Usenet ? null : SuggestedUrl ?? "", null, true,
                    type is AcquisitionConnectionType.Newznab or AcquisitionConnectionType.Sabnzbd ? 25 : 50, [], null, null, null, useSsl: type == AcquisitionConnectionType.Usenet ? true : null));

            return new Form<SaveAcquisitionConnectionDataV1>(form =>
            {
                form.Hidden(item => item.AcquisitionConnectionTypeId);
                form.Text(item => item.Name, "acquisition.connection.name");
                // A news server is entered as in SABnzbd: server, port and SSL; every other connection by its web address.
                if (type == AcquisitionConnectionType.Usenet)
                {
                    form.Text(item => item.Host, "acquisition.connection.host");
                    form.Number(item => item.Port, "acquisition.connection.port");
                    form.Select(item => item.UseSsl, "acquisition.connection.ssl", [new SelectOption("True", texts["acquisition.connection.on"]), new SelectOption("False", texts["acquisition.connection.off"])]);
                }
                else
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
                else if (type == AcquisitionConnectionType.AnnasArchive)
                {
                    form.Password(item => item.ApiKey, "acquisition.connection.memberKey", "new-password");
                }
                else if (type != AcquisitionConnectionType.Gutenberg)
                {
                    form.Password(item => item.ApiKey, "acquisition.connection.apiKey", "new-password");
                }

                form.Number(item => item.Priority, "acquisition.connection.priority");
                form.Select(item => item.IsEnabled, "acquisition.connection.enabled", [new SelectOption("True", texts["acquisition.connection.on"]), new SelectOption("False", texts["acquisition.connection.off"])]);

                // Empty media scope serves all types. SABnzbd connections can use separate
                // categories per type while sharing the same SAB server and path mapping.
                if (type is AcquisitionConnectionType.Newznab or AcquisitionConnectionType.Sabnzbd)
                {
                    form.Choices(item => item.MediaTypeIds, "acquisition.connection.mediaTypes", Enum.GetValues<MediaType>()
                        .Select(media => new SelectOption(((int)media).ToString(CultureInfo.InvariantCulture), texts[MediaTypeLabel.Key(media, null)])));
                }

                // Indexer categories (empty uses the media type defaults), automatic searches and daily limits.
                if (type == AcquisitionConnectionType.Newznab)
                {
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
                if (type == AcquisitionConnectionType.Sabnzbd)
                {
                    form.Text(item => item.DownloadCategory, "acquisition.connection.category");
                    form.Toggle(item => item.RemovesCompleted, "acquisition.connection.removesCompleted");
                    // SAB reports its real Complete path; only the Jularr-visible folder is chosen here.
                    if (_selected?.RemotePath is { } detected)
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
                    var browse = Microsoft.AspNetCore.WebUtilities.QueryHelpers.AddQueryString(Address(AcquisitionConnectionId, type, "Folders"),
                        "storageMountId", input.DownloadStorageMountId?.ToString(CultureInfo.InvariantCulture) ?? "");
                    if (texts.Can("storage.read"))
                    {
                        form.Content(new FolderPicker(browse, input.LocalPath ?? "", nameof(input.LocalPath), "/Admin/Storage",
                            storageMountFieldName: nameof(input.DownloadStorageMountId), isVisible: input.DownloadStorageMountId is not null));
                    }
                }

                form.Cancel();
                form.Submit("common.save");
            }, Address(AcquisitionConnectionId, type), csrf, input, _input is null ? null : FormError);
        }

        // A form of one connection's action; posted through htmx, it swaps the page content.
        private IUiComponent Post(string handler, long id, string? csrf, (string Name, string Value)[] hidden, IUiComponent button) => new PostForm(Address(id, handler: handler), csrf, hidden, button);

        private IUiComponent Glyph(string name, int size = 20) => new Icon(name, assets, size);

        private static IUiComponent Number(int value) => new ValueText(value.ToString("N0", CultureInfo.CurrentCulture));

        private static IUiComponent Dim() => new Span(new ValueText("–")).Class("list-value list-value--dim");

        private static IUiComponent Time(DateTime? value) => value is { } time
            ? new Span(new ValueText(time.ToLocalTime().ToString("g", CultureInfo.CurrentCulture))).Class("list-value")
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
