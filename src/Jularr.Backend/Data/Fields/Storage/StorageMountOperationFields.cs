using Jularr.Backend.Data.Enums.Storage;

namespace Jularr.Backend.Data.Fields.Storage
{
    public static class StorageMountOperationFields
    {
        public static readonly EnumField OperationTypeId = new(typeof(StorageMountOperationType));
    }
}
