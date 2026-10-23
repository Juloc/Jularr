using System.Text;
using Jularr.Backend.Data.Enums.Storage;
using Jularr.Backend.Data.Logic.Storage;
using Jularr.Integrations.Data.Nas;
using Jularr.Integrations.Data.Storage;
using Jularr.Integrations.Logic.Storage;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;

namespace Jularr.Backend.Service.Core
{
    // Opens a connection's files: a local path or a Docker volume as a folder on this host, SMB without a volume through Jularr's own session.
    internal sealed class StorageAccess(IOptions<StorageOptions> options, SmbShares shares, StorageStreamServer streams, IDataProtectionProvider protection)
    {
        private readonly IDataProtector _protector = protection.CreateProtector(StoragePasswords.ProtectorPurpose);

        // Throws NasMountException when a volume is not mounted; a direct share connects on its first request.
        public StorageFiles Open(StorageMountData mount)
        {
            if (mount.StorageProtocolTypeId != StorageProtocolType.Smb || mount.IsVolume)
            {
                return new LocalStorageFiles(options.Value.GetConnectionPath(mount.StorageProtocolTypeId, mount.Id, mount.SharePath));
            }

            // The share path names the share, then a folder inside it.
            var parts = mount.SharePath.Trim('/').Split('/', 2);
            var address = new SmbAddress(mount.Host!, parts[0], mount.Domain, mount.Username ?? "",
                mount.ProtectedPassword is { } password ? Encoding.UTF8.GetString(_protector.Unprotect(password)) : "");
            return new SmbStorageFiles(shares.Get(mount.Id, mount.Version, () => SmbShare.Connect(address)), parts.Length > 1 ? parts[1] : "");
        }

        // Where FFmpeg, ffprobe and fpcalc read a file; dispose once the tool is done.
        public StorageInput Input(StorageFiles files, string path) => streams.Open(files, path);

        // Missing permission or an absent connection; never throws for storage that is out of reach.
        public async Task<StorageMountProbeData> ProbeAsync(StorageMountData mount, CancellationToken ct)
        {
            StorageProbe probe;
            try
            {
                probe = await Open(mount).ProbeAsync(ct);
            }
            catch (NasMountException exception)
            {
                probe = new StorageProbe(false, exception.Code, null, null);
            }

            var availability = probe.IsReadable ? StorageAvailabilityType.Available
                : probe.Code == "permission_denied" ? StorageAvailabilityType.AccessDenied : StorageAvailabilityType.Offline;
            return new StorageMountProbeData(mount.Id, availability, probe.Code, probe.FreeBytes, probe.TotalBytes);
        }
    }
}
