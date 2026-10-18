using System.Runtime.CompilerServices;
using Jularr.Integrations.Data.Nas;
using Jularr.Integrations.Data.Storage;

namespace Jularr.Integrations.Logic.Storage
{
    // The files of one connection by paths inside it: a folder on this host (a local path or a Docker volume) or an SMB share Jularr reaches itself.
    // Storage out of reach and missing paths throw NasMountException, refused access UnauthorizedAccessException, anything else IOException.
    internal abstract class StorageFiles
    {
        // Replaced files wait here inside their library; scans pass it by.
        public const string RecycleFolder = ".jularr-recycle";

        // The entry at the path, null when there is none.
        public abstract Task<StorageEntry?> GetAsync(string path, CancellationToken ct);

        // The entries of a folder that exists, without hidden or system entries and links.
        public abstract Task<IReadOnlyList<StorageEntry>> ListAsync(string directory, CancellationToken ct);

        // A seekable stream of an existing file.
        public abstract Task<Stream> OpenReadAsync(string path, CancellationToken ct);

        // A new file in a folder created as needed; null when the name is taken.
        public abstract Task<Stream?> CreateAsync(string path, CancellationToken ct);

        // Moves a file or folder into a folder created as needed; false when the target name is taken.
        public abstract Task<bool> MoveAsync(string source, string target, CancellationToken ct);

        public abstract Task DeleteFileAsync(string path, CancellationToken ct);

        // Only an empty folder goes; false when it still holds something.
        public abstract Task<bool> DeleteDirectoryAsync(string path, CancellationToken ct);

        public abstract Task CreateDirectoryAsync(string path, CancellationToken ct);

        // Readable or why not, with the volume's space; never longer than five seconds.
        public abstract Task<StorageProbe> ProbeAsync(CancellationToken ct);

        public async Task<bool> ExistsAsync(string path, CancellationToken ct) => await GetAsync(path, ct) is not null;

        // The subfolder names of a folder, sorted.
        public async Task<IReadOnlyList<string>> ListDirectoriesAsync(string directory, int limit, CancellationToken ct, string? afterName = null) =>
            (await ListAsync(directory, ct)).Where(entry => entry.IsDirectory).Select(entry => entry.Name)
                .Where(name => afterName is null || string.CompareOrdinal(name, afterName) > 0).Order(StringComparer.Ordinal).Take(limit).ToArray();

        // Every file below a folder, paths relative to it; without extensions every file counts. A folder that cannot be read makes the walk incomplete.
        public async IAsyncEnumerable<StorageFile> EnumerateFilesAsync(string directory, IReadOnlySet<string>? extensions, StorageScanState state,
            [EnumeratorCancellation] CancellationToken ct)
        {
            directory = StoragePath.Check(directory);
            var pending = new Stack<string>();
            pending.Push("");
            while (pending.TryPop(out var relative))
            {
                ct.ThrowIfCancellationRequested();
                IReadOnlyList<StorageEntry> entries;
                try
                {
                    entries = await ListAsync(StoragePath.Combine(directory, relative), ct);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NasMountException)
                {
                    // An unreadable root means no walk; a skipped folder means an incomplete one.
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
                    if (entry.IsDirectory)
                    {
                        if (entry.Name != RecycleFolder) pending.Push(path);
                    }
                    else if (extensions is null || extensions.Contains(Path.GetExtension(entry.Name).ToLowerInvariant()))
                    {
                        yield return new StorageFile(path, entry.Length, entry.ModifiedAt);
                    }
                }
            }
        }

        // A folder a move or deletion left empty goes, and so do its emptied parents, never the stop folder itself.
        public async Task DeleteEmptyFoldersAsync(string folder, string stopAt, CancellationToken ct)
        {
            stopAt = StoragePath.Check(stopAt);
            for (var directory = StoragePath.Check(folder); directory.Length > 0 && (stopAt.Length == 0 || directory.StartsWith(stopAt + "/", StringComparison.Ordinal));
                directory = StoragePath.Parent(directory))
            {
                if (!await DeleteDirectoryAsync(directory, ct))
                {
                    return;
                }
            }
        }
    }
}
