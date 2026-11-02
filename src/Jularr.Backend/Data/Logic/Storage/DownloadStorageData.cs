using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Storage;
using Jularr.Backend.Data.Fields.Storage;
using Jularr.Backend.Data.Fields.Library;

namespace Jularr.Backend.Data.Logic.Storage
{
    internal sealed class DownloadStorageAreaData(DownloadStorageArea downloadStorageAreaId) : BaseData
    {
        [DataField(typeof(DownloadStorageFields), nameof(DownloadStorageFields.DownloadStorageAreaId))]
        public DownloadStorageArea DownloadStorageAreaId { get; } = downloadStorageAreaId;
    }

    // The speed limit of Jularr's own downloader of one workspace; none when empty.
    internal sealed class DownloadSpeedLimitData : BaseData
    {
        public DownloadSpeedLimitData(DownloadStorageArea downloadStorageAreaId, long? maxBytesPerSecond)
        {
            DownloadStorageAreaId = downloadStorageAreaId;
            MaxBytesPerSecond = maxBytesPerSecond;
        }

        [DataField(typeof(DownloadStorageFields), nameof(DownloadStorageFields.DownloadStorageAreaId))]
        public DownloadStorageArea DownloadStorageAreaId { get; }

        [DataField(typeof(DownloadStorageFields), nameof(DownloadStorageFields.MaxBytesPerSecond), false)]
        public long? MaxBytesPerSecond { get; }
    }

    internal sealed class DownloadStorageSettingsData : BaseData
    {
        public DownloadStorageSettingsData(DownloadStorageArea downloadStorageAreaId, long? incompleteStorageMountId, string? incompletePath, string? incompleteFilesystemIdentity,
            long? completeStorageMountId, string? completePath, string? completeFilesystemIdentity, long? freeSpaceReserveBytes, long? maxJobBytes, long? maxWorkspaceBytes,
            int maxParallelJobs, long version, Guid workspaceId, long? maxBytesPerSecond = null)
        {
            MaxBytesPerSecond = maxBytesPerSecond;
            WorkspaceId = workspaceId;
            DownloadStorageAreaId = downloadStorageAreaId;
            IncompleteStorageMountId = incompleteStorageMountId;
            IncompletePath = incompletePath;
            IncompleteFilesystemIdentity = incompleteFilesystemIdentity;
            CompleteStorageMountId = completeStorageMountId;
            CompletePath = completePath;
            CompleteFilesystemIdentity = completeFilesystemIdentity;
            FreeSpaceReserveBytes = freeSpaceReserveBytes;
            MaxJobBytes = maxJobBytes;
            MaxWorkspaceBytes = maxWorkspaceBytes;
            MaxParallelJobs = maxParallelJobs;
            Version = version;
        }

        [DataField(typeof(DownloadStorageFields), nameof(DownloadStorageFields.DownloadStorageAreaId))]
        public DownloadStorageArea DownloadStorageAreaId { get; }

        [DataField(typeof(StorageMountFields), nameof(StorageMountFields.Id), false)]
        public long? IncompleteStorageMountId { get; }

        [DataField(typeof(LibraryRootFields), nameof(LibraryRootFields.RelativePath), false)]
        public string? IncompletePath { get; }

        [DataField(typeof(DownloadStorageFields), nameof(DownloadStorageFields.FilesystemIdentity), false)]
        public string? IncompleteFilesystemIdentity { get; }

        [DataField(typeof(StorageMountFields), nameof(StorageMountFields.Id), false)]
        public long? CompleteStorageMountId { get; }

        [DataField(typeof(LibraryRootFields), nameof(LibraryRootFields.RelativePath), false)]
        public string? CompletePath { get; }

        [DataField(typeof(DownloadStorageFields), nameof(DownloadStorageFields.FilesystemIdentity), false)]
        public string? CompleteFilesystemIdentity { get; }

        [DataField(typeof(DownloadStorageFields), nameof(DownloadStorageFields.FreeSpaceReserveBytes), false)]
        public long? FreeSpaceReserveBytes { get; }

        [DataField(typeof(DownloadStorageFields), nameof(DownloadStorageFields.ConfiguredJobLimitBytes))]
        public long? MaxJobBytes { get; }

        [DataField(typeof(DownloadStorageFields), nameof(DownloadStorageFields.ConfiguredJobLimitBytes))]
        public long? MaxWorkspaceBytes { get; }

        [DataField(typeof(DownloadStorageFields), nameof(DownloadStorageFields.MaxParallelJobs))]
        public int MaxParallelJobs { get; }

        [DataField(typeof(StorageMountFields), nameof(StorageMountFields.Version))]
        public long Version { get; }

        [DataField(typeof(DownloadStorageFields), nameof(DownloadStorageFields.WorkspaceId))]
        public Guid WorkspaceId { get; }

        // The speed limit of Jularr's own downloader of this workspace; none when empty.
        [DataField(typeof(DownloadStorageFields), nameof(DownloadStorageFields.MaxBytesPerSecond), false)]
        public long? MaxBytesPerSecond { get; }
    }
}
