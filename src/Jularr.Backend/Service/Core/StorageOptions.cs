using System.Globalization;
using Jularr.Backend.Data.Enums.Storage;
using Jularr.Backend.Data.Logic.Storage;
using Jularr.Integrations.Data.Nas;
using Jularr.Integrations.Logic.Storage;

namespace Jularr.Backend.Service.Core
{
    public sealed class StorageOptions
    {
        // Linux helper destinations.
        public string MountRoot { get; set; } = "/mnt/jularr";

        // Staging for files Jularr downloads itself; never a library.
        public string DirectDownloadPath { get; set; } = Path.Combine(Path.GetTempPath(), "jularr", "downloads");

        // Compose paths the Owner may select.
        public string[] LocalPaths { get; set; } = [];

        // A stale empty mount point is never a library.
        public bool RequireMountPoint { get; set; } = true;

        // Libraries rescan after this many hours; 0 turns it off.
        public int ScanIntervalHours { get; set; } = 24;

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

        // Missing permission or an absent connection.
        internal async Task<StorageMountProbeData> ProbeAsync(long storageMountId, StorageProtocolType protocol, string sharePath, CancellationToken ct)
        {
            string path;
            try
            {
                path = GetConnectionPath(protocol, storageMountId, sharePath);
            }
            catch (NasMountException exception)
            {
                return new StorageMountProbeData(storageMountId, StorageAvailabilityType.Offline, exception.Code, null, null);
            }

            var probe = await StorageFileSystem.ProbeAsync(path, ct);
            var availability = probe.IsReadable ? StorageAvailabilityType.Available
                : probe.Code == "permission_denied" ? StorageAvailabilityType.AccessDenied : StorageAvailabilityType.Offline;
            return new StorageMountProbeData(storageMountId, availability, probe.Code, probe.FreeBytes, probe.TotalBytes);
        }
    }
}
