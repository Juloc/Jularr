using System.Globalization;
using Jularr.Backend.Data.Enums.Storage;
using Jularr.Integrations.Data.Nas;
using Jularr.Integrations.Logic.Storage;

namespace Jularr.Backend.Service.Core
{
    public sealed class StorageOptions
    {
        // Where Docker mounts NAS volumes, one folder per connection.
        public string MountRoot { get; set; } = "/mnt/jularr";

        // Large native Usenet/direct-source downloads require an explicitly chosen persistent workspace.
        // Never silently write them to the container overlay or the VM's /tmp.
        public string? DirectDownloadPath { get; set; }

        // Deliberate opt-in for workspaces on the host filesystem rather than a Docker/NAS mount.
        // Host limits and reservations are a separate required release gate (#1209).
        public bool AllowDirectDownloadOnHostFilesystem { get; set; }

        internal string? ReadyDirectDownloadPath()
        {
            if (string.IsNullOrWhiteSpace(DirectDownloadPath) || !Path.IsPathFullyQualified(DirectDownloadPath))
            {
                return null;
            }

            DirectoryInfo directory;
            try
            {
                directory = new DirectoryInfo(Path.GetFullPath(DirectDownloadPath));
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
            {
                return null;
            }

            if (!directory.Exists || directory.Parent is null)
            {
                return null;
            }

            // A symlinked workspace can bypass an otherwise valid mount and end up on the VM.
            for (DirectoryInfo? current = directory; current is not null; current = current.Parent)
            {
                if (current.LinkTarget is not null)
                {
                    return null;
                }
            }

            if (AllowDirectDownloadOnHostFilesystem)
            {
                return directory.FullName;
            }

            if (!OperatingSystem.IsLinux())
            {
                return null;
            }

            // The workspace may be a subfolder of a real Docker bind/NAS mount, but the
            // container's own root mount is never enough to enable native downloads.
            try
            {
                for (var current = directory; current.Parent is not null; current = current.Parent)
                {
                    if (StorageFileSystem.IsMountPoint(current.FullName))
                    {
                        return directory.FullName;
                    }
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return null;
            }

            return null;
        }

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
