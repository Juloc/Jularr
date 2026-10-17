using System.Text.RegularExpressions;
using Jularr.Integrations.Data.Nas;
using Jularr.Integrations.Data.Storage;

namespace Jularr.Integrations.Logic.Storage
{
    // Folders on this host that are no connection: download outputs and the mount check of Docker volumes.
    internal static class StorageFileSystem
    {
        private static readonly EnumerationOptions Options = new()
        {
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint,
            IgnoreInaccessible = true,
            RecurseSubdirectories = false,
            ReturnSpecialDirectories = false
        };

        public static string ResolveDirectory(string connectionPath, string relativePath)
        {
            // Stay below the configured connection.
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(connectionPath));
            var directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(root, relativePath)));
            if (directory != root && !directory.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                throw new NasMountException("storage_unavailable");
            }

            // Reject links below the trusted root.
            var current = new DirectoryInfo(directory);
            while (current.FullName != root)
            {
                if (!current.Exists || current.LinkTarget is not null)
                {
                    throw new NasMountException("storage_unavailable");
                }

                current = current.Parent!;
            }

            if (!current.Exists)
            {
                throw new NasMountException("storage_unavailable");
            }

            return directory;
        }

        public static string ResolveFile(string directory, string relativePath)
        {
            // Files below a resolved directory, never links.
            var root = Path.TrimEndingDirectorySeparator(directory);
            var path = Path.GetFullPath(Path.Combine(root, relativePath));
            if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                throw new NasMountException("storage_unavailable");
            }

            FileSystemInfo current = new FileInfo(path);
            while (current.FullName != root)
            {
                if (!current.Exists || current.LinkTarget is not null)
                {
                    throw new NasMountException("storage_unavailable");
                }

                current = current is FileInfo file ? file.Directory! : ((DirectoryInfo)current).Parent!;
            }

            return path;
        }

        public static bool IsMountPoint(string path)
        {
            // Only Linux hosts mount connections; elsewhere the directory is the connection.
            if (!OperatingSystem.IsLinux())
            {
                return Directory.Exists(path);
            }

            var target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            foreach (var line in File.ReadLines("/proc/self/mountinfo"))
            {
                var fields = line.Split(' ');
                if (fields.Length > 4 && Regex.Replace(fields[4], @"\\([0-7]{3})", match => ((char)Convert.ToInt32(match.Groups[1].Value, 8)).ToString()) == target)
                {
                    return true;
                }
            }

            return false;
        }

        // A separate Linux mount is not sufficient when it is RAM-backed or a container overlay.
        public static bool IsDownloadWorkspaceMount(string path)
        {
            if (!OperatingSystem.IsLinux())
            {
                return false;
            }
            var target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            foreach (var line in File.ReadLines("/proc/self/mountinfo"))
            {
                var fields = line.Split(' ');
                var separator = Array.IndexOf(fields, "-");
                if (fields.Length < 6 || separator < 0 || separator + 1 >= fields.Length)
                {
                    continue;
                }

                var mount = Regex.Replace(fields[4], @"\\([0-7]{3})",
                    match => ((char)Convert.ToInt32(match.Groups[1].Value, 8)).ToString());
                if (!string.Equals(target, mount, StringComparison.Ordinal))
                {
                    continue;
                }

                // A bind mount on an ext4 VM disk can still be non-NAS: this blocks only
                // known unsafe pseudo filesystems. Physical storage identity needs a separate check.
                return fields[separator + 1] is not ("overlay" or "tmpfs" or "ramfs" or "devtmpfs"
                    or "proc" or "sysfs" or "cgroup" or "cgroup2" or "squashfs" or "autofs"
                    or "mqueue" or "securityfs" or "debugfs" or "tracefs" or "fusectl"
                    or "pstore" or "hugetlbfs" or "configfs");
            }

            return false;
        }

        // Disk reservations must inspect the filesystem holding the actual output,
        // not Path.GetPathRoot(output), which would always be "/" on Linux.
        // If mountinfo and DriveInfo disagree, no free-space claim is trustworthy.
        public static long? AvailableFreeSpace(string directory)
        {
            if (!Directory.Exists(directory))
            {
                return null;
            }

            try
            {
                var fullPath = Path.GetFullPath(directory);
                var pathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
                var mountPath = Path.GetPathRoot(fullPath)!;
                if (OperatingSystem.IsLinux())
                {
                    DirectoryInfo? parent = new(fullPath);
                    while (parent is not null && !IsMountPoint(parent.FullName))
                    {
                        parent = parent.Parent;
                    }

                    if (parent is null)
                    {
                        return null;
                    }

                    mountPath = parent.FullName;
                }

                var root = Path.TrimEndingDirectorySeparator(mountPath);
                var drive = DriveInfo.GetDrives().FirstOrDefault(item =>
                    string.Equals(Path.TrimEndingDirectorySeparator(item.RootDirectory.FullName), root, pathComparison));
                return drive is { IsReady: true } ? drive.AvailableFreeSpace : null;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
            {
                return null;
            }
        }

        // Without extensions every file counts.
        public static IEnumerable<StorageFile> EnumerateFiles(string directory, IReadOnlySet<string>? extensions, StorageScanState state, CancellationToken ct)
        {
            var pending = new Stack<string>();
            pending.Push("");
            while (pending.TryPop(out var relative))
            {
                ct.ThrowIfCancellationRequested();
                var current = new DirectoryInfo(relative.Length == 0 ? directory : Path.Combine(directory, relative));
                FileSystemInfo[] entries;
                try
                {
                    entries = current.GetFileSystemInfos("*", Options);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // An unreadable root means no scan; a skipped folder means an incomplete one.
                    if (relative.Length == 0)
                    {
                        throw new NasMountException("storage_unavailable");
                    }

                    state.IsComplete = false;
                    continue;
                }

                foreach (var entry in entries)
                {
                    var path = relative.Length == 0 ? entry.Name : relative + "/" + entry.Name;
                    if (entry is DirectoryInfo && entry.Name != StorageFiles.RecycleFolder)
                    {
                        pending.Push(path);
                    }
                    else if (entry is FileInfo file && (extensions is null || extensions.Contains(file.Extension.ToLowerInvariant())))
                    {
                        yield return new StorageFile(path, file.Length, file.LastWriteTimeUtc);
                    }
                }
            }
        }
    }
}
