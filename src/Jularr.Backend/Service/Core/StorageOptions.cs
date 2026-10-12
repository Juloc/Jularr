using System.Globalization;
using Jularr.Backend.Data.Enums.Storage;
using Jularr.Integrations.Data.Nas;
using Jularr.Integrations.Logic.Storage;

namespace Jularr.Backend.Service.Core
{
    public sealed class StorageOptions
    {
        // Linux helper destinations.
        public string MountRoot { get; set; } = "/mnt/jularr";

        // Compose paths the Owner may select.
        public string[] LocalPaths { get; set; } = [];

        // A stale empty mount point is never a library.
        public bool RequireMountPoint { get; set; } = true;

        internal string GetConnectionPath(StorageProtocolType protocol, long storageMountId, string sharePath)
        {
            if (protocol == StorageProtocolType.Local)
            {
                return sharePath;
            }

            var path = Path.Combine(MountRoot, storageMountId.ToString(CultureInfo.InvariantCulture));
            if (RequireMountPoint && !StorageFileSystem.IsMountPoint(path))
            {
                throw new NasMountException("storage_unavailable");
            }

            return path;
        }
    }
}
