using Jularr.Integrations.Data.Nas;
using Jularr.Integrations.Data.Storage;

namespace Jularr.Integrations.Logic.Storage
{
    // A connection that is a folder on this host: a local path or a Docker volume. Links below it are never followed.
    internal sealed class LocalStorageFiles(string root) : StorageFiles
    {
        private static readonly EnumerationOptions Options = new()
        {
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint,
            IgnoreInaccessible = true,
            RecurseSubdirectories = false,
            ReturnSpecialDirectories = false
        };

        private readonly string _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));

        // The host path of an existing file, for tools that read files themselves.
        public string ResolveFile(string path)
        {
            var full = Full(path);
            if (!File.Exists(full) || !IsInside(full))
            {
                throw new NasMountException("storage_unavailable");
            }

            return full;
        }

        // The host path of an existing folder.
        public string ResolveDirectory(string path)
        {
            var full = Full(path);
            if (!Directory.Exists(full) || !IsInside(full))
            {
                throw new NasMountException("storage_unavailable");
            }

            return full;
        }

        public override Task<StorageEntry?> GetAsync(string path, CancellationToken ct) => Task.Run(() =>
        {
            var full = Full(path);
            FileSystemInfo info = Directory.Exists(full) ? new DirectoryInfo(full) : new FileInfo(full);
            if (!info.Exists)
            {
                RequireRoot();
                return null;
            }

            return IsInside(full) ? Entry(info) : null;
        }, ct);

        public override Task<IReadOnlyList<StorageEntry>> ListAsync(string directory, CancellationToken ct) => Task.Run<IReadOnlyList<StorageEntry>>(() =>
        {
            var full = ResolveDirectory(directory);
            return new DirectoryInfo(full).GetFileSystemInfos("*", Options).Select(Entry).ToArray();
        }, ct);

        public override Task<Stream> OpenReadAsync(string path, CancellationToken ct) => Task.Run<Stream>(() =>
            new FileStream(ResolveFile(path), FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous), ct);

        public override Task<Stream?> CreateAsync(string path, CancellationToken ct) => Task.Run<Stream?>(() =>
        {
            var full = Prepare(path);
            try
            {
                return new FileStream(full, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous);
            }
            catch (IOException) when (File.Exists(full) || Directory.Exists(full))
            {
                return null;
            }
        }, ct);

        public override Task<bool> MoveAsync(string source, string target, CancellationToken ct) => Task.Run(() =>
        {
            var from = Full(source);
            if (!IsInside(from) || !File.Exists(from) && !Directory.Exists(from))
            {
                throw new NasMountException("storage_unavailable");
            }

            var to = Prepare(target);
            if (File.Exists(to) || Directory.Exists(to))
            {
                return false;
            }

            try
            {
                if (Directory.Exists(from)) Directory.Move(from, to);
                else File.Move(from, to, false);
                return true;
            }
            catch (IOException) when (File.Exists(to) || Directory.Exists(to))
            {
                return false;
            }
        }, ct);

        public override Task DeleteFileAsync(string path, CancellationToken ct) => Task.Run(() => File.Delete(ResolveFile(path)), ct);

        public override Task<bool> DeleteDirectoryAsync(string path, CancellationToken ct) => Task.Run(() =>
        {
            var full = Full(path);
            if (!Directory.Exists(full))
            {
                return true;
            }

            if (!IsInside(full) || Directory.EnumerateFileSystemEntries(full).Any())
            {
                return false;
            }

            Directory.Delete(full);
            return true;
        }, ct);

        public override Task CreateDirectoryAsync(string path, CancellationToken ct) => Task.Run(() =>
        {
            RequireRoot();
            var full = Full(path);
            Directory.CreateDirectory(full);
            if (!IsInside(full)) throw new NasMountException("storage_unavailable");
        }, ct);

        public override async Task<StorageProbe> ProbeAsync(CancellationToken ct)
        {
            // Hanging mounts time out.
            try
            {
                return await Task.Run(Probe, ct).WaitAsync(TimeSpan.FromSeconds(5), ct);
            }
            catch (TimeoutException)
            {
                return new StorageProbe(false, "probe_timeout", null, null);
            }
        }

        private StorageProbe Probe()
        {
            try
            {
                if (!Directory.Exists(_root))
                {
                    return new StorageProbe(false, "storage_unavailable", null, null);
                }

                // One entry proves an answer.
                using (var entries = Directory.EnumerateFileSystemEntries(_root).GetEnumerator())
                {
                    entries.MoveNext();
                }

                // Windows reports drive roots only.
                try
                {
                    var drive = new DriveInfo(OperatingSystem.IsWindows() ? Path.GetPathRoot(_root)! : _root);
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

        private string Full(string path)
        {
            var relative = StoragePath.Check(path);
            return relative.Length == 0 ? _root : Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        }

        // Every existing step from the root down is a real folder or file, never a link.
        private bool IsInside(string full)
        {
            FileSystemInfo current = Directory.Exists(full) ? new DirectoryInfo(full) : new FileInfo(full);
            while (current.FullName.Length > _root.Length)
            {
                if (current.Exists && current.LinkTarget is not null)
                {
                    return false;
                }

                current = current is FileInfo file ? file.Directory! : ((DirectoryInfo)current).Parent!;
            }

            return current.FullName == _root;
        }

        // The parent folders exist below an existing root.
        private string Prepare(string path)
        {
            RequireRoot();
            var full = Full(path);
            var parent = Path.GetDirectoryName(full)!;
            Directory.CreateDirectory(parent);
            if (!IsInside(parent)) throw new NasMountException("storage_unavailable");
            return full;
        }

        private void RequireRoot()
        {
            if (!Directory.Exists(_root)) throw new NasMountException("storage_unavailable");
        }

        private static StorageEntry Entry(FileSystemInfo info) =>
            new(info.Name, info is DirectoryInfo, info is FileInfo file ? file.Length : 0, info.LastWriteTimeUtc);
    }
}
