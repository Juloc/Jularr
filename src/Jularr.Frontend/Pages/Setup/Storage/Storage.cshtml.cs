using Jularr.Backend.Service.Admin.Library.V1;
using Jularr.Backend.Service.Admin.Storage.V1;
using Jularr.Backend.Data.Admin.Instance.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Service.Admin.Instance.V1;
using Jularr.Frontend.Framework;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;

namespace Jularr.Frontend.Pages.Setup.Storage
{
    // Reuse the connection and path editor.
    public sealed class StorageModel(ReadStorageMountsV1 read, ReadStorageMountV1 readMount, ReadStorageProtocolsV1 readProtocols, ReadStorageLocalPathsV1 readLocalPaths,
        CreateStorageMountV1 create, UpdateStorageMountV1 update, CheckStorageMountV1 check, ReadStorageMountVolumeV1 readVolume, WakeStorageMountV1 wake, RemoveStorageMountV1 remove,
        UpdateStorageSynologyV1 synology, ReadStorageSynologyStatusV1 synologyStatus, PowerStorageSynologyV1 synologyPower, ReadStorageSynologyCertificateV1 synologyCertificate,
        TrustStorageSynologyCertificateV1 trustCertificate, ReadStorageDirectoriesV1 readDirectories, ReadLibraryRootsV1 readRoots, CreateLibraryRootV1 createRoot, RenameLibraryRootV1 renameRoot, DeleteLibraryRootV1 deleteRoot,
        QueueLibraryScanV1 queueScan, IAntiforgery antiforgery, UiText texts, ReadSetupProgressV1 readProgress, SaveSetupLibrariesV1 next)
        : Admin.Storage.StorageModel(read, readMount, readProtocols, readLocalPaths, create, update, check, readVolume, wake, remove, synology, synologyStatus, synologyPower, synologyCertificate, trustCertificate, readDirectories, readRoots, createRoot, renameRoot, deleteRoot,
            queueScan, antiforgery, texts)
    {
        private SetupProgressV1? _progress;

        protected override void DefineFooter(Page page, string? csrf)
        {
            if (_progress is not { IsCompleted: false } progress) return;
            page.Content.Add(new Form<CompleteSetupDataV1>(form =>
            {
                form.Hidden(item => item.Version);
                form.Submit("setup.next");
            }, "/Setup/Storage?handler=Next", csrf, new CompleteSetupDataV1(progress.Version), FormError));
        }

        protected override async Task LoadDataAsync()
        {
            await base.LoadDataAsync();
            _progress = await readProgress.ExecuteAsync<SetupProgressV1>(cancellationToken: HttpContext.RequestAborted);
        }

        public async Task<IActionResult> OnPostNextAsync(long version)
        {
            await next.ExecuteAsync<UpdateResult>(new SetupProgressParametersV1(), new CompleteSetupDataV1(version), HttpContext.RequestAborted);
            return LocalRedirect("/Setup/Sources");
        }
    }
}
