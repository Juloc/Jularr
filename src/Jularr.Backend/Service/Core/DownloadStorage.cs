using System.Globalization;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Storage;
using Jularr.Backend.Data.Logic.Storage;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Storage;
using Jularr.Integrations.Data.Nas;
using Jularr.Integrations.Data.Storage;
using Jularr.Integrations.Logic.Storage;

namespace Jularr.Backend.Service.Core
{
    internal sealed class DownloadStorage(StorageAccess storage)
    {
        private readonly DownloadStorageReservations _reservations = new();

        public async Task<DownloadStoragePaths> OpenAsync(LogicContext context, DownloadStorageArea area, CancellationToken ct)
        {
            var settings = await context.ExecuteBatchAsync((ctx, token) => ctx.ReadDownloadStorageSettingsAsync(area, token), ct);
            if (!DownloadStorageLogic.IsConfigured(settings))
            {
                throw new ServiceUnavailableException("acquisition_download_path_unconfigured");
            }

            var incompleteId = settings.IncompleteStorageMountId!.Value;
            var completeId = settings.CompleteStorageMountId!.Value;
            var (incomplete, complete) = await context.ExecuteBatchAsync(async (ctx, token) =>
                (await ctx.ReadStorageMountAsync(incompleteId, token), await ctx.ReadStorageMountAsync(completeId, token)), ct);
            try
            {
                if (incomplete.IsReadOnly || complete.IsReadOnly || storage.Open(incomplete) is not LocalStorageFiles source || storage.Open(complete) is not LocalStorageFiles destination)
                {
                    throw new ServiceUnavailableException("download_storage_mount_required");
                }

                var sourceRoot = source.ResolveDirectory(settings.IncompletePath!);
                var destinationRoot = destination.ResolveDirectory(settings.CompletePath!);
                if (StorageFileSystem.DownloadFilesystemIdentity(sourceRoot) != settings.IncompleteFilesystemIdentity
                    || StorageFileSystem.DownloadFilesystemIdentity(destinationRoot) != settings.CompleteFilesystemIdentity)
                {
                    throw new ServiceUnavailableException("download_storage_unavailable");
                }

                foreach (var path in new[] { sourceRoot, destinationRoot })
                {
                    if (StorageFileSystem.AvailableFreeSpace(path) is not { } available || available < settings.FreeSpaceReserveBytes.Value)
                    {
                        throw new ServiceUnavailableException("download_storage_reserve");
                    }
                }

                var name = ".jularr-" + settings.WorkspaceId.ToString("N");
                var paths = new DownloadStoragePaths(settings, Path.Combine(sourceRoot, name), Path.Combine(destinationRoot, name), _reservations);
                paths.CheckCapacity();
                return paths;
            }
            catch (Exception exception) when (exception is NasMountException or IOException or UnauthorizedAccessException)
            {
                throw new ServiceUnavailableException("download_storage_unavailable");
            }
        }
    }

    internal sealed class DownloadStorageReservations
    {
        private readonly object _gate = new();
        private readonly Dictionary<(Guid Workspace, long Operation), long> _writes = [];
        private readonly Dictionary<string, long> _volumes = new(StringComparer.Ordinal);
        private readonly Dictionary<Guid, HashSet<long>> _jobs = [];

        public IDisposable? TryStart(Guid workspace, long operation, int maximumParallelJobs)
        {
            lock (_gate)
            {
                if (!_jobs.TryGetValue(workspace, out var active)) _jobs[workspace] = active = [];
                if (active.Contains(operation) || active.Count >= maximumParallelJobs) return null;
                active.Add(operation);
            }

            return new Reservation(() =>
            {
                lock (_gate)
                {
                    var active = _jobs[workspace];
                    active.Remove(operation);
                    if (active.Count == 0) _jobs.Remove(workspace);
                }
            });
        }

