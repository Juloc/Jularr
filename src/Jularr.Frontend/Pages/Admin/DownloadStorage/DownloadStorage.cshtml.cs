using System.Globalization;
using System.Text.Encodings.Web;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Storage.V1;
using Jularr.Backend.Data.Admin.Instance.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Instance;
using Jularr.Backend.Data.Enums.Storage;
using Jularr.Backend.Service.Admin.Storage.V1;
using Jularr.Backend.Service.Admin.Instance.V1;
using Jularr.Frontend.Framework;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.WebUtilities;

namespace Jularr.Frontend.Pages.Admin.DownloadStorage
{
    public sealed class DownloadStorageModel(ReadDownloadStorageSettingsV1 read, SaveDownloadStorageSettingsV1 save, ReadStorageMountsV1 mounts,
        ReadStorageMountV1 mount, ReadStorageDirectoriesV1 directories, UpdateSetupProgressV1 progress, IAntiforgery antiforgery, UiText texts) : JularrPage
    {
        private readonly Dictionary<DownloadStorageArea, DownloadStorageSettingsV1> _settings = [];
        private readonly Dictionary<long, StorageMountDetailsV1> _mounts = [];
        private SaveDownloadStorageDataV1? _input;
        private DownloadStorageArea? _failedArea;
        private bool IsSetup => SetupWizard.IsActive(Request);

        protected override void Define(Page page)
        {
            page.Title("downloadStorage.title");
            page.Navigation("nav.admin");
            page.Asset("Framework/Components/FolderPicker.entry.ts");
            if (IsSetup) page.Content.Add(SetupWizard.Frame(SetupStepType.DownloadStorage));
            else if (texts.Can("storage.read")) page.Content.Add(new NavigationTabs([new("storage.connections", "/Admin/Storage", false), new("downloadStorage.title", "/Admin/DownloadStorage", true)], "storage.sections"));
            var csrf = antiforgery.GetAndStoreTokens(HttpContext).RequestToken;
            page.Content.Add(new P(new Text("downloadStorage.purpose")));
            foreach (var area in Enum.GetValues<DownloadStorageArea>())
            {
                if (!_settings.TryGetValue(area, out var settings)) continue;
                var input = _failedArea == area && _input is not null ? _input : new SaveDownloadStorageDataV1(settings.IncompleteStorageMountId, settings.IncompletePath ?? "",
                    settings.CompleteStorageMountId, settings.CompletePath ?? "", settings.FreeSpaceReserveBytes, settings.MaxJobBytes, settings.MaxWorkspaceBytes,
                    settings.MaxParallelJobs, settings.Version, true);
                page.Content.Add(new UiSection("downloadStorage.area." + area,
                [
                    new Form<SaveDownloadStorageDataV1>(form =>
                    {
                        form.Hidden(item => item.IncompleteStorageMountId);
                        form.Hidden(item => item.IncompletePath);
                        form.Content(new Div(new Span(new Text("downloadStorage.incomplete")).Class("ui-field__top"),
                            Picker(input.IncompleteStorageMountId, input.IncompletePath ?? "", nameof(input.IncompletePath), nameof(input.IncompleteStorageMountId))).Class("ui-field"));
                        form.Hidden(item => item.CompleteStorageMountId);
                        form.Hidden(item => item.CompletePath);
                        form.Content(new Div(new Span(new Text("downloadStorage.complete")).Class("ui-field__top"),
                            Picker(input.CompleteStorageMountId, input.CompletePath ?? "", nameof(input.CompletePath), nameof(input.CompleteStorageMountId))).Class("ui-field"));
                        form.ByteSize(item => item.FreeSpaceReserveBytes, "downloadStorage.reserve");
                        form.Number(item => item.MaxParallelJobs, "downloadStorage.parallel");
                        form.ByteSize(item => item.MaxJobBytes, "downloadStorage.jobLimit", new FieldHelp(Info: "downloadStorage.limitHint"));
                        form.ByteSize(item => item.MaxWorkspaceBytes, "downloadStorage.workspaceLimit", new FieldHelp(Info: "downloadStorage.limitHint"));
                        form.Hidden(item => item.Version);
                        form.Hidden(item => item.Confirmed);
                        form.Content(new P(new Text("downloadStorage.confirmHint")));
                        form.Submit("downloadStorage.confirm");
                    }, Address("Save", area), csrf, input, _failedArea == area ? FormError : null)
                ]));
            }

            if (IsSetup)
            {
                page.Content.Add(new Form<UpdateSetupProgressDataV1>(form =>
                {
                    form.Hidden(item => item.SetupStepTypeId);
                    form.Submit("setup.next");
                }, Address("Next"), csrf, new UpdateSetupProgressDataV1(SetupStepType.Providers), FormError));
            }
            if (FormError is not null && _failedArea is null) page.Content.Add(new P(ErrorText.Of(FormError)).Attribute("role", "alert"));
        }

        // What Jularr last measured on the connection's volume; an unreachable one shows nothing.
        private string Free(StorageMountDetailsV1 connection) => connection is { StorageHealthTypeId: StorageHealthType.Online, FreeBytes: { } free }
            ? " · " + string.Format(CultureInfo.CurrentCulture, texts["downloadStorage.free"], ByteSize.Format(free)) : "";

        private FolderPicker Picker(long? storageMountId, string path, string field, string mountField)
        {
            var browse = QueryHelpers.AddQueryString(Address("Folders"), new Dictionary<string, string?>
            {
                ["storageMountId"] = storageMountId?.ToString(CultureInfo.InvariantCulture)
            });
            var label = storageMountId is { } id && _mounts.TryGetValue(id, out var connection) ? connection.Name + " /" + path + Free(connection) : texts["downloadStorage.unset"];
            return new FolderPicker(browse, path, field, browse, storageMountFieldName: mountField, choosesStorage: true, selectedLabel: label);
        }

        public async Task<IActionResult> OnGetAsync()
        {
            if (!texts.Can("acquisition.manage", "Acquisition")) return LocalRedirect(IsSetup ? "/Setup/Sources" : "/Settings");
            await LoadDataAsync();
            PreparePage();
            return Page();
        }

        public async Task<IActionResult> OnPostSaveAsync([BindRequired] DownloadStorageArea downloadStorageAreaId, long? incompleteStorageMountId, string? incompletePath,
            long? completeStorageMountId, string? completePath, long? freeSpaceReserveBytes, long? maxJobBytes, long? maxWorkspaceBytes, [BindRequired] int maxParallelJobs,
            [BindRequired] long version, [BindRequired] bool confirmed)
        {
            _failedArea = downloadStorageAreaId;
            _input = new SaveDownloadStorageDataV1(incompleteStorageMountId, incompletePath ?? "", completeStorageMountId, completePath ?? "", freeSpaceReserveBytes,
                maxJobBytes, maxWorkspaceBytes, maxParallelJobs, version, confirmed);
            await save.ExecuteAsync<UpdateResult>(new DownloadStorageParametersV1(downloadStorageAreaId), _input, HttpContext.RequestAborted);
            return LocalRedirect(Address());
        }

        public async Task<IActionResult> OnPostNextAsync()
        {
            if (!IsSetup) return NotFound();
            await progress.ExecuteAsync<UpdateResult>(new SetupProgressParametersV1(), new UpdateSetupProgressDataV1(SetupStepType.Providers), HttpContext.RequestAborted);
            return LocalRedirect("/Setup/Sources");
        }

        public async Task<IActionResult> OnGetFoldersAsync(long? storageMountId, string? path, string? afterName, int mountPage = 1)
        {
            IUiComponent content;
            var ct = HttpContext.RequestAborted;
            var home = Address("Folders");
            if (storageMountId is { } id)
            {
                var connection = await mount.ExecuteAsync<StorageMountDetailsV1>(new StorageMountParametersV1(id), ct);
                var items = await directories.ExecuteAsync<BaseDataList<StorageDirectoryV1>>(new StorageMountParametersV1(id), new ReadStorageDirectoriesDataV1(path ?? "", afterName), ct);
                var browse = QueryHelpers.AddQueryString(home, "storageMountId", id.ToString(CultureInfo.InvariantCulture));
                var warning = connection.IsReadOnly ? "downloadStorage.readOnly" : connection.StorageProtocolTypeId == StorageProtocolType.Smb && !connection.IsVolume
                    ? "downloadStorage.mountRequired" : null;
                content = FolderPicker.Directories(browse, path ?? "", items.Select(item => item.Name).ToArray(), texts, afterName, items.Count == StorageDirectoryV1.PageSize,
                    home, id, connection.Name + " /" + path, warning);
            }
            else
            {
                var configured = await mounts.ExecuteAsync<PageResult<StorageMountDetailsV1>>(new ReadStorageMountsParametersV1(mountPage), ct);
                var entries = configured.Items.Select(item => (IUiComponent)new Li(new A(new ValueText(item.Name + " · " + item.SharePath))
                    .Attribute("href", QueryHelpers.AddQueryString(home, "storageMountId", item.Id.ToString(CultureInfo.InvariantCulture)))
                    .Attribute("data-folder-navigate", ""))).ToArray();
                var list = new List<IUiComponent> { new H3(new Text("folderPicker.storageConnections")) };
                list.Add(entries.Length == 0 ? new P(new Text("downloadStorage.noStorage")) : new Ul(entries).Class("folder-picker__folders"));
                if (mountPage > 1) list.Add(new A(new Text("common.previous")).Attribute("href", QueryHelpers.AddQueryString(home, "mountPage", (mountPage - 1).ToString(CultureInfo.InvariantCulture)))
                    .Attribute("data-folder-navigate", ""));
                if (configured.Items.Count == configured.PageSize) list.Add(new A(new Text("common.next")).Attribute("href", QueryHelpers.AddQueryString(home, "mountPage", (mountPage + 1).ToString(CultureInfo.InvariantCulture)))
                    .Attribute("data-folder-navigate", ""));
                content = new Div([.. list]).Attribute("data-folder-fragment", "");
            }

            using var writer = new StringWriter(CultureInfo.InvariantCulture);
            content.WriteTo(writer, HtmlEncoder.Default, texts);
            Response.Headers.CacheControl = "no-store";
            return Content(writer.ToString(), "text/html");
        }

        protected override async Task LoadDataAsync()
        {
            foreach (var area in Enum.GetValues<DownloadStorageArea>())
            {
                var settings = await read.ExecuteAsync<DownloadStorageSettingsV1>(new DownloadStorageParametersV1(area), HttpContext.RequestAborted);
                _settings[area] = settings;
                foreach (var id in new[] { settings.IncompleteStorageMountId, settings.CompleteStorageMountId }.OfType<long>().Distinct())
                {
                    if (_mounts.ContainsKey(id)) continue;
                    _mounts[id] = await mount.ExecuteAsync<StorageMountDetailsV1>(new StorageMountParametersV1(id), HttpContext.RequestAborted);
                }
            }
        }

        private string Address(string? handler = null, DownloadStorageArea? area = null) => QueryHelpers.AddQueryString(IsSetup ? "/Setup/DownloadStorage" : "/Admin/DownloadStorage",
            new Dictionary<string, string?> { ["handler"] = handler, ["downloadStorageAreaId"] = area is { } value ? ((int)value).ToString(CultureInfo.InvariantCulture) : null });
    }
}
