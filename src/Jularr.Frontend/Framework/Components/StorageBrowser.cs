using System.Globalization;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Storage.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Storage;
using Jularr.Backend.Service.Admin.Storage.V1;
using Microsoft.AspNetCore.WebUtilities;

namespace Jularr.Frontend.Framework
{
    // The fragment of the folder picker that chooses a connection and then a folder on it: the connections first, then the folders of the one opened.
    // A place that writes (download folders) refuses a connection that is read-only or not mounted for writing.
    public static class StorageBrowser
    {
        public static async Task<IUiComponent> FragmentAsync(string home, long? storageMountId, string? path, string? afterName, int mountPage, bool needsWriting,
            ReadStorageMountsV1 mounts, ReadStorageMountV1 mount, ReadStorageDirectoriesV1 directories, UiText texts, CancellationToken ct)
        {
            if (storageMountId is { } id)
            {
                var connection = await mount.ExecuteAsync<StorageMountDetailsV1>(new StorageMountParametersV1(id), ct);
                var items = await directories.ExecuteAsync<BaseDataList<StorageDirectoryV1>>(new StorageMountParametersV1(id), new ReadStorageDirectoriesDataV1(path ?? "", afterName), ct);
                var browse = QueryHelpers.AddQueryString(home, "storageMountId", id.ToString(CultureInfo.InvariantCulture));
                var warning = !needsWriting ? null : connection.IsReadOnly ? "downloadStorage.readOnly" : connection.StorageProtocolTypeId == StorageProtocolType.Smb && !connection.IsVolume
                    ? "downloadStorage.mountRequired" : null;
                return FolderPicker.Directories(browse, path ?? "", items.Select(item => item.Name).ToArray(), texts, afterName, items.Count == StorageDirectoryV1.PageSize,
                    home, id, connection.Name + " /" + path, warning);
            }

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
            return new Div([.. list]).Attribute("data-folder-fragment", "");
        }
    }
}