        public IDisposable Reserve(Guid workspace, long operation, string volume, long bytes, Action<long, long> check)
        {
            if (bytes < 0) throw new ArgumentOutOfRangeException(nameof(bytes));
            lock (_gate)
            {
                var jobPending = _writes.GetValueOrDefault((workspace, operation));
                var workspacePending = _writes.Where(item => item.Key.Workspace == workspace).Sum(item => item.Value);
                var volumePending = _volumes.GetValueOrDefault(volume);
                check(workspacePending, volumePending);
                if (bytes == 0) return new Reservation(static () => { });
                _writes[(workspace, operation)] = checked(jobPending + bytes);
                _volumes[volume] = checked(volumePending + bytes);
            }

            return new Reservation(() =>
            {
                lock (_gate)
                {
                    var key = (workspace, operation);
                    var jobRemaining = _writes[key] - bytes;
                    if (jobRemaining == 0) _writes.Remove(key); else _writes[key] = jobRemaining;
                    var volumeRemaining = _volumes[volume] - bytes;
                    if (volumeRemaining == 0) _volumes.Remove(volume); else _volumes[volume] = volumeRemaining;
                }
            });
        }

        private sealed class Reservation(Action release) : IDisposable
        {
            private Action? _release = release;
            public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
        }
    }

    internal sealed class DownloadStoragePaths(DownloadStorageSettingsData settings, string incompleteRoot, string completeRoot, DownloadStorageReservations reservations)
    {
        public DownloadStorageSettingsData Settings { get; } = settings;
        public string IncompleteRoot { get; } = incompleteRoot;
        public string CompleteRoot { get; } = completeRoot;

        public string Incomplete(long operationId) => Path.Combine(IncompleteRoot, operationId.ToString(CultureInfo.InvariantCulture));
        public string Complete(long operationId) => Path.Combine(CompleteRoot, operationId.ToString(CultureInfo.InvariantCulture));
        private string Pending(long operationId) => Path.Combine(CompleteRoot, "partial-" + operationId.ToString(CultureInfo.InvariantCulture));

        public bool IsComplete(long operationId) => Directory.Exists(Complete(operationId));
        public IDisposable? TryStartJob(long operationId) => reservations.TryStart(Settings.WorkspaceId, operationId, Settings.MaxParallelJobs);

        public IDisposable ReserveWrite(long operationId, string directory, long bytes)
        {
            var volume = StorageFileSystem.DownloadFilesystemIdentity(directory, includeDirectory: false)
                ?? throw new ServiceUnavailableException("download_storage_unavailable");
            return reservations.Reserve(Settings.WorkspaceId, operationId, volume, bytes, (workspacePending, volumePending) =>
            {
                CheckCapacity();
                if (StorageFileSystem.AvailableFreeSpace(directory) is not { } available
                    || bytes > available - Settings.FreeSpaceReserveBytes!.Value - volumePending)
                {
                    throw new ServiceUnavailableException("download_storage_reserve");
                }

                if (Settings.MaxWorkspaceBytes is { } workspaceLimit && bytes > workspaceLimit - UsedBytes(IncompleteRoot) - UsedBytes(CompleteRoot) - workspacePending)
                {
                    throw new ServiceUnavailableException("download_workspace_limit");
                }
            });
        }

        public async Task<string> PrepareIncompleteAsync(long operationId, CancellationToken ct)
        {
            CheckIdentity(IncompleteRoot, Settings.IncompleteFilesystemIdentity);
            await new LocalStorageFiles(Path.GetDirectoryName(IncompleteRoot)!).CreateDirectoryAsync(Path.GetFileName(IncompleteRoot), ct);
            await new LocalStorageFiles(IncompleteRoot).CreateDirectoryAsync(operationId.ToString(CultureInfo.InvariantCulture), ct);
            return StorageFileSystem.ResolveDirectory(IncompleteRoot, operationId.ToString(CultureInfo.InvariantCulture));
        }

        public long MaximumArticleBytes(string articles)
        {
            var maximum = Settings.MaxJobBytes ?? long.MaxValue;
            var current = UsedBytes(articles);
            if (Settings.MaxWorkspaceBytes is { } workspaceLimit)
            {
                maximum = Math.Min(maximum, workspaceLimit - (UsedBytes(IncompleteRoot) + UsedBytes(CompleteRoot) - current));
            }

            return maximum;
        }

        public void CheckCapacity(long additionalBytes = 0)
        {
            foreach (var (root, expected) in new[] { (IncompleteRoot, Settings.IncompleteFilesystemIdentity), (CompleteRoot, Settings.CompleteFilesystemIdentity) })
            {
                var directory = Path.GetDirectoryName(root)!;
                CheckIdentity(root, expected);

                if (StorageFileSystem.AvailableFreeSpace(directory) is not { } available
                    || additionalBytes > available - Settings.FreeSpaceReserveBytes!.Value)
                {
                    throw new ServiceUnavailableException("download_storage_reserve");
                }
            }

            if (Settings.MaxWorkspaceBytes is { } workspaceLimit && additionalBytes > workspaceLimit - UsedBytes(IncompleteRoot) - UsedBytes(CompleteRoot))
            {
                throw new ServiceUnavailableException("download_workspace_limit");
            }
        }

