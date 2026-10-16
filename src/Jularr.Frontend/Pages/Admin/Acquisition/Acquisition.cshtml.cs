using System.Globalization;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Acquisition.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Enums.Instance;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Service.Admin.Acquisition.V1;
using Jularr.Frontend.Framework;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Jularr.Frontend.Pages.Admin.Acquisition
{
    // Indexers, download clients and subtitle providers, each kind on its own page.
    public sealed class AcquisitionModel(ReadAcquisitionConnectionsV1 connections, ReadAcquisitionConnectionV1 connection, CreateAcquisitionConnectionV1 create,
        UpdateAcquisitionConnectionV1 update, DeleteAcquisitionConnectionV1 delete, TestAcquisitionConnectionV1 test, SearchAcquisitionConnectionV1 search, IAntiforgery antiforgery,
        UiText texts) : JularrPage
    {
        // The kinds each tab adds.
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

        [BindProperty(SupportsGet = true)]
        public long? AcquisitionConnectionId { get; set; }

        [BindProperty(SupportsGet = true)]
        public AcquisitionConnectionType? Type { get; set; }

        // Indexers live at the area's address, download clients and subtitles at their own.
        private AcquisitionConnectionRole Role { get; set; } = AcquisitionConnectionRole.Search;

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
            var csrf = antiforgery.GetAndStoreTokens(HttpContext).RequestToken;
            if (SetupWizard.IsActive(Request)) page.Content.Add(SetupWizard.Frame(Role == AcquisitionConnectionRole.Download ? SetupStepType.Downloads : SetupStepType.Indexers));
            page.Navigation("nav.admin");
            var type = _selected?.AcquisitionConnectionTypeId ?? Type;

            page.Content.Add(new Split(split =>
            {
                if (_connections is { } list)
                {
                    split.Primary.Add(list.Items.Count == 0 ? new P(new Text("acquisition.connection.none." + Role)).Attribute("role", "status") : ConnectionTable(list.Items));
                    if (PageNumber > 1) split.Primary.Add(new A(new Text("common.previous")).Attribute("href", Address(pageNumber: PageNumber - 1)));
                    if (list.Items.Count == list.PageSize) split.Primary.Add(new A(new Text("common.next")).Attribute("href", Address(pageNumber: PageNumber + 1)));
                }

                // Indexers also come from a Prowlarr in one step; subtitle providers from the source catalog with their addresses.
                var additions = Additions[Role].Select(addition => (IUiComponent)new A(new Text(addition.LabelKey)).Class("button").Attribute("href", Address(type: addition.Type))).ToList();
                if (Role == AcquisitionConnectionRole.Search)
                {
                    additions.Add(new A(new Text("acquisition.connection.importProwlarr")).Class("button")
                        .Attribute("href", SetupWizard.IsActive(Request) ? SetupWizard.Paths[SetupStepType.Media] : "/Admin/Acquisition/Takeover"));
                }
                else if (Role == AcquisitionConnectionRole.Subtitles)
                {
                    additions.Add(new A(new Text("acquisition.connection.addSubtitles")).Class("button").Attribute("href", "/Admin/Acquisition/Sources"));
                }

                split.Primary.Add(new Div([.. additions]).Class("page-actions"));

                if (type is { } selectedType)
                {
                    // A test just run replaces the stored state.
                    if (TempData["AcquisitionTested"] is string tested) split.Secondary.Add(new Strong(new ValueText(tested)).Attribute("role", "status"));
                    else if (_selected is { } selected) split.Secondary.Add(new Strong(Health(selected)).Attribute("role", "status"));
                    split.Secondary.Add(ConnectionForm(selectedType, csrf));
                    if (_selected is { } current)
                    {
                        split.Secondary.Add(Actions(current, csrf));
                        if (current.AcquisitionConnectionTypeId == AcquisitionConnectionType.Newznab) split.Secondary.Add(SearchSection(current, csrf));
                    }
                }
            }).MasterDetail(type is not null, Address()));

            if (FormError is not null && _searchInput is null) page.Content.Add(new P(ErrorText.Of(FormError)).Attribute("role", "alert"));
        }

        private Table<AcquisitionConnectionDetailsV1> ConnectionTable(IReadOnlyList<AcquisitionConnectionDetailsV1> items)
        {
            // Each kind shows what decides its use.
            var table = new Table<AcquisitionConnectionDetailsV1>(items)
                .Text(item => item.Name, "acquisition.connection.name")
                .Resource(item => item.AcquisitionConnectionTypeId, "acquisition.connection.type", value => "acquisition.connection.type." + value)
                .Content("table.status", Health);
            if (Role == AcquisitionConnectionRole.Search)
            {
                table.Content("acquisition.connection.today", Usage)
                    .Resource(item => item.SearchesAutomatically, "acquisition.connection.searches", automatic => automatic ? "acquisition.connection.automatic" : "acquisition.connection.manualOnly");
            }

            if (Role != AcquisitionConnectionRole.Subtitles) table.Text(item => item.Priority, "acquisition.connection.priority");
            return table.Resource(item => item.IsEnabled, "acquisition.connection.enabled", enabled => enabled ? "acquisition.connection.on" : "acquisition.connection.off")
                .Link(item => item.Id, "common.actions", id => Address(id), "common.edit");
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
                    new A(new Text("common.back")).Class("button").Attribute("href", Address(selected.Id))).Class("page-actions");
            }

            return new Div(new Form<AcquisitionConnectionParametersV1>(form => form.Submit("acquisition.connection.test"), Address(selected.Id, handler: "Test"), csrf,
                    new AcquisitionConnectionParametersV1(selected.Id)),
                new A(new Text("acquisition.connection.delete")).Class("button").Attribute("href", Address(selected.Id) + "&delete=true")).Class("page-actions");
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
            int? maxConnections, string? password, int[]? mediaTypeIds, bool? searchesAutomatically, int? queryLimit, int? grabLimit, string? host, int? port, bool? useSsl)
        {
            _input = new SaveAcquisitionConnectionDataV1(name ?? "", acquisitionConnectionTypeId, baseUrl, apiKey, isEnabled, priority, Categories(categories),
                downloadCategory, remotePath, localPath, username, maxConnections, password, mediaTypeIds, searchesAutomatically ?? true, queryLimit, grabLimit, host, port, useSsl);
            if (AcquisitionConnectionId is { } existing)
            {
                await update.ExecuteAsync<UpdateResult>(new AcquisitionConnectionParametersV1(existing), _input, HttpContext.RequestAborted);
            }
            else
            {
                AcquisitionConnectionId = (await create.ExecuteAsync<CreateAcquisitionConnectionResultV1>(_input, HttpContext.RequestAborted)).Id;
            }

            return LocalRedirect(Address(AcquisitionConnectionId));
        }

        public async Task<IActionResult> OnPostTestAsync()
        {
            // A successful test also ends a pause after failures.
            var result = await test.ExecuteAsync<AcquisitionConnectionTestV1>(new AcquisitionConnectionParametersV1(SelectedId()), default, HttpContext.RequestAborted);
            TempData["AcquisitionTested"] = result.IsAvailable
                ? string.Join(" · ", new[] { texts["acquisition.connection.healthy"], result.ServerVersion is { } version ? texts["table.version"] + " " + version : null,
                    result.SearchModes.Length > 0 ? texts["acquisition.connection.searchModes"] + " " + string.Join(", ", result.SearchModes) : null }.OfType<string>())
                : texts["errors." + (result.ErrorKey ?? "acquisition_unavailable")];
            return LocalRedirect(Address(AcquisitionConnectionId));
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
            // The address names the kind; an opened connection keeps its own.
            Role = Request.Path.Value?.EndsWith("/Downloads", StringComparison.OrdinalIgnoreCase) is true ? AcquisitionConnectionRole.Download
                : Request.Path.Value?.EndsWith("/Subtitles", StringComparison.OrdinalIgnoreCase) is true ? AcquisitionConnectionRole.Subtitles
                : AcquisitionConnectionRole.Search;
            if (AcquisitionConnectionId is { } id)
            {
                _selected = await connection.ExecuteAsync<AcquisitionConnectionDetailsV1>(new AcquisitionConnectionParametersV1(id), HttpContext.RequestAborted);
                Role = _selected.AcquisitionConnectionRoleId;
            }

            _connections = await connections.ExecuteAsync<PageResult<AcquisitionConnectionDetailsV1>>(new ReadAcquisitionConnectionsParametersV1(PageNumber, acquisitionConnectionRoleId: Role),
                HttpContext.RequestAborted);
        }

        private Form<SaveAcquisitionConnectionDataV1> ConnectionForm(AcquisitionConnectionType type, string? csrf)
        {
            var input = _input ?? (_selected is { } selected
                ? new SaveAcquisitionConnectionDataV1(selected.Name, selected.AcquisitionConnectionTypeId, selected.BaseUrl, null, selected.IsEnabled, selected.Priority, selected.Categories,
                    selected.DownloadCategory, selected.RemotePath, selected.LocalPath, selected.Username, selected.MaxConnections, null, selected.MediaTypeIds,
                    selected.SearchesAutomatically, selected.QueryLimit, selected.GrabLimit, selected.Host, selected.Port, selected.UseSsl)
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

                // Indexer categories (empty uses the media type defaults), the media it serves (none: all), automatic searches and daily limits.
                if (type == AcquisitionConnectionType.Newznab)
                {
                    form.Text(item => item.Categories, "acquisition.connection.categories");
                    form.Choices(item => item.MediaTypeIds, "acquisition.connection.mediaTypes", Enum.GetValues<MediaType>()
                        .Select(media => new SelectOption(((int)media).ToString(CultureInfo.InvariantCulture), texts[MediaTypeLabel.Key(media, null)])));
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
                    form.Text(item => item.RemotePath, "acquisition.connection.remotePath");
                    form.Text(item => item.LocalPath, "acquisition.connection.localPath");
                }

                form.Submit("common.save");
            }, Address(AcquisitionConnectionId, type), csrf, input, _input is null ? null : FormError);
        }

        // A failing connection waits before automatic use; a successful test ends the pause.
        private IUiComponent Health(AcquisitionConnectionDetailsV1 item) => item is { IsBackedOff: true, RetryAt: { } retry }
            ? new Span(new Text("acquisition.connection.backoff"), new ValueText(" " + retry.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)))
            : new Text(item.CheckedAt is null ? "acquisition.connection.unchecked" : item.ErrorKey is { } error ? "errors." + error : "acquisition.connection.healthy");

        // Today's queries and grabs of an indexer against its limits.
        private static IUiComponent Usage(AcquisitionConnectionDetailsV1 item) => item.AcquisitionConnectionTypeId != AcquisitionConnectionType.Newznab ? new Span()
            : new Span(new ValueText(Count(item.QueryCount, item.QueryLimit) + " "), new Text("acquisition.connection.queries"),
                new ValueText(" · " + Count(item.GrabCount, item.GrabLimit) + " "), new Text("acquisition.connection.grabs"));

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
