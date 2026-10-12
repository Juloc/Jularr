using Jularr.Integrations.Data.Nas;
using Jularr.Integrations.Data.Storage;

namespace Jularr.Integrations.Logic.Storage
{
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

        public static IEnumerable<StorageFile> EnumerateFiles(string directory, IReadOnlySet<string> extensions, CancellationToken ct)
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
                    // An unreadable root means no complete scan.
                    if (relative.Length == 0)
                    {
                        throw new NasMountException("storage_unavailable");
                    }

                    continue;
                }

                foreach (var entry in entries)
                {
                    var path = relative.Length == 0 ? entry.Name : relative + "/" + entry.Name;
                    if (entry is DirectoryInfo)
                    {
                        pending.Push(path);
                    }
                    else if (entry is FileInfo file && extensions.Contains(file.Extension.ToLowerInvariant()))
                    {
                        yield return new StorageFile(path, file.Length, file.LastWriteTimeUtc);
                    }
                }
            }
        }
    }
}
