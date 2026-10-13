using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Enums.Operation;
using Jularr.Backend.Data.Fields.Library;
using Jularr.Backend.Data.Fields.Operation;
using Jularr.Backend.Data.Fields.Storage;

namespace Jularr.Backend.Data.Admin.Library.V1
{
    public sealed class ReadLibraryRootsParametersV1 : BaseParameters
    {
        public ReadLibraryRootsParametersV1(long? storageMountId = null, int page = 1, int pageSize = 25)
        {
            StorageMountId = storageMountId;
            Page = page;
            PageSize = pageSize;
        }

        [DataField(typeof(StorageMountFields), nameof(StorageMountFields.Id), false)]
        public long? StorageMountId { get; }

        public int Page { get; }
        public int PageSize { get; }
    }

    public sealed class LibraryRootParametersV1 : BaseParameters
    {
        public LibraryRootParametersV1(long libraryRootId)
        {
            LibraryRootId = libraryRootId;
        }

        [DataField(typeof(LibraryRootFields), nameof(LibraryRootFields.Id))]
        public long LibraryRootId { get; }
    }

    public sealed class DeleteLibraryRootParametersV1 : BaseParameters
    {
        public DeleteLibraryRootParametersV1(long libraryRootId, long version)
        {
            LibraryRootId = libraryRootId;
            Version = version;
        }

        [DataField(typeof(LibraryRootFields), nameof(LibraryRootFields.Id))]
        public long LibraryRootId { get; }

        [DataField(typeof(LibraryRootFields), nameof(LibraryRootFields.Version))]
        public long Version { get; }
    }

    public sealed class CreateLibraryRootParametersV1 : BaseParameters
    {
    }

    public sealed class LibraryRootDetailsV1 : BaseResponse
    {
        public LibraryRootDetailsV1(long id, string name, MediaType mediaTypeId, MediaSubtype? mediaSubtypeId, long storageMountId, string storageMountName, string relativePath, long fileCount, long version)
        {
            Id = id;
            Name = name;
            MediaTypeId = mediaTypeId;
            MediaSubtypeId = mediaSubtypeId;
            StorageMountId = storageMountId;
            StorageMountName = storageMountName;
            RelativePath = relativePath;
            FileCount = fileCount;
            Version = version;
        }

        [DataField(typeof(LibraryRootFields), nameof(LibraryRootFields.Id))]
        public long Id { get; }

        [DataField(typeof(LibraryRootFields), nameof(LibraryRootFields.Name))]
        public string Name { get; }

        [DataField(typeof(LibraryRootFields), nameof(LibraryRootFields.MediaTypeId))]
        public MediaType MediaTypeId { get; }

        [DataField(typeof(LibraryRootFields), nameof(LibraryRootFields.MediaSubtypeId))]
        public MediaSubtype? MediaSubtypeId { get; }

        [DataField(typeof(StorageMountFields), nameof(StorageMountFields.Id))]
        public long StorageMountId { get; }

        [DataField(typeof(StorageMountFields), nameof(StorageMountFields.Name))]
        public string StorageMountName { get; }

        [DataField(typeof(LibraryRootFields), nameof(LibraryRootFields.RelativePath))]
        public string RelativePath { get; }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.FileCount))]
        public long FileCount { get; }

        [DataField(typeof(LibraryRootFields), nameof(LibraryRootFields.Version))]
        public long Version { get; }
    }

    public sealed class CreateLibraryRootDataV1 : BaseData
    {
        public CreateLibraryRootDataV1(string name, MediaType mediaTypeId, long storageMountId, string relativePath, MediaSubtype? mediaSubtypeId = null)
        {
            Name = name;
            MediaTypeId = mediaTypeId;
            MediaSubtypeId = mediaSubtypeId;
            StorageMountId = storageMountId;
            RelativePath = relativePath;
        }

        [DataField(typeof(LibraryRootFields), nameof(LibraryRootFields.Name))]
        public string Name { get; }

        [DataField(typeof(LibraryRootFields), nameof(LibraryRootFields.MediaTypeId))]
        public MediaType MediaTypeId { get; }

        // A subtype library receives that subtype's Works first.
        [DataField(typeof(LibraryRootFields), nameof(LibraryRootFields.MediaSubtypeId))]
        public MediaSubtype? MediaSubtypeId { get; }

        [DataField(typeof(StorageMountFields), nameof(StorageMountFields.Id))]
        public long StorageMountId { get; }

        [DataField(typeof(LibraryRootFields), nameof(LibraryRootFields.RelativePath))]
        public string RelativePath { get; }

        public override void Validate()
        {
            base.Validate();
            if (!MediaSubtypes.Fits(MediaTypeId, MediaSubtypeId))
            {
                throw new DataValidationException(nameof(MediaSubtypeId), "unknown_catalog_value");
            }
        }
    }

    public sealed class RenameLibraryRootDataV1 : BaseData
    {
        public RenameLibraryRootDataV1(string name, long version)
        {
            Name = name;
            Version = version;
        }

        [DataField(typeof(LibraryRootFields), nameof(LibraryRootFields.Name))]
        public string Name { get; }

        [DataField(typeof(LibraryRootFields), nameof(LibraryRootFields.Version))]
        public long Version { get; }
    }

    public sealed class CreateLibraryRootResultV1 : BaseResponse
    {
        public CreateLibraryRootResultV1(long id)
        {
            Id = id;
        }

        [DataField(typeof(LibraryRootFields), nameof(LibraryRootFields.Id))]
        public long Id { get; }
    }

    public sealed class QueueLibraryScanResultV1 : BaseResponse
    {
        public QueueLibraryScanResultV1(long id)
        {
            Id = id;
        }

        [DataField(typeof(OperationFields), nameof(OperationFields.Id))]
        public long Id { get; }
    }
}
