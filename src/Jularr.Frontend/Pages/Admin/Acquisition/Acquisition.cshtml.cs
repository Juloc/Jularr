using System.Globalization;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Acquisition.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Service.Admin.Acquisition.V1;
using Jularr.Frontend.Framework;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Jularr.Frontend.Pages.Admin.Acquisition
{
    public sealed class AcquisitionModel(ReadAcquisitionConnectionsV1 connections, ReadAcquisitionConnectionV1 connection, CreateAcquisitionConnectionV1 create,
        UpdateAcquisitionConnectionV1 update, DeleteAcquisitionConnectionV1 delete, TestAcquisitionConnectionV1 test, IAntiforgery antiforgery, UiText texts) : JularrPage
    {
        private PageResult<AcquisitionConnectionDetailsV1>? _connections;
        private AcquisitionConnectionDetailsV1? _selected;
        private SaveAcquisitionConnectionDataV1? _input;

        [BindProperty(SupportsGet = true)]
        public long? AcquisitionConnectionId { get; set; }

        [BindProperty(SupportsGet = true)]
        public AcquisitionConnectionType? Type { get; set; }

        [BindProperty(SupportsGet = true)]
        public int PageNumber { get; set; } = 1;

        protected override void Define(Page page)
        {
            page.Title("admin.acquisition.title");
            page.Navigation("nav.admin");
            var csrf = antiforgery.GetAndStoreTokens(HttpContext).RequestToken;
            var type = _selected?.AcquisitionConnectionTypeId ?? Type;

            // Indexers and download clients share one list.
            page.Content.Add(new Split(split =>
            {
                if (_connections is { } list)
                {
                    split.Primary.Add(new Table<AcquisitionConnectionDetailsV1>(list.Items)
                        .Text(item => item.Name, "acquisition.connection.name")
                        .Resource(item => item.AcquisitionConnectionTypeId, "acquisition.connection.type", value => "acquisition.connection.type." + value)
                        .Text(item => item.BaseUrl, "acquisition.connection.baseUrl")
                        .Content("table.status", Health)
                        .Resource(item => item.IsEnabled, "acquisition.connection.enabled", enabled => enabled ? "acquisition.connection.on" : "acquisition.connection.off")
                        .Link(item => item.Id, "common.actions", id => Address(id), "common.edit"));
                    if (PageNumber > 1) split.Primary.Add(new A(new Text("common.previous")).Attribute("href", Address(pageNumber: PageNumber - 1)));
                    if (list.Items.Count == list.PageSize) split.Primary.Add(new A(new Text("common.next")).Attribute("href", Address(pageNumber: PageNumber + 1)));
                    split.Primary.Add(new A(new Text("acquisition.connection.addIndexer")).Class("button").Attribute("href", Address(type: AcquisitionConnectionType.Newznab)));
                    split.Primary.Add(new A(new Text("acquisition.connection.addClient")).Class("button").Attribute("href", Address(type: AcquisitionConnectionType.Sabnzbd)));
                }

                if (type is { } selectedType)
                {
                    split.Secondary.Add(ConnectionForm(selectedType, csrf));
                    if (_selected is { } selected)
                    {
                        split.Secondary.Add(new Form<AcquisitionConnectionParametersV1>(form => form.Submit("acquisition.connection.test"), Address(selected.Id, handler: "Test"), csrf,
                            new AcquisitionConnectionParametersV1(selected.Id)));
                        split.Secondary.Add(new Form<DeleteAcquisitionConnectionParametersV1>(form =>
                        {
                            form.Hidden(item => item.Version);
                            form.Submit("acquisition.connection.delete");
                        }, Address(selected.Id, handler: "Delete"), csrf, new DeleteAcquisitionConnectionParametersV1(selected.Id, selected.Version)));
                    }

                    if (TempData["AcquisitionTested"] is true && _selected is { } tested)
                    {
                        split.Secondary.Add(new P(Health(tested)).Attribute("role", "status"));
                    }
                }
            }).MasterDetail(type is not null, Address()));

            if (Error is not null) page.Content.Add(new P(new Text(Error.ResourceKey)).Attribute("role", "alert"));
        }

        public async Task<IActionResult> OnGetAsync()
        {
            await LoadDataAsync();
            PreparePage();
            return Page();
        }

        public async Task<IActionResult> OnPostAsync([BindRequired] AcquisitionConnectionType acquisitionConnectionTypeId, string? name, string? baseUrl, string? apiKey,
            [BindRequired] bool isEnabled, [BindRequired] int priority, string? downloadCategory, string? remotePath, string? localPath)
        {
            // Categories are kept; the editor does not change them.
            var categories = AcquisitionConnectionId is { } id
                ? (await connection.ExecuteAsync<AcquisitionConnectionDetailsV1>(new AcquisitionConnectionParametersV1(id), HttpContext.RequestAborted)).Categories
                : [];
            _input = new SaveAcquisitionConnectionDataV1(name ?? "", acquisitionConnectionTypeId, baseUrl ?? "", Blank(apiKey), isEnabled, priority, categories,
                Blank(downloadCategory), Blank(remotePath), Blank(localPath));
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
            await test.ExecuteAsync<AcquisitionConnectionTestV1>(new AcquisitionConnectionParametersV1(SelectedId()), default, HttpContext.RequestAborted);
            TempData["AcquisitionTested"] = true;
            return LocalRedirect(Address(AcquisitionConnectionId));
        }

        public async Task<IActionResult> OnPostDeleteAsync([BindRequired] long version)
        {
            await delete.ExecuteAsync<UpdateResult>(new DeleteAcquisitionConnectionParametersV1(SelectedId(), version), default, HttpContext.RequestAborted);
            return LocalRedirect(Address());
        }

        protected override async Task LoadDataAsync()
        {
            _connections = await connections.ExecuteAsync<PageResult<AcquisitionConnectionDetailsV1>>(new ReadAcquisitionConnectionsParametersV1(PageNumber), HttpContext.RequestAborted);
            if (AcquisitionConnectionId is { } id)
            {
                _selected = await connection.ExecuteAsync<AcquisitionConnectionDetailsV1>(new AcquisitionConnectionParametersV1(id), HttpContext.RequestAborted);
            }
        }

        private Form<SaveAcquisitionConnectionDataV1> ConnectionForm(AcquisitionConnectionType type, string? csrf)
        {
            var input = _input ?? (_selected is { } selected
                ? new SaveAcquisitionConnectionDataV1(selected.Name, selected.AcquisitionConnectionTypeId, selected.BaseUrl, null, selected.IsEnabled, selected.Priority, selected.Categories,
                    selected.DownloadCategory, selected.RemotePath, selected.LocalPath)
                : new SaveAcquisitionConnectionDataV1("", type, "", null, true, 25, [], null, null, null));

            return new Form<SaveAcquisitionConnectionDataV1>(form =>
            {
                form.Hidden(item => item.AcquisitionConnectionTypeId);
                form.Text(item => item.Name, "acquisition.connection.name");
                form.Text(item => item.BaseUrl, "acquisition.connection.baseUrl");
                form.Password(item => item.ApiKey, "acquisition.connection.apiKey", "new-password");
                form.Number(item => item.Priority, "acquisition.connection.priority");
                form.Select(item => item.IsEnabled, "acquisition.connection.enabled", [new SelectOption("True", texts["acquisition.connection.on"]), new SelectOption("False", texts["acquisition.connection.off"])]);

                // Only download clients place files and report their own paths.
                if (type == AcquisitionConnectionType.Sabnzbd)
                {
                    form.Text(item => item.DownloadCategory, "acquisition.connection.category");
                    form.Text(item => item.RemotePath, "acquisition.connection.remotePath");
                    form.Text(item => item.LocalPath, "acquisition.connection.localPath");
                }

                form.Submit("common.save");
            }, Address(AcquisitionConnectionId, type), csrf, input, _input is null ? null : Error);
        }

        private static IUiComponent Health(AcquisitionConnectionDetailsV1 item) =>
            new Text(item.CheckedAt is null ? "acquisition.connection.unchecked" : item.ErrorKey is { } error ? "errors." + error : "acquisition.connection.healthy");

        private long SelectedId() => AcquisitionConnectionId ?? throw new KeyNotFoundException("No connection was selected.");

        private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

        private string Address(long? id = null, AcquisitionConnectionType? type = null, string? handler = null, int? pageNumber = null)
        {
            var address = "/Admin/Acquisition?PageNumber=" + (pageNumber ?? PageNumber).ToString(CultureInfo.InvariantCulture);
            if (id is { } connectionId) address += "&AcquisitionConnectionId=" + connectionId.ToString(CultureInfo.InvariantCulture);
            else if (type is { } connectionType) address += "&Type=" + connectionType;
            return handler is null ? address : address + "&handler=" + handler;
        }
    }
}
