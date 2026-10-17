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

        // Explicit shared workspace; independent downloader paths take precedence.
        public string? DirectDownloadPath { get; set; }

        // Independent workspaces override the shared path for each native downloader.
        public string? UsenetDownloadPath { get; set; }
        public string? DirectSourceDownloadPath { get; set; }

        // Deliberate opt-in for workspaces on the host filesystem rather than a Docker/NAS mount.
        public bool AllowDirectDownloadOnHostFilesystem { get; set; }

        internal string? ReadyUsenetDownloadPath() => ReadyDownloadPath(UsenetDownloadPath ?? DirectDownloadPath);
        internal string? ReadyDirectSourceDownloadPath() => ReadyDownloadPath(DirectSourceDownloadPath ?? DirectDownloadPath);

        private string? ReadyDownloadPath(string? configured)
        {
            if (string.IsNullOrWhiteSpace(configured) || !Path.IsPathFullyQualified(configured))
            {
                return null;
            }

            try
            {
                var directory = new DirectoryInfo(Path.GetFullPath(configured));
                if (!directory.Exists || directory.Parent is null)
                {
                    return null;
                }

                // Reject links even when their targets do not exist.
                for (DirectoryInfo? current = directory; current is not null; current = current.Parent)
                {
                    if (current.LinkTarget is not null)
                    {
                        return null;
                    }
                }

                if (!OperatingSystem.IsLinux())
                {
                    return AllowDirectDownloadOnHostFilesystem ? directory.FullName : null;
                }

                // Only the nearest mount contains the workspace. An unsafe child mount
                // cannot inherit approval from a persistent ancestor.
                for (var current = directory; current is not null; current = current.Parent)
                {
                    if (StorageFileSystem.IsMountPoint(current.FullName))
                    {
                        return StorageFileSystem.IsDownloadWorkspaceMount(current.FullName)
                            && (current.Parent is not null || AllowDirectDownloadOnHostFilesystem) ? directory.FullName : null;
                    }
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
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
