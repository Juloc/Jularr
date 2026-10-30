using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Storage;
using Jularr.Backend.Data.Fields.Storage;
using Jularr.Backend.Data.Fields.Library;

namespace Jularr.Backend.Data.Admin.Storage.V1
{
    public sealed class DownloadStorageParametersV1 : BaseParameters
    {
        public DownloadStorageParametersV1(DownloadStorageArea downloadStorageAreaId) => DownloadStorageAreaId = downloadStorageAreaId;

        [DataField(typeof(DownloadStorageFields), nameof(DownloadStorageFields.DownloadStorageAreaId))]
        public DownloadStorageArea DownloadStorageAreaId { get; }
    }

    public sealed class SaveDownloadStorageDataV1 : BaseData
    {
        public SaveDownloadStorageDataV1(long? incompleteStorageMountId, string? incompletePath, long? completeStorageMountId, string? completePath, long? freeSpaceReserveBytes,
            long? maxJobBytes, long? maxWorkspaceBytes, int maxParallelJobs, long version, bool confirmed)
        {
            IncompleteStorageMountId = incompleteStorageMountId;
            IncompletePath = incompletePath;
            CompleteStorageMountId = completeStorageMountId;
            CompletePath = completePath;
            FreeSpaceReserveBytes = freeSpaceReserveBytes;
            MaxJobBytes = maxJobBytes;
            MaxWorkspaceBytes = maxWorkspaceBytes;
            MaxParallelJobs = maxParallelJobs;
            Version = version;
            Confirmed = confirmed;
        }

        [DataField(typeof(StorageMountFields), nameof(StorageMountFields.Id), true)]
        public long? IncompleteStorageMountId { get; }

        [DataField(typeof(LibraryRootFields), nameof(LibraryRootFields.RelativePath), true)]
        public string? IncompletePath { get; }

        [DataField(typeof(StorageMountFields), nameof(StorageMountFields.Id), true)]
        public long? CompleteStorageMountId { get; }

        [DataField(typeof(LibraryRootFields), nameof(LibraryRootFields.RelativePath), true)]
        public string? CompletePath { get; }

        [DataField(typeof(DownloadStorageFields), nameof(DownloadStorageFields.FreeSpaceReserveBytes), true)]
        public long? FreeSpaceReserveBytes { get; }

        [DataField(typeof(DownloadStorageFields), nameof(DownloadStorageFields.ConfiguredJobLimitBytes))]
        public long? MaxJobBytes { get; }

        [DataField(typeof(DownloadStorageFields), nameof(DownloadStorageFields.ConfiguredJobLimitBytes))]
        public long? MaxWorkspaceBytes { get; }

        [DataField(typeof(DownloadStorageFields), nameof(DownloadStorageFields.MaxParallelJobs))]
        public int MaxParallelJobs { get; }

        [DataField(typeof(StorageMountFields), nameof(StorageMountFields.Version))]
        public long Version { get; }

        [DataField(typeof(DownloadStorageFields), nameof(DownloadStorageFields.Confirmed))]
        public bool Confirmed { get; }
    }

    public sealed class DownloadStorageSettingsV1 : BaseResponse
    {
        public DownloadStorageSettingsV1(DownloadStorageArea downloadStorageAreaId, long? incompleteStorageMountId, string? incompletePath, string? incompleteFilesystemIdentity,
            long? completeStorageMountId, string? completePath, string? completeFilesystemIdentity, long? freeSpaceReserveBytes, long? maxJobBytes, long? maxWorkspaceBytes,
            int maxParallelJobs, long version, bool isConfigured, long? maxBytesPerSecond = null)
        {
            MaxBytesPerSecond = maxBytesPerSecond;
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
            IsConfigured = isConfigured;
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

        [DataField(typeof(DownloadStorageFields), nameof(DownloadStorageFields.IsConfigured))]
        public bool IsConfigured { get; }

        // The speed limit of Jularr's own downloader of this workspace; none when empty.
        [DataField(typeof(DownloadStorageFields), nameof(DownloadStorageFields.MaxBytesPerSecond), false)]
        public long? MaxBytesPerSecond { get; }
    }
}
