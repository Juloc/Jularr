using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Storage.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Logic.Storage;
using Jularr.Backend.Logic.Storage;
using Jularr.Backend.Service.Core;
using Jularr.Integrations.Data.Nas;
using Jularr.Integrations.Logic.Storage;

namespace Jularr.Backend.Service.Admin.Storage.V1
{
    [ServiceEndpoint("GET", "/api/v1/admin/storage/downloads/{downloadStorageAreaId:int}")]
    public sealed class ReadDownloadStorageSettingsV1 : AdminService<DownloadStorageParametersV1>
    {
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<DownloadStorageSettingsV1>()];
        protected override ModuleRequirement GetInstanceModules(DownloadStorageParametersV1 parameters) => ModuleRequirement.All("Acquisition");
        protected override ServicePermission GetPermission(DownloadStorageParametersV1 parameters, Type resultType) => new("acquisition.manage");

        protected override async Task<IServiceOutput> OnRead()
        {
            var settings = await Context.ReadDownloadStorageSettingsAsync(Parameters.DownloadStorageAreaId, CancellationToken);
            return new DownloadStorageSettingsV1(settings.DownloadStorageAreaId, settings.IncompleteStorageMountId, settings.IncompletePath, settings.IncompleteFilesystemIdentity,
                settings.CompleteStorageMountId, settings.CompletePath, settings.CompleteFilesystemIdentity, settings.FreeSpaceReserveBytes, settings.MaxJobBytes,
                settings.MaxWorkspaceBytes, settings.MaxParallelJobs, settings.Version, DownloadStorageLogic.IsConfigured(settings), settings.MaxBytesPerSecond);
        }
    }

    [ServiceEndpoint("POST", "/api/v1/admin/storage/downloads/{downloadStorageAreaId:int}")]
    public sealed class SaveDownloadStorageSettingsV1 : AdminService<DownloadStorageParametersV1, SaveDownloadStorageDataV1>
    {
        private readonly StorageAccess _storage;

        internal SaveDownloadStorageSettingsV1(StorageAccess storage) => _storage = storage;

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(DownloadStorageParametersV1 parameters) => ModuleRequirement.All("Acquisition");
        protected override ServicePermission GetPermission(DownloadStorageParametersV1 parameters, Type resultType) => new("acquisition.manage");

        protected override async Task<IServiceOutput> OnExecute()
        {
            if (!Data.Confirmed) throw new DataValidationException(nameof(Data.Confirmed), "required");
            var (settings, incomplete, complete) = await Context.ExecuteBatchAsync(async (ctx, ct) =>
                (await ctx.ReadDownloadStorageSettingsAsync(Parameters.DownloadStorageAreaId, ct), await ctx.ReadStorageMountAsync(Data.IncompleteStorageMountId!.Value, ct),
                    await ctx.ReadStorageMountAsync(Data.CompleteStorageMountId!.Value, ct)), CancellationToken);
            if (settings.Version != Data.Version) throw new DataValidationException(nameof(Data.Version), "outdated_version");

            string incompletePath;
            string completePath;
            string incompleteIdentity;
            string completeIdentity;
            try
            {
                if (incomplete.IsReadOnly || complete.IsReadOnly || _storage.Open(incomplete) is not LocalStorageFiles incompleteFiles
                    || _storage.Open(complete) is not LocalStorageFiles completeFiles)
                {
                    throw new ServiceUnavailableException("download_storage_mount_required");
                }

                incompletePath = incompleteFiles.ResolveDirectory(Data.IncompletePath!);
                completePath = completeFiles.ResolveDirectory(Data.CompletePath!);
                var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
                if (incompletePath.Equals(completePath, comparison) || incompletePath.StartsWith(completePath + Path.DirectorySeparatorChar, comparison)
                    || completePath.StartsWith(incompletePath + Path.DirectorySeparatorChar, comparison))
                {
                    throw new ServiceUnavailableException("download_storage_paths_overlap");
                }

                incompleteIdentity = StorageFileSystem.DownloadFilesystemIdentity(incompletePath) ?? throw new ServiceUnavailableException("download_storage_mount_required");
                completeIdentity = StorageFileSystem.DownloadFilesystemIdentity(completePath) ?? throw new ServiceUnavailableException("download_storage_mount_required");
                foreach (var path in new[] { incompletePath, completePath })
                {
                    if (StorageFileSystem.AvailableFreeSpace(path) is not { } available || available < Data.FreeSpaceReserveBytes!.Value)
                    {
                        throw new ServiceUnavailableException("download_storage_reserve");
                    }

                    // Check writes only after explicit confirmation and remove only this probe.
                    var probe = Path.Combine(path, ".jularr-storage-probe-" + Guid.NewGuid().ToString("N"));
                    try
                    {
                        await using var file = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.Asynchronous);
                        await file.WriteAsync(new byte[] { 1 }, CancellationToken);
                    }
                    finally
                    {
                        if (File.Exists(probe)) File.Delete(probe);
                    }
                }
            }
            catch (Exception exception) when (exception is NasMountException or IOException or UnauthorizedAccessException)
            {
                throw new ServiceUnavailableException("download_storage_unavailable");
            }

            await Context.ExecuteBatchAsync(async (ctx, ct) =>
            {
                await ctx.RequireDownloadStorageSettingsAsync(settings, ct);
                // The configuration used for the I/O probe must still be current.
                var currentIncomplete = await ctx.ReadStorageMountAsync(incomplete.Id, ct);
                var currentComplete = await ctx.ReadStorageMountAsync(complete.Id, ct);
                if (currentIncomplete.Version != incomplete.Version || currentComplete.Version != complete.Version)
                {
                    throw new DataValidationException(nameof(Data.Version), "outdated_version");
                }

                await ctx.SaveDownloadStorageSettingsAsync(new DownloadStorageSettingsData(Parameters.DownloadStorageAreaId, incomplete.Id, Data.IncompletePath,
                    incompleteIdentity, complete.Id, Data.CompletePath, completeIdentity, Data.FreeSpaceReserveBytes, Data.MaxJobBytes, Data.MaxWorkspaceBytes,
                    Data.MaxParallelJobs, Data.Version, settings.WorkspaceId), ct);
                return true;
            }, CancellationToken);
            return new UpdateResult(true);
        }
    }
}
