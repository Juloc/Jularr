using Jularr.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Web.Features.Library;

// Watches every enabled root that supports filesystem events, turns quiet-period batches
// into folder scans, and runs the periodic safety reconciliation. Watcher callbacks only
// record the change; all scanning is queued through LibraryScanCoordinator.
public sealed class LibraryWatchService(
    IServiceScopeFactory scopeFactory,
    LibraryScanCoordinator scans,
    ILogger<LibraryWatchService> logger) : BackgroundService
{
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan SyncInterval = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan QuietPeriod = TimeSpan.FromSeconds(10);

    private readonly object gate = new();
    private readonly Dictionary<Guid, RootWatch> watches = new();
    private readonly Dictionary<Guid, DateTime> periodicAttempts = new();
    private readonly LibraryChangeTracker tracker = new(QuietPeriod);

    public int WatchedRootCount
    {
        get
        {
            lock (gate)
            {
                return watches.Count(x => x.Value.Watcher is not null);
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();

        try
        {
            var lastSyncUtc = DateTime.MinValue;
            using var timer = new PeriodicTimer(Tick);
            while (!stoppingToken.IsCancellationRequested)
            {
                var now = DateTime.UtcNow;
                if (now - lastSyncUtc >= SyncInterval)
                {
                    // The first pass only attaches watchers; periodic checks start one
                    // interval later so the startup reconciliation keeps its own record.
                    await GuardedAsync(() => SyncAsync(includePeriodic: lastSyncUtc != DateTime.MinValue, stoppingToken), stoppingToken);
                    lastSyncUtc = now;
                }

                await GuardedAsync(() => FlushChangesAsync(stoppingToken), stoppingToken);

                if (!await timer.WaitForNextTickAsync(stoppingToken))
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            DisposeWatchers();
        }
    }

    public override void Dispose()
    {
        DisposeWatchers();
        base.Dispose();
    }

    private async Task GuardedAsync(Func<Task> action, CancellationToken stoppingToken)
    {
        try
        {
            await action();
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Library watch pass failed; it will run again on the next tick.");
        }
    }

    private async Task SyncAsync(bool includePeriodic, CancellationToken cancellationToken)
    {
        var roots = await LoadRootsAsync(cancellationToken);
        SyncWatchers(roots);

        if (includePeriodic)
        {
            await RunPeriodicAsync(roots, cancellationToken);
        }
    }

    // One periodic safety reconciliation pass over every enabled root: queues a full scan for
    // each root whose interval has elapsed. The service runs it once a minute.
    public async Task ReconcileDueRootsAsync(CancellationToken cancellationToken) =>
        await RunPeriodicAsync(await LoadRootsAsync(cancellationToken), cancellationToken);

    private async Task<WatchedRoot[]> LoadRootsAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.LibraryRoots
            .AsNoTracking()
            .Where(x => x.IsEnabled)
            .ServingAnime(db)
            .Select(x => new WatchedRoot(x.Id, x.Path, x.ReconciliationIntervalMinutes, x.LastScannedAt))
            .ToArrayAsync(cancellationToken);
    }

    private async Task RunPeriodicAsync(IReadOnlyList<WatchedRoot> roots, CancellationToken cancellationToken)
    {
        foreach (var root in roots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await RunPeriodicAsync(root, cancellationToken);
        }
    }

    private void SyncWatchers(IReadOnlyList<WatchedRoot> roots)
    {
        lock (gate)
        {
            var wanted = roots.ToDictionary(x => x.Id, x => Path.GetFullPath(x.Path));

            foreach (var (rootId, watch) in watches.ToArray())
            {
                var gone = !wanted.TryGetValue(rootId, out var path) ||
                    !string.Equals(path, watch.Path, StringComparison.Ordinal);
                if (!gone && !watch.Faulted)
                {
                    continue;
                }

                // A faulted watcher is re-attached below; its pending full reconciliation
                // stays recorded. Only a removed or re-pathed root drops its pending state.
                watch.Watcher?.Dispose();
                watches.Remove(rootId);
                if (gone)
                {
                    tracker.Forget(rootId);
                    periodicAttempts.Remove(rootId);
                }
            }

            foreach (var (rootId, path) in wanted)
            {
                if (watches.TryGetValue(rootId, out var existing))
                {
                    if (existing.Watcher is null && Directory.Exists(path))
                    {
                        // The root became readable (for example the NAS woke up); attach now.
                        watches[rootId] = Attach(rootId, path, existing);
                    }

                    continue;
                }

                watches.Add(rootId, Attach(rootId, path, new RootWatch(path)));
            }
        }
    }

    private RootWatch Attach(Guid rootId, string path, RootWatch watch)
    {
        if (!Directory.Exists(path))
        {
            if (!watch.UnavailableLogged)
            {
                logger.LogInformation(
                    "Library root {RootId} is not readable; filesystem events will attach once it is available.",
                    rootId);
            }

            return new RootWatch(path) { UnavailableLogged = true };
        }

        try
        {
            var watcher = new FileSystemWatcher(path)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName
                    | NotifyFilters.DirectoryName
                    | NotifyFilters.LastWrite
                    | NotifyFilters.Size,
                InternalBufferSize = 64 * 1024
            };

            watcher.Created += (_, e) => Record(rootId, path, e.FullPath);
            watcher.Changed += (_, e) => Record(rootId, path, e.FullPath);
            watcher.Deleted += (_, e) => Record(rootId, path, e.FullPath);
            watcher.Renamed += (_, e) =>
            {
                Record(rootId, path, e.OldFullPath);
                Record(rootId, path, e.FullPath);
            };
            watcher.Error += (_, e) => OnWatcherError(rootId, e.GetException());
            watcher.EnableRaisingEvents = true;

            logger.LogInformation("Watching library root {RootId} for filesystem changes.", rootId);
            return new RootWatch(path) { Watcher = watcher };
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or PlatformNotSupportedException
                or ArgumentException
                or InvalidOperationException)
        {
            // Network mounts and some platforms cannot raise events; the periodic safety
            // reconciliation covers those roots.
            if (!watch.UnavailableLogged)
            {
                logger.LogInformation(
                    exception,
                    "Filesystem events are not available for library root {RootId}; periodic reconciliation covers it.",
                    rootId);
            }

            return new RootWatch(path) { UnavailableLogged = true };
        }
    }

    private void Record(Guid rootId, string rootPath, string fullPath) =>
        tracker.RecordChange(rootId, rootPath, fullPath, DateTime.UtcNow);

    private void OnWatcherError(Guid rootId, Exception exception)
    {
        logger.LogWarning(
            exception,
            "Filesystem watcher for library root {RootId} lost events; a full reconciliation is scheduled.",
            rootId);
        tracker.RecordOverflow(rootId, DateTime.UtcNow);

        lock (gate)
        {
            if (watches.TryGetValue(rootId, out var watch))
            {
                watch.Faulted = true;
            }
        }
    }

    private async Task FlushChangesAsync(CancellationToken cancellationToken)
    {
        if (!tracker.HasPending)
        {
            return;
        }

        foreach (var batch in tracker.Drain(DateTime.UtcNow))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await QueueBatchAsync(batch, cancellationToken);
        }
    }

    public bool HasPendingChanges => tracker.HasPending;

    public async Task QueueBatchAsync(LibraryChangeBatch batch, CancellationToken cancellationToken)
    {
        if (batch.FullReconciliation)
        {
            await QueueAsync(new LibraryScanRequest(batch.RootId, LibraryScanTrigger.Watch), cancellationToken);
            return;
        }

        foreach (var folder in batch.Folders)
        {
            await QueueAsync(new LibraryScanRequest(batch.RootId, LibraryScanTrigger.Watch, folder), cancellationToken);
        }
    }

    private async Task QueueAsync(LibraryScanRequest request, CancellationToken cancellationToken)
    {
        var result = await scans.QueueAsync(request, cancellationToken);
        if (result.Queued)
        {
            logger.LogDebug(
                "Library change scan for root {RootId} ({Folder}): {Outcome}.",
                request.RootId,
                request.Folder ?? "full",
                result.Outcome);
            return;
        }

        if (result.Outcome == LibraryScanQueueOutcome.AlreadyActive)
        {
            // The running scan may already have passed this change; keep it pending so it is
            // offered again after the next quiet period, once the running scan has finished.
            if (request.Folder is null)
            {
                tracker.RecordOverflow(request.RootId, DateTime.UtcNow);
            }
            else
            {
                tracker.RecordFolder(request.RootId, request.Folder, DateTime.UtcNow);
            }

            return;
        }

        logger.LogInformation(
            "Library change scan for root {RootId} was not queued: {Message}",
            request.RootId,
            result.Message);
    }

    private async Task RunPeriodicAsync(WatchedRoot root, CancellationToken cancellationToken)
    {
        DateTime? lastAttempt;
        lock (gate)
        {
            lastAttempt = periodicAttempts.TryGetValue(root.Id, out var value) ? value : null;
        }

        var now = DateTime.UtcNow;
        if (!LibraryReconciliationSchedule.IsDue(root.ReconciliationIntervalMinutes, root.LastScannedAt, lastAttempt, now))
        {
            return;
        }

        lock (gate)
        {
            periodicAttempts[root.Id] = now;
        }

        var result = await scans.QueueAsync(
            new LibraryScanRequest(root.Id, LibraryScanTrigger.Periodic),
            cancellationToken);

        if (result.Outcome == LibraryScanQueueOutcome.RootUnavailable)
        {
            logger.LogInformation(
                "Periodic library reconciliation skipped for root {RootId}: media storage is unavailable. Next attempt in {Minutes} minutes.",
                root.Id,
                root.ReconciliationIntervalMinutes);
        }
        else if (result.Outcome == LibraryScanQueueOutcome.AlreadyActive)
        {
            // Another run of the root is executing; try again on the next pass instead of
            // waiting a whole interval (a running full scan moves LastScannedAt anyway).
            lock (gate)
            {
                periodicAttempts.Remove(root.Id);
            }
        }
        else if (!result.Queued)
        {
            logger.LogWarning(
                "Periodic library reconciliation was not queued for root {RootId}: {Message}",
                root.Id,
                result.Message);
        }
    }

    private void DisposeWatchers()
    {
        lock (gate)
        {
            foreach (var watch in watches.Values)
            {
                watch.Watcher?.Dispose();
            }

            watches.Clear();
        }
    }

    private sealed record WatchedRoot(
        Guid Id,
        string Path,
        int ReconciliationIntervalMinutes,
        DateTime? LastScannedAt);

    private sealed class RootWatch(string path)
    {
        public string Path { get; } = path;
        public FileSystemWatcher? Watcher { get; init; }
        public bool Faulted { get; set; }
        public bool UnavailableLogged { get; init; }
    }
}
