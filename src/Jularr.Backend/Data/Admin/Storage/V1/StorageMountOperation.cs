using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Fields.Storage;

namespace Jularr.Backend.Data.Admin.Storage.V1
{
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
