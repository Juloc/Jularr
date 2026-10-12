using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Storage;
using Jularr.Backend.Data.Fields.Storage;

namespace Jularr.Backend.Data.Admin.Storage.V1
{
    public sealed class ExecuteStorageMountDataV1 : BaseData
    {
        public ExecuteStorageMountDataV1(StorageMountOperationType operationTypeId)
        {
            OperationTypeId = operationTypeId;
        }

        [DataField(typeof(StorageMountOperationFields), nameof(StorageMountOperationFields.OperationTypeId))]
        public StorageMountOperationType OperationTypeId { get; }
    }

    public sealed class RemoveStorageMountDataV1 : BaseData
    {
        public RemoveStorageMountDataV1(long version)
        {
            Version = version;
        }

        [DataField(typeof(StorageMountFields), nameof(StorageMountFields.Version))]
        public long Version { get; }
    }
}
