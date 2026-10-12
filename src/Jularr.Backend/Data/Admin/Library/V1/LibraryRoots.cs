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
        public LibraryRootDetailsV1(long id, string name, MediaType mediaTypeId, long storageMountId, string storageMountName, string relativePath, long fileCount, long version)
        {
            Id = id;
            Name = name;
            MediaTypeId = mediaTypeId;
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
        public CreateLibraryRootDataV1(string name, MediaType mediaTypeId, long storageMountId, string relativePath)
        {
            Name = name;
            MediaTypeId = mediaTypeId;
            StorageMountId = storageMountId;
            RelativePath = relativePath;
        }

        [DataField(typeof(LibraryRootFields), nameof(LibraryRootFields.Name))]
        public string Name { get; }

        [DataField(typeof(LibraryRootFields), nameof(LibraryRootFields.MediaTypeId))]
        public MediaType MediaTypeId { get; }

        [DataField(typeof(StorageMountFields), nameof(StorageMountFields.Id))]
        public long StorageMountId { get; }

        [DataField(typeof(LibraryRootFields), nameof(LibraryRootFields.RelativePath))]
        public string RelativePath { get; }
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

    public sealed class ReadOperationsParametersV1 : BaseParameters
    {
        public ReadOperationsParametersV1(int page = 1, int pageSize = 25)
        {
            Page = page;
            PageSize = pageSize;
        }

        public int Page { get; }
        public int PageSize { get; }
    }

    public sealed class OperationDetailsV1 : BaseResponse
    {
        public OperationDetailsV1(long id, OperationType operationTypeId, OperationStatusType operationStatusTypeId, long? libraryRootId, string? libraryRootName, long processedCount,
            string? errorKey, DateTime createdAt, DateTime? startedAt, DateTime? finishedAt)
        {
            Id = id;
            OperationTypeId = operationTypeId;
            OperationStatusTypeId = operationStatusTypeId;
            LibraryRootId = libraryRootId;
            LibraryRootName = libraryRootName;
            ProcessedCount = processedCount;
            ErrorKey = errorKey;
            CreatedAt = createdAt;
            StartedAt = startedAt;
            FinishedAt = finishedAt;
        }

        [DataField(typeof(OperationFields), nameof(OperationFields.Id))]
        public long Id { get; }

        [DataField(typeof(OperationFields), nameof(OperationFields.OperationTypeId))]
        public OperationType OperationTypeId { get; }

        [DataField(typeof(OperationFields), nameof(OperationFields.OperationStatusTypeId))]
        public OperationStatusType OperationStatusTypeId { get; }

        [DataField(typeof(LibraryRootFields), nameof(LibraryRootFields.Id), false)]
        public long? LibraryRootId { get; }

        [DataField(typeof(LibraryRootFields), nameof(LibraryRootFields.Name), false)]
        public string? LibraryRootName { get; }

        [DataField(typeof(OperationFields), nameof(OperationFields.ProcessedCount))]
        public long ProcessedCount { get; }

        [DataField(typeof(OperationFields), nameof(OperationFields.ErrorKey))]
        public string? ErrorKey { get; }

        [DataField(typeof(OperationFields), nameof(OperationFields.CreatedAt))]
        public DateTime CreatedAt { get; }

        [DataField(typeof(OperationFields), nameof(OperationFields.StartedAt))]
        public DateTime? StartedAt { get; }

        [DataField(typeof(OperationFields), nameof(OperationFields.FinishedAt))]
        public DateTime? FinishedAt { get; }
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
