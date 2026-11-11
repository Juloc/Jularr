using System.Runtime.CompilerServices;
using Jularr.Integrations.Data.Nas;
using Jularr.Integrations.Data.Storage;

namespace Jularr.Integrations.Logic.Storage
{
    // The files of one connection by paths inside it: a folder on this host (a local path or a Docker volume) or an SMB share Jularr reaches itself.
    // Storage out of reach and missing paths throw NasMountException, refused access too (code storage_access_denied, never the caller's sign-in),
    // anything else IOException. Opened through StorageAccess, every call passes the connection's StorageGate.
    internal abstract class StorageFiles
    {
        // Replaced files wait here inside their library; scans pass it by.
        public const string RecycleFolder = ".jularr-recycle";

        // A probe's caller waits this long at most.
        private static readonly TimeSpan ProbeTime = TimeSpan.FromSeconds(5);

        // The connection's gate, and whether a person waits for the answer (a web request) rather than background work.
        public StorageGate? Gate { get; init; }
        public bool Interactive { get; init; }

        // The entry at the path, null when there is none.
        public Task<StorageEntry?> GetAsync(string path, CancellationToken ct) => PassAsync(token => GetCoreAsync(path, token), ct);

        // The entries of a folder that exists, without hidden or system entries and links.
        public Task<IReadOnlyList<StorageEntry>> ListAsync(string directory, CancellationToken ct) => PassAsync(token => ListCoreAsync(directory, token), ct);

        // A seekable stream of an existing file.
        public Task<Stream> OpenReadAsync(string path, CancellationToken ct) => PassAsync(token => OpenReadCoreAsync(path, token), ct);

        // A new file in a folder created as needed; null when the name is taken.
        public Task<Stream?> CreateAsync(string path, CancellationToken ct) => PassAsync(token => CreateCoreAsync(path, token), ct);

        // Moves a file or folder into a folder created as needed; false when the target name is taken.
        public Task<bool> MoveAsync(string source, string target, CancellationToken ct) => PassAsync(token => MoveCoreAsync(source, target, token), ct);

        public Task DeleteFileAsync(string path, CancellationToken ct) => PassAsync(async token =>
        {
            await DeleteFileCoreAsync(path, token);
            return true;
        }, ct);

        // Only an empty folder goes; false when it still holds something.
        public Task<bool> DeleteDirectoryAsync(string path, CancellationToken ct) => PassAsync(token => DeleteDirectoryCoreAsync(path, token), ct);

        public Task CreateDirectoryAsync(string path, CancellationToken ct) => PassAsync(async token =>
        {
            await CreateDirectoryCoreAsync(path, token);
            return true;
        }, ct);

        // Readable or why not, with the volume's space; never longer than five seconds.
        public async Task<StorageProbe> ProbeAsync(CancellationToken ct)
        {
            try
            {
                return await (Gate is { } gate ? gate.ProbeAsync(ProbeCoreAsync, ct) : ProbeCoreAsync(ct)).WaitAsync(ProbeTime, ct);
            }
            catch (TimeoutException)
            {
                return new StorageProbe(false, "probe_timeout", null, null);
            }
        }

        protected abstract Task<StorageEntry?> GetCoreAsync(string path, CancellationToken ct);

        protected abstract Task<IReadOnlyList<StorageEntry>> ListCoreAsync(string directory, CancellationToken ct);

        protected abstract Task<Stream> OpenReadCoreAsync(string path, CancellationToken ct);

        protected abstract Task<Stream?> CreateCoreAsync(string path, CancellationToken ct);

        protected abstract Task<bool> MoveCoreAsync(string source, string target, CancellationToken ct);

        protected abstract Task DeleteFileCoreAsync(string path, CancellationToken ct);

        protected abstract Task<bool> DeleteDirectoryCoreAsync(string path, CancellationToken ct);

        protected abstract Task CreateDirectoryCoreAsync(string path, CancellationToken ct);

        // Readable or why not; the caller's five seconds are counted here, the storage's answer may take longer.
        protected abstract Task<StorageProbe> ProbeCoreAsync(CancellationToken ct);

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
                    else if (extensions is null || StoragePath.HasExtension(entry.Name, extensions))
                    {
                        yield return new StorageFile(path, entry.Length, entry.ModifiedAt);
                    }
                }
            }
        }

        // Storage that refuses Jularr is a storage problem: a 503, never the 401 that would sign the caller out.
        private async Task<T> PassAsync<T>(Func<CancellationToken, Task<T>> call, CancellationToken ct)
        {
            try
            {
                return await (Gate is { } gate ? gate.RunAsync(call, Interactive, ct) : call(ct));
            }
            catch (UnauthorizedAccessException exception)
            {
                throw new NasMountException(NasMountException.AccessDenied, exception);
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
