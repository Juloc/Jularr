using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Operation;
using Jularr.Backend.Data.Fields.Operation;

namespace Jularr.Backend.Data.System.Operation.V1
{
    public sealed class OperationQueueParametersV1 : BaseParameters
    {
    }

    // The queue of one kind of work: scans and backups are claimed apart, so a long scan never holds up a backup.
    public sealed class ClaimOperationParametersV1 : BaseParameters
    {
        public ClaimOperationParametersV1(OperationType operationTypeId)
        {
            OperationTypeId = operationTypeId;
        }

        [DataField(typeof(OperationFields), nameof(OperationFields.OperationTypeId))]
        public OperationType OperationTypeId { get; }
    }

    public sealed class LibraryScanScheduleParametersV1 : BaseParameters
    {
    }

    public sealed class PruneOperationsParametersV1 : BaseParameters
    {
    }

    public sealed class OperationParametersV1 : BaseParameters
    {
        public OperationParametersV1(long operationId)
        {
            OperationId = operationId;
        }

        [DataField(typeof(OperationFields), nameof(OperationFields.Id))]
        public long OperationId { get; }
    }

    public sealed class ClaimOperationResultV1 : BaseResponse
    {
        public ClaimOperationResultV1(long? operationId, OperationType? operationTypeId)
        {
            OperationId = operationId;
            OperationTypeId = operationTypeId;
        }

        [DataField(typeof(OperationFields), nameof(OperationFields.Id), false)]
        public long? OperationId { get; }

        [DataField(typeof(OperationFields), nameof(OperationFields.OperationTypeId), false)]
        public OperationType? OperationTypeId { get; }
    }

    public sealed class FailOperationDataV1 : BaseData
    {
        public FailOperationDataV1(string errorKey)
        {
            ErrorKey = errorKey;
        }

        [DataField(typeof(OperationFields), nameof(OperationFields.ErrorKey), true)]
        public string ErrorKey { get; }
    }
}
