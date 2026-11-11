using System.Collections.Concurrent;
using System.Text;
using Jularr.Backend.Data.Enums.Storage;
using Jularr.Backend.Data.Logic.Storage;
using Jularr.Integrations.Data.Nas;
using Jularr.Integrations.Data.Storage;
using Jularr.Integrations.Logic.Storage;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Jularr.Backend.Service.Core
{
    // Opens a connection's files: a local path or a Docker volume as a folder on this host, SMB without a volume through Jularr's own session.
    // Storage may sleep or vanish, so no service reaches it while it holds its SQL transaction (ExternalCallGuard): a read service ends its read first.
    // Every call passes the connection's StorageGate; a web request waits two seconds at most for a free place, background work longer.
    internal sealed class StorageAccess(IOptions<StorageOptions> options, SmbShares shares, StorageStreamServer streams, IDataProtectionProvider protection,
        IHostEnvironment? environment = null, ILogger<StorageAccess>? logger = null, IHttpContextAccessor? http = null, TimeProvider? time = null)
    {
        private readonly IDataProtector _protector = protection.CreateProtector(StoragePasswords.ProtectorPurpose);
        private readonly bool _strict = ExternalCallGuard.IsStrict(environment);
        private readonly ILogger _logger = (ILogger?)logger ?? NullLogger.Instance;
        private readonly ConcurrentDictionary<long, StorageGate> _gates = new();

        // How long one storage call may take before the connection counts as hanging; tests shorten it.
        internal TimeSpan CallTime { get; set; } = StorageGate.CallTime;

        // Throws NasMountException when a volume is not mounted; a direct share connects on its first request.
        public StorageFiles Open(StorageMountData mount)
        {
            ExternalCallGuard.Check(_strict, _logger, "storage_in_transaction", "opens storage " + mount.Name);
            var gate = _gates.GetOrAdd(mount.Id, _ => new StorageGate(time ?? TimeProvider.System, CallTime));
            var interactive = http?.HttpContext is not null;
            if (mount.StorageProtocolTypeId != StorageProtocolType.Smb || mount.IsVolume)
            {
                return new LocalStorageFiles(options.Value.GetConnectionPath(mount.StorageProtocolTypeId, mount.Id, mount.SharePath)) { Gate = gate, Interactive = interactive };
            }

            // The share path names the share, then a folder inside it.
            var parts = mount.SharePath.Trim('/').Split('/', 2);
            var address = new SmbAddress(mount.Host!, parts[0], mount.Domain, mount.Username ?? "",
                mount.ProtectedPassword is { } password ? Encoding.UTF8.GetString(_protector.Unprotect(password)) : "");
            return new SmbStorageFiles(shares.Get(mount.Id, mount.Version, () => SmbShare.Connect(address)), parts.Length > 1 ? parts[1] : "")
            {
                Gate = gate,
                Interactive = interactive
            };
        }

        // Where FFmpeg, ffprobe and fpcalc read a file; dispose once the tool is done.
        public StorageInput Input(StorageFiles files, string path)
        {
            ExternalCallGuard.Check(_strict, _logger, "storage_in_transaction", "hands a tool a storage file");
            return streams.Open(files, path);
        }

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
                probe = new StorageProbe(false, exception.Code == NasMountException.AccessDenied ? "permission_denied" : exception.Code, null, null);
            }

            var availability = probe.IsReadable ? StorageAvailabilityType.Available
                : probe.Code == "permission_denied" ? StorageAvailabilityType.AccessDenied : StorageAvailabilityType.Offline;
            return new StorageMountProbeData(mount.Id, availability, probe.Code, probe.FreeBytes, probe.TotalBytes);
        }
    }
}