        private static long UsedBytes(string directory)
        {
            if (!Directory.Exists(directory)) return 0;
            var state = new StorageScanState();
            var bytes = StorageFileSystem.EnumerateFiles(directory, null, state, CancellationToken.None, includeHidden: true).Sum(file => file.SizeBytes);
            if (!state.IsComplete) throw new ServiceUnavailableException("download_storage_unavailable");
            return bytes;
        }

        private static void CheckIdentity(string root, string? expected)
        {
            var directory = Path.GetDirectoryName(root)!;
            if (StorageFileSystem.DownloadFilesystemIdentity(directory) != expected)
            {
                throw new ServiceUnavailableException("download_storage_unavailable");
            }

            if (Directory.Exists(root)) StorageFileSystem.ResolveDirectory(directory, Path.GetFileName(root));
        }

        public async Task<string> CompleteAsync(long operationId, string source, IReadOnlySet<string>? extensions, CancellationToken ct)
        {
            CheckCapacity();
            var target = Complete(operationId);
            if (Directory.Exists(target)) return StorageFileSystem.ResolveDirectory(CompleteRoot, operationId.ToString(CultureInfo.InvariantCulture));
            await new LocalStorageFiles(Path.GetDirectoryName(CompleteRoot)!).CreateDirectoryAsync(Path.GetFileName(CompleteRoot), ct);
            var state = new StorageScanState();
            var files = StorageFileSystem.EnumerateFiles(source, extensions, state, ct).ToArray();
            if (!state.IsComplete) throw new ServiceUnavailableException("download_storage_unavailable");
            var bytes = files.Sum(file => file.SizeBytes);
            CheckCapacity();
            source = StorageFileSystem.ResolveDirectory(Incomplete(operationId), Path.GetRelativePath(Incomplete(operationId), source));
            var sourceVolume = StorageFileSystem.DownloadFilesystemIdentity(source, includeDirectory: false, includeMountPoint: true);
            if (sourceVolume is not null && sourceVolume == StorageFileSystem.DownloadFilesystemIdentity(CompleteRoot, includeDirectory: false, includeMountPoint: true))
            {
                // Moving on one filesystem needs no second payload copy.
                Directory.Move(source, target);
                return target;
            }

            // Publishing a job directory is atomic on the Complete filesystem.
            var pending = Pending(operationId);
            if (Directory.Exists(pending)) Directory.Delete(StorageFileSystem.ResolveDirectory(CompleteRoot, Path.GetFileName(pending)), recursive: true);
            CheckCapacity(bytes);
            var root = Path.GetDirectoryName(CompleteRoot)!;
            if (StorageFileSystem.AvailableFreeSpace(root) is not { } available || bytes > available - Settings.FreeSpaceReserveBytes!.Value)
            {
                throw new ServiceUnavailableException("download_storage_reserve");
            }

            Directory.CreateDirectory(pending);
            try
            {
                var destination = new LocalStorageFiles(pending);
                var origin = new LocalStorageFiles(source);
                foreach (var file in files)
                {
                    await StorageFileTransfer.PlaceAsync(origin, file.RelativePath, destination, file.RelativePath, ct, count => ReserveWrite(operationId, pending, count));
                }

                Directory.Move(pending, target);
            }
            finally
            {
                if (Directory.Exists(pending)) Directory.Delete(pending, recursive: true);
            }

            return target;
        }

        public void RemoveCompleted(long operationId)
        {
            foreach (var (root, expected) in new[] { (IncompleteRoot, Settings.IncompleteFilesystemIdentity), (CompleteRoot, Settings.CompleteFilesystemIdentity) })
            {
                CheckIdentity(root, expected);
                var name = operationId.ToString(CultureInfo.InvariantCulture);
                var path = Path.Combine(root, name);
                if (Directory.Exists(path)) Directory.Delete(StorageFileSystem.ResolveDirectory(root, name), recursive: true);
            }
        }
    }
}
