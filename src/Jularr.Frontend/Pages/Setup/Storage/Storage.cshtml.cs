using Jularr.Backend.Service.Admin.Library.V1;
using Jularr.Backend.Service.Admin.Storage.V1;
using Jularr.Frontend.Framework;
using Microsoft.AspNetCore.Antiforgery;

namespace Jularr.Frontend.Pages.Setup.Storage
{
    // Reuse the connection and path editor.
    public sealed class StorageModel(ReadStorageMountsV1 read, ReadStorageMountV1 readMount, ReadStorageProtocolsV1 readProtocols, ReadStorageLocalPathsV1 readLocalPaths,
        CreateStorageMountV1 create, UpdateStorageMountV1 update, ExecuteStorageMountV1 execute, WakeStorageMountV1 wake, RemoveStorageMountV1 remove,
        UpdateStorageSynologyV1 synology, ReadStorageSynologyStatusV1 synologyStatus, PowerStorageSynologyV1 synologyPower, ReadStorageDirectoriesV1 readDirectories, ReadLibraryRootsV1 readRoots, CreateLibraryRootV1 createRoot, RenameLibraryRootV1 renameRoot, DeleteLibraryRootV1 deleteRoot,
        QueueLibraryScanV1 queueScan, IAntiforgery antiforgery, UiText texts)
        : Admin.Storage.StorageModel(read, readMount, readProtocols, readLocalPaths, create, update, execute, wake, remove, synology, synologyStatus, synologyPower, readDirectories, readRoots, createRoot, renameRoot, deleteRoot,
            queueScan, antiforgery, texts)
    {
    }
}
