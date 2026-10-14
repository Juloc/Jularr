using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Storage;
using Jularr.Backend.Data.Fields.Instance;
using Jularr.Backend.Data.Fields.Library;
using Jularr.Backend.Data.Fields.Media;
using Jularr.Backend.Data.Fields.Storage;

namespace Jularr.Backend.Data.Logic.Library
{
    // Where replaced files go: inside their library, into one folder of a storage connection, or nowhere.
    internal sealed class ReplacementSettingsData : BaseData
    {
        public ReplacementSettingsData(bool keepsReleases, bool recyclesInLibraries, long? recycleStorageMountId, StorageProtocolType? storageProtocolTypeId, string? sharePath,
            bool? isReadOnly, string? recyclePath, int recycleDays)
        {
            IsReadOnly = isReadOnly;
            KeepsReleases = keepsReleases;
            RecyclesInLibraries = recyclesInLibraries;
            RecycleStorageMountId = recycleStorageMountId;
            StorageProtocolTypeId = storageProtocolTypeId;
            SharePath = sharePath;
            RecyclePath = recyclePath;
            RecycleDays = recycleDays;
        }

        [DataField(typeof(InstanceSettingsFields), nameof(InstanceSettingsFields.KeepsReleases))]
        public bool KeepsReleases { get; }

        [DataField(typeof(InstanceSettingsFields), nameof(InstanceSettingsFields.RecyclesInLibraries))]
        public bool RecyclesInLibraries { get; }

        [DataField(typeof(InstanceSettingsFields), nameof(InstanceSettingsFields.RecycleStorageMountId))]
        public long? RecycleStorageMountId { get; }

        [DataField(typeof(StorageMountFields), nameof(StorageMountFields.StorageProtocolTypeId), false)]
        public StorageProtocolType? StorageProtocolTypeId { get; }

        [DataField(typeof(StorageMountFields), nameof(StorageMountFields.SharePath), false)]
        public string? SharePath { get; }

        [DataField(typeof(StorageMountFields), nameof(StorageMountFields.IsReadOnly), false)]
        public bool? IsReadOnly { get; }

        [DataField(typeof(InstanceSettingsFields), nameof(InstanceSettingsFields.RecyclePath))]
        public string? RecyclePath { get; }

        [DataField(typeof(InstanceSettingsFields), nameof(InstanceSettingsFields.RecycleDays))]
        public int RecycleDays { get; }
    }

    internal sealed class ReplacementSettingsSaveData : BaseData
    {
        public ReplacementSettingsSaveData(bool keepsReleases, bool recyclesInLibraries, long? recycleStorageMountId, string? recyclePath, int recycleDays, long version)
        {
            KeepsReleases = keepsReleases;
            RecyclesInLibraries = recyclesInLibraries;
            RecycleStorageMountId = recycleStorageMountId;
            RecyclePath = recyclePath;
            RecycleDays = recycleDays;
            Version = version;
        }

        [DataField(typeof(InstanceSettingsFields), nameof(InstanceSettingsFields.KeepsReleases))]
        public bool KeepsReleases { get; }

        [DataField(typeof(InstanceSettingsFields), nameof(InstanceSettingsFields.RecyclesInLibraries))]
        public bool RecyclesInLibraries { get; }

        [DataField(typeof(InstanceSettingsFields), nameof(InstanceSettingsFields.RecycleStorageMountId))]
        public long? RecycleStorageMountId { get; }

        [DataField(typeof(InstanceSettingsFields), nameof(InstanceSettingsFields.RecyclePath))]
        public string? RecyclePath { get; }

        [DataField(typeof(InstanceSettingsFields), nameof(InstanceSettingsFields.RecycleDays))]
        public int RecycleDays { get; }

        [DataField(typeof(InstanceSettingsFields), nameof(InstanceSettingsFields.Version))]
        public long Version { get; }
    }

    internal sealed class ReplacingAssetsData : BaseData
    {
        public ReplacingAssetsData(long[] mediaAssetIds)
        {
            MediaAssetIds = mediaAssetIds;
        }

        // The assets an import just created.
        [DataField(typeof(MediaAssetFields), nameof(MediaAssetFields.Ids))]
        public long[] MediaAssetIds { get; }
    }

    // A file of an earlier asset of the same film, episode or volume, with the library it lies in.
    internal sealed class ReplacedFileData : BaseData
    {
        public ReplacedFileData(long newMediaAssetId, long storedFileId, long storageMountId, StorageProtocolType storageProtocolTypeId, string sharePath, string rootPath,
            string relativePath)
        {
            NewMediaAssetId = newMediaAssetId;
            StoredFileId = storedFileId;
            StorageMountId = storageMountId;
            StorageProtocolTypeId = storageProtocolTypeId;
            SharePath = sharePath;
            RootPath = rootPath;
            RelativePath = relativePath;
        }

        [DataField(typeof(MediaAssetFields), nameof(MediaAssetFields.Id))]
        public long NewMediaAssetId { get; }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.Id))]
        public long StoredFileId { get; }

        [DataField(typeof(StorageMountFields), nameof(StorageMountFields.Id))]
        public long StorageMountId { get; }

        [DataField(typeof(StorageMountFields), nameof(StorageMountFields.StorageProtocolTypeId))]
        public StorageProtocolType StorageProtocolTypeId { get; }

        [DataField(typeof(StorageMountFields), nameof(StorageMountFields.SharePath))]
        public string SharePath { get; }

        [DataField(typeof(LibraryRootFields), nameof(LibraryRootFields.RelativePath))]
        public string RootPath { get; }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.RelativePath))]
        public string RelativePath { get; }
    }

    internal sealed class RetiredFilesData : BaseData
    {
        public RetiredFilesData(string files)
        {
            Files = files;
        }

        // JSON rows of StoredFileId and NewMediaAssetId: files gone from the library whose history moves to the new asset.
        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.Payload))]
        public string Files { get; }
    }

    // A library folder holding a recycle folder.
    internal sealed class RecycleLibraryData : BaseData
    {
        public RecycleLibraryData(long storageMountId, StorageProtocolType storageProtocolTypeId, string sharePath, string rootPath)
        {
            StorageMountId = storageMountId;
            StorageProtocolTypeId = storageProtocolTypeId;
            SharePath = sharePath;
            RootPath = rootPath;
        }

        [DataField(typeof(StorageMountFields), nameof(StorageMountFields.Id))]
        public long StorageMountId { get; }

        [DataField(typeof(StorageMountFields), nameof(StorageMountFields.StorageProtocolTypeId))]
        public StorageProtocolType StorageProtocolTypeId { get; }

        [DataField(typeof(StorageMountFields), nameof(StorageMountFields.SharePath))]
        public string SharePath { get; }

        [DataField(typeof(LibraryRootFields), nameof(LibraryRootFields.RelativePath))]
        public string RootPath { get; }
    }
}
