using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Fields.Instance;
using Jularr.Backend.Data.Fields.Library;
using Jularr.Backend.Data.Fields.Storage;

namespace Jularr.Backend.Data.Logic.Library
{
    internal sealed class DeletableFilesQueryData : BaseData
    {
        public DeletableFilesQueryData(long[] storedFileIds)
        {
            StoredFileIds = storedFileIds;
        }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.Ids))]
        public long[] StoredFileIds { get; }
    }

    // A file an administrator chose, or a subtitle beside a chosen media file, with the library it lies in.
    internal sealed class DeletableFileData : BaseData
    {
        public DeletableFileData(long storedFileId, long storageMountId, string rootPath, string relativePath, bool isMissing, bool isMedia)
        {
            StoredFileId = storedFileId;
            StorageMountId = storageMountId;
            RootPath = rootPath;
            RelativePath = relativePath;
            IsMissing = isMissing;
            IsMedia = isMedia;
        }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.Id))]
        public long StoredFileId { get; }

        [DataField(typeof(StorageMountFields), nameof(StorageMountFields.Id))]
        public long StorageMountId { get; }

        [DataField(typeof(LibraryRootFields), nameof(LibraryRootFields.RelativePath))]
        public string RootPath { get; }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.RelativePath))]
        public string RelativePath { get; }

        // A file a scan no longer finds has nothing to delete on disk; only its record goes.
        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.IsMissing))]
        public bool IsMissing { get; }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.IsMedia))]
        public bool IsMedia { get; }
    }

    internal sealed class DeletedFilesData : BaseData
    {
        public DeletedFilesData(string files, bool unmonitorsDeletedUnits)
        {
            Files = files;
            UnmonitorsDeletedUnits = unmonitorsDeletedUnits;
        }

        // JSON rows of StoredFileId; recycled ones add StorageMountId, FolderPath and RecycledPath.
        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.Payload))]
        public string Files { get; }

        [DataField(typeof(InstanceSettingsFields), nameof(InstanceSettingsFields.UnmonitorsDeletedUnits))]
        public bool UnmonitorsDeletedUnits { get; }
    }
}
