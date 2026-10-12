using System.Globalization;
using Jularr.Backend.Data.Enums.Storage;

namespace Jularr.Backend.Service.Core
{
    public sealed class StorageOptions
    {
        // Linux helper destinations.
        public string MountRoot { get; set; } = "/mnt/jularr";

        // Compose paths the Owner may select.
        public string[] LocalPaths { get; set; } = [];

        internal string GetConnectionPath(StorageProtocolType protocol, long storageMountId, string sharePath) =>
            protocol == StorageProtocolType.Local ? sharePath : Path.Combine(MountRoot, storageMountId.ToString(CultureInfo.InvariantCulture));
    }
}
