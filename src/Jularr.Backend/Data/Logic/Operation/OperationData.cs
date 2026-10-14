using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Operation;
using Jularr.Backend.Data.Fields.Account;
using Jularr.Backend.Data.Fields.Library;
using Jularr.Backend.Data.Fields.Operation;

namespace Jularr.Backend.Data.Logic.Operation
{
    internal sealed class OperationCreationData : BaseData
    {
        public OperationCreationData(OperationType operationTypeId, long? libraryRootId, long? requestedByAccountId)
        {
            OperationTypeId = operationTypeId;
            LibraryRootId = libraryRootId;
            RequestedByAccountId = requestedByAccountId;
        }

        [DataField(typeof(OperationFields), nameof(OperationFields.OperationTypeId))]
        public OperationType OperationTypeId { get; }

        [DataField(typeof(LibraryRootFields), nameof(LibraryRootFields.Id), false)]
        public long? LibraryRootId { get; }

        [DataField(typeof(AccountFields), nameof(AccountFields.Id), false)]
        public long? RequestedByAccountId { get; }
    }

    internal sealed class OperationData : BaseData
    {
        public OperationData(long id, OperationType operationTypeId, long? libraryRootId)
        {
            Id = id;
            OperationTypeId = operationTypeId;
            LibraryRootId = libraryRootId;
        }

        [DataField(typeof(OperationFields), nameof(OperationFields.Id))]
        public long Id { get; }

        [DataField(typeof(OperationFields), nameof(OperationFields.OperationTypeId))]
        public OperationType OperationTypeId { get; }

        [DataField(typeof(LibraryRootFields), nameof(LibraryRootFields.Id), false)]
        public long? LibraryRootId { get; }
    }

    internal sealed class OperationProgressData : BaseData
    {
        public OperationProgressData(long id, long processedCount, OperationStatusType operationStatusTypeId, string? errorKey = null)
        {
            Id = id;
            ProcessedCount = processedCount;
            OperationStatusTypeId = operationStatusTypeId;
            ErrorKey = errorKey;
        }

        [DataField(typeof(OperationFields), nameof(OperationFields.Id))]
        public long Id { get; }

        [DataField(typeof(OperationFields), nameof(OperationFields.ProcessedCount))]
        public long ProcessedCount { get; }

        [DataField(typeof(OperationFields), nameof(OperationFields.OperationStatusTypeId))]
        public OperationStatusType OperationStatusTypeId { get; }

        [DataField(typeof(OperationFields), nameof(OperationFields.ErrorKey))]
        public string? ErrorKey { get; }
    }

    // Operations created after this point count as recent.
    internal sealed class OperationSinceData : BaseData
    {
        public OperationSinceData(DateTime since)
        {
            Since = since;
        }

        [DataField(typeof(OperationFields), nameof(OperationFields.CreatedAt))]
        public DateTime Since { get; }
    }
}
