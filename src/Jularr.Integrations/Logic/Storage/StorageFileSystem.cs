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

        private static readonly EnumerationOptions DownloadOptions = new()
        {
            AttributesToSkip = FileAttributes.ReparsePoint,
            IgnoreInaccessible = false,
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

        // Stable across remounts, but different when a missing NAS reveals its local parent.
        public static string? DownloadFilesystemIdentity(string directory, bool includeDirectory = true, bool includeMountPoint = false)
        {
            if (!Directory.Exists(directory)) return null;
            var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
            for (var current = new DirectoryInfo(path); current is not null; current = current.Parent)
            {
                if (current.LinkTarget is not null) return null;
            }
            if (!OperatingSystem.IsLinux())
            {
                var drive = new DriveInfo(Path.GetPathRoot(path)!);
                return drive.IsReady ? drive.DriveFormat + ":" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(drive.Name + (includeDirectory ? "\n" + path : "")))) : null;
            }

            string? identity = null;
            var longest = -1;
            foreach (var line in File.ReadLines("/proc/self/mountinfo"))
            {
                var fields = line.Split(' ');
                var separator = Array.IndexOf(fields, "-");
                if (fields.Length < 6 || separator < 0 || separator + 2 >= fields.Length) continue;
                var mount = Regex.Replace(fields[4], @"\\([0-7]{3})", match => ((char)Convert.ToInt32(match.Groups[1].Value, 8)).ToString());
                if (mount.Length <= longest || path != mount && !path.StartsWith(mount == "/" ? "/" : mount + "/", StringComparison.Ordinal)) continue;
                longest = mount.Length;
                identity = IsDownloadWorkspaceMount(mount)
                    ? fields[separator + 1] + ":" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                        System.Text.Encoding.UTF8.GetBytes(fields[separator + 2] + (includeMountPoint ? "\n" + fields[4] + "\n" + fields[3] : "")
                            + (includeDirectory ? "\n" + fields[3] + "\n" + path : ""))))
                    : null;
            }

            return identity;
        }

        // Disk reservations must inspect the filesystem holding the actual output,
        // not Path.GetPathRoot(output), which would always be "/" on Linux.
        // A mount can be visible via mountinfo without being enumerated by DriveInfo.GetDrives
        // (notably CIFS/NFS bind mounts propagated into Docker). Check the actual mount path instead.
        public static long? AvailableFreeSpace(string directory)
        {
            if (!Directory.Exists(directory))
            {
                return null;
            }

            try
            {
                var fullPath = Path.GetFullPath(directory);
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

                // DriveInfo.GetDrives() can omit propagated network bind mounts in containers.
                // Mountpoint resolution above is based on this process's mountinfo and avoids
                // accidentally measuring '/' while the NAS is unmounted.
                var drive = new DriveInfo(mountPath);
                return drive.IsReady ? drive.AvailableFreeSpace : null;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
            {
                return null;
            }
        }

        // Without extensions every file counts.
        public static IEnumerable<StorageFile> EnumerateFiles(string directory, IReadOnlySet<string>? extensions, StorageScanState state, CancellationToken ct, bool includeHidden = false)
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
                    entries = current.GetFileSystemInfos("*", includeHidden ? DownloadOptions : Options);
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
                        if (includeHidden) file.Refresh();
                        yield return new StorageFile(path, file.Length, file.LastWriteTimeUtc);
                    }
                }
            }
        }
    }
}
