using System.Text.RegularExpressions;
using Jularr.Integrations.Data.Nas;
using Jularr.Integrations.Data.Storage;

namespace Jularr.Integrations.Logic.Storage
{
    internal static class StorageFileSystem
    {
        // Replaced files wait here inside their library; scans pass it by.
        public const string RecycleFolder = ".jularr-recycle";

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

        public static async Task<StorageProbe> ProbeAsync(string path, CancellationToken ct)
        {
            // Hanging mounts time out.
            try
            {
                return await Task.Run(() => Probe(path), ct).WaitAsync(TimeSpan.FromSeconds(5), ct);
            }
            catch (TimeoutException)
            {
                return new StorageProbe(false, "probe_timeout", null, null);
            }
        }

        private static StorageProbe Probe(string path)
        {
            try
            {
                if (!Directory.Exists(path))
                {
                    return new StorageProbe(false, "storage_unavailable", null, null);
                }

                // One entry proves an answer.
                using (var entries = Directory.EnumerateFileSystemEntries(path).GetEnumerator())
                {
                    entries.MoveNext();
                }

                // Windows reports drive roots only.
                try
                {
                    var drive = new DriveInfo(OperatingSystem.IsWindows() ? Path.GetPathRoot(Path.GetFullPath(path))! : path);
                    return drive.IsReady ? new StorageProbe(true, null, drive.AvailableFreeSpace, drive.TotalSize) : new StorageProbe(true, null, null, null);
                }
                catch (ArgumentException)
                {
                    return new StorageProbe(true, null, null, null);
                }
            }
            catch (UnauthorizedAccessException)
            {
                return new StorageProbe(false, "permission_denied", null, null);
            }
            catch (IOException)
            {
                return new StorageProbe(false, "storage_unavailable", null, null);
            }
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

        public static IReadOnlyList<string> ListDirectories(string directory, int limit)
        {
            try
            {
                return new DirectoryInfo(directory).EnumerateDirectories("*", Options)
                    .Select(item => item.Name)
                    .Order(StringComparer.Ordinal)
                    .Take(limit)
                    .ToArray();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw new NasMountException("storage_unavailable");
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
                    if (entry is DirectoryInfo && entry.Name != RecycleFolder)
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
