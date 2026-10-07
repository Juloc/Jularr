using System.Data.Common;
using System.Diagnostics;
using System.Text.Json;
using Jularr.Web.Data;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.DownloadClients;
using Jularr.Web.Features.Acquisition.Health;
using Jularr.Web.Features.Acquisition.Indexers;
using Jularr.Web.Features.Acquisition.Wanted;
using Jularr.Web.Features.Appearance;
using Jularr.Web.Features.Health;
using Jularr.Web.Features.Operations;
using Jularr.Web.Features.Providers;
using Jularr.Web.Features.Storage;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Web.Features.Admin;

/// <summary>How a service or connection tile reads. <see cref="Neutral"/> is for "not configured" and "nothing observed yet".</summary>
public enum DashboardState
{
    Online,
    Warning,
    Error,
    Neutral
}

/// <summary>
/// The separate concepts the dashboard reports on. Indexers, download clients and metadata providers are
/// different things and are never merged into one figure.
/// </summary>
public enum DashboardServiceKind
{
    Web,
    Database,
    Storage,
    DownloadClients,
    Indexers,
    Metadata,
    Subtitles
}

/// <summary>One service or connection tile. <see cref="Total"/> is 0 when the tile is not a group.</summary>
public sealed record DashboardServiceTile(
    DashboardServiceKind Kind,
    DashboardState State,
    int Healthy = 0,
    int Total = 0,
    double? LatencyMs = null,
    string? Reason = null,
    string? Version = null,
    TimeSpan? Uptime = null);

public enum DashboardSeverity
{
    Warning,
    Error
}

/// <summary>
/// One thing that is wrong. <see cref="Code"/> picks the message (<c>admin.dashboard.problem.{code}</c>);
/// <see cref="Name"/>, <see cref="Count"/> and <see cref="Detail"/> fill its placeholders and
/// <see cref="Href"/> is the filtered admin page that resolves it.
/// </summary>
public sealed record DashboardProblem(
    DashboardSeverity Severity,
    string Code,
    string Href,
    string? Name = null,
    int Count = 0,
    string? Detail = null,
    long? Bytes = null);

/// <summary>A media storage root as the dashboard shows it; sizes are null when the volume cannot say.</summary>
public sealed record DashboardRoot(
    string Name,
    string Path,
    StorageHealthState? Health,
    long? FreeBytes,
    long? TotalBytes,
    string? FileSystemType);

public sealed record DashboardStorage(
    IReadOnlyList<DashboardRoot> Roots,
    long? DataFreeBytes,
    long? DataTotalBytes,
    long? DatabaseBytes);

/// <summary>Active download and Jularr task work; the lists hold the first rows, the counts cover all of it.</summary>
public sealed record DashboardWork(
    IReadOnlyList<OperationSnapshot> Downloads,
    int DownloadsRunning,
    int DownloadsQueued,
    IReadOnlyList<OperationSnapshot> Tasks,
    int TasksRunning,
    int TasksQueued);

/// <summary>Work that is waiting for the admin; zero counts are not shown.</summary>
public sealed record DashboardWaiting(
    int PendingRequests,
    int Wanted,
    int WantedFailed,
    int UnresolvedMappings,
    int MissingLearningText)
{
    public bool Any => PendingRequests > 0 || Wanted > 0 || UnresolvedMappings > 0 || MissingLearningText > 0;
}

/// <summary>
/// Everything the dashboard shows. A section that could not be read is null, so the page shows an error
/// state for it instead of an empty or a healthy one. <c>HealthComplete</c> says whether everything that
/// decides "healthy" was read; only then may the page say that all systems are operational.
/// </summary>
public sealed record AdminDashboardSnapshot(
    DateTime GeneratedAtUtc,
    IReadOnlyList<DashboardServiceTile> Services,
    IReadOnlyList<DashboardProblem> Problems,
    DashboardStorage? Storage,
    DashboardWork? Work,
    IReadOnlyList<AdminSessionRow>? Sessions,
    IReadOnlyList<OperationSnapshot>? Recent,
    DashboardWaiting? Waiting,
    StackResourceSnapshot Resources,
    bool HealthComplete);

/// <summary>
/// Read-only figures for the Admin dashboard. Every value reuses an existing store, query or service;
/// nothing here owns data. Each section is read on its own, so one failing source does not blank the page.
/// </summary>
public sealed class AdminDashboardService(
    AppDbContext db,
    AdminOverviewService overview,
    AdminSessionsService sessions,
    AcquisitionAccessStore acquisitionAccess,
    WantedListService wanted,
    LibraryRootAvailabilityService storageAvailability,
    IndexerStore indexers,
    DownloadClientStore downloadClients,
    AcquisitionHealthStore acquisitionHealth,
    ProviderCatalog providerCatalog,
    ProviderHealthTracker providerHealth,
    IStackResourceTelemetry telemetry,
    TimeProvider clock,
    ILogger<AdminDashboardService> logger)
{
    /// <summary>How many downloads and tasks the dashboard lists; the rest is a count and a link.</summary>
    public const int ListLimit = 6;

    public const int RecentLimit = 8;

    /// <summary>Above this, a database round trip is reported as slow.</summary>
    public const double SlowDatabaseMilliseconds = 250;

    private const int ActiveScanLimit = 500;

    public async Task<AdminDashboardSnapshot> GetAsync(bool includeSessions, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var resources = telemetry.GetSnapshot();

        var counts = await TryAsync("overview", () => overview.GetAsync(cancellationToken));
        var database = await ReadDatabaseAsync(cancellationToken);
        var storage = await TryAsync("storage", () => ReadStorageAsync(database.SizeBytes, cancellationToken));
        var acquisition = await TryAsync("acquisition", () => ReadAcquisitionAsync(cancellationToken));
        var work = await TryAsync("work", () => ReadWorkAsync(cancellationToken));
        var recent = await TryAsync(
            "recent",
            () => new OperationStore(db).ListAsync(new OperationListFilter(View: "history", Limit: RecentLimit), cancellationToken));
        var waiting = await TryAsync("waiting", () => ReadWaitingAsync(counts, cancellationToken));
        var liveSessions = includeSessions
            ? await TryAsync("sessions", () => sessions.ListAllAsync(cancellationToken))
            : null;

        var providers = ReadProviders();
        var services = new List<DashboardServiceTile>
        {
            new(
                DashboardServiceKind.Web,
                DashboardState.Online,
                Version: AppBuildInfo.Version,
                Uptime: ProcessUptime(now)),
            database.Tile
        };
        if (storage is not null)
        {
            services.Add(StorageTile(storage));
        }

        if (acquisition is not null)
        {
            services.Add(acquisition.DownloadClients);
            services.Add(acquisition.Indexers);
        }

        services.AddRange(providers.Tiles);

        var problems = new List<DashboardProblem>();
        if (database.Tile.State != DashboardState.Online || database.Tile.LatencyMs >= SlowDatabaseMilliseconds)
        {
            problems.Add(new DashboardProblem(
                database.Tile.State == DashboardState.Error ? DashboardSeverity.Error : DashboardSeverity.Warning,
                database.Tile.State == DashboardState.Error ? "databaseDown" : "databaseSlow",
                "/Admin/Health",
                Count: (int)Math.Round(database.Tile.LatencyMs ?? 0)));
        }

        if (storage is not null)
        {
            AddStorageProblems(problems, storage);
        }

        if (acquisition is not null)
        {
            problems.AddRange(acquisition.Problems);
        }

        problems.AddRange(providers.Problems);
        if (counts is not null)
        {
            AddOperationProblems(problems, counts);
        }

        if (waiting is { WantedFailed: > 0 })
        {
            problems.Add(new DashboardProblem(
                DashboardSeverity.Warning,
                "wantedFailed",
                "/Admin/Wanted?tab=failed",
                Count: waiting.WantedFailed));
        }

        return new AdminDashboardSnapshot(
            now,
            services,
            problems
                .OrderBy(problem => problem.Severity == DashboardSeverity.Error ? 0 : 1)
                .ToArray(),
            storage,
            work,
            liveSessions,
            recent,
            waiting,
            resources,
            HealthComplete: counts is not null && storage is not null && acquisition is not null && work is not null);
    }

    private static TimeSpan? ProcessUptime(DateTime nowUtc)
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            var uptime = nowUtc - process.StartTime.ToUniversalTime();
            return uptime > TimeSpan.Zero ? uptime : null;
        }
        catch (Exception exception) when (exception is InvalidOperationException or PlatformNotSupportedException or NotSupportedException)
        {
            return null;
        }
    }

    private async Task<(DashboardServiceTile Tile, long? SizeBytes)> ReadDatabaseAsync(CancellationToken cancellationToken)
    {
        try
        {
            var watch = Stopwatch.StartNew();
            await db.Database.SqlQuery<int>($"SELECT 1 AS \"Value\"").FirstOrDefaultAsync(cancellationToken);
            watch.Stop();
            var latency = watch.Elapsed.TotalMilliseconds;

            long? size = null;
            try
            {
                size = await db.Database
                    .SqlQuery<long>($"SELECT pg_database_size(current_database()) AS \"Value\"")
                    .FirstOrDefaultAsync(cancellationToken);
            }
            catch (Exception exception) when (IsExpected(exception))
            {
                logger.LogDebug(exception, "The database size could not be read.");
            }

            return (
                new DashboardServiceTile(
                    DashboardServiceKind.Database,
                    latency >= SlowDatabaseMilliseconds ? DashboardState.Warning : DashboardState.Online,
                    LatencyMs: latency),
                size);
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            logger.LogWarning(exception, "The database did not answer for the dashboard.");
            return (new DashboardServiceTile(DashboardServiceKind.Database, DashboardState.Error), null);
        }
    }

    private async Task<DashboardStorage> ReadStorageAsync(long? databaseBytes, CancellationToken cancellationToken)
    {
        var libraryRoots = await db.LibraryRoots
            .AsNoTracking()
            .OrderBy(root => root.Name)
            .ToListAsync(cancellationToken);

        var roots = new List<DashboardRoot>(libraryRoots.Count);
        foreach (var root in libraryRoots)
        {
            var snapshot = storageAvailability.GetCached(root);
            long? total = null;
            if (snapshot is { IsAvailable: true })
            {
                total = AdminServerLoad.VolumeSpace(root.Path).Total;
            }

            roots.Add(new DashboardRoot(
                root.Name,
                root.Path,
                snapshot?.Health,
                snapshot?.FreeSpaceBytes,
                total,
                ContainerMounts.FileSystemTypeForPath(root.Path)));
        }

        var (free, totalBytes) = AdminServerLoad.DataVolumeSpace();
        return new DashboardStorage(roots, free, totalBytes, databaseBytes);
    }

    private static DashboardServiceTile StorageTile(DashboardStorage storage)
    {
        if (storage.Roots.Count == 0)
        {
            return new DashboardServiceTile(DashboardServiceKind.Storage, DashboardState.Neutral);
        }

        var online = storage.Roots.Count(root => root.Health == StorageHealthState.Online);
        var offline = storage.Roots.Count(root => root.Health is StorageHealthState.OfflineUnexpected or StorageHealthState.Error);
        var state = offline > 0
            ? DashboardState.Error
            : online == storage.Roots.Count ? DashboardState.Online : DashboardState.Warning;
        return new DashboardServiceTile(DashboardServiceKind.Storage, state, online, storage.Roots.Count);
    }

    private static void AddStorageProblems(List<DashboardProblem> problems, DashboardStorage storage)
    {
        foreach (var root in storage.Roots)
        {
            if (root.Health is StorageHealthState.OfflineUnexpected or StorageHealthState.Error)
            {
                problems.Add(new DashboardProblem(DashboardSeverity.Error, "storageOffline", "/Admin/System", Name: root.Name));
            }
            else if (root.Health == StorageHealthState.Online && root.FreeBytes is { } free && free <= SystemHealthService.WarningFreeBytes)
            {
                problems.Add(new DashboardProblem(
                    free <= SystemHealthService.ErrorFreeBytes ? DashboardSeverity.Error : DashboardSeverity.Warning,
                    "storageLow",
                    "/Admin/System",
                    Name: root.Name,
                    Bytes: free));
            }
        }

        if (storage.DataFreeBytes is { } dataFree && dataFree <= SystemHealthService.WarningFreeBytes)
        {
            problems.Add(new DashboardProblem(
                dataFree <= SystemHealthService.ErrorFreeBytes ? DashboardSeverity.Error : DashboardSeverity.Warning,
                "dataVolumeLow",
                "/Admin/Storage",
                Bytes: dataFree));
        }
    }

    private sealed record AcquisitionRead(
        DashboardServiceTile DownloadClients,
        DashboardServiceTile Indexers,
        IReadOnlyList<DashboardProblem> Problems);

    private async Task<AcquisitionRead> ReadAcquisitionAsync(CancellationToken cancellationToken)
    {
        var health = (await acquisitionHealth.LoadAsync(cancellationToken)).Statuses;
        var problems = new List<DashboardProblem>();

        DashboardServiceTile Group(
            DashboardServiceKind kind,
            AcquisitionHealthKind healthKind,
            IEnumerable<(Guid Id, string Name)> entries,
            string problemCode)
        {
            var enabled = entries.ToArray();
            if (enabled.Length == 0)
            {
                return new DashboardServiceTile(kind, DashboardState.Neutral);
            }

            var unhealthy = enabled
                .Select(entry => (entry.Name, Status: health.FirstOrDefault(status => status.Kind == healthKind && status.EntryId == entry.Id)))
                .Where(entry => entry.Status is { IsHealthy: false })
                .ToArray();
            var healthy = enabled.Length - unhealthy.Length;
            foreach (var (name, status) in unhealthy)
            {
                problems.Add(new DashboardProblem(
                    healthy == 0 ? DashboardSeverity.Error : DashboardSeverity.Warning,
                    problemCode,
                    "/Admin/Usenet",
                    Name: name,
                    Detail: status?.LastError));
            }

            return new DashboardServiceTile(
                kind,
                unhealthy.Length == 0 ? DashboardState.Online : healthy == 0 ? DashboardState.Error : DashboardState.Warning,
                healthy,
                enabled.Length,
                Reason: unhealthy.Length == 0 ? null : unhealthy[0].Status?.LastError);
        }

        var clientEntries = (await downloadClients.LoadAllAsync(cancellationToken))
            .Where(entry => entry.Enabled)
            .Select(entry => (entry.Id, entry.Name));
        var indexerEntries = (await indexers.LoadAllAsync(cancellationToken))
            .Where(entry => entry.Enabled)
            .Select(entry => (entry.Id, entry.Name));

        var clientTile = Group(DashboardServiceKind.DownloadClients, AcquisitionHealthKind.DownloadClient, clientEntries, "clientDown");
        var indexerTile = Group(DashboardServiceKind.Indexers, AcquisitionHealthKind.Indexer, indexerEntries, "indexerDown");
        return new AcquisitionRead(clientTile, indexerTile, problems);
    }

    private (IReadOnlyList<DashboardServiceTile> Tiles, IReadOnlyList<DashboardProblem> Problems) ReadProviders()
    {
        var tiles = new List<DashboardServiceTile>();
        var problems = new List<DashboardProblem>();

        void AddGroup(DashboardServiceKind kind, ProviderCapabilities capability, string problemCode, string href)
        {
            var members = providerCatalog.All().Where(descriptor => descriptor.Supports(capability)).ToArray();
            if (members.Length == 0)
            {
                return;
            }

            var states = members.Select(descriptor => (Descriptor: descriptor, Health: providerHealth.Get(descriptor.Key))).ToArray();
            var healthy = states.Count(entry => entry.Health.Status == ProviderHealthStatus.Healthy);
            var failing = states.Where(entry => entry.Health.Status is ProviderHealthStatus.Degraded or ProviderHealthStatus.Unavailable or ProviderHealthStatus.AuthenticationFailed).ToArray();
            var observed = states.Count(entry => entry.Health.Status != ProviderHealthStatus.Unknown);

            var state = failing.Any(entry => entry.Health.Status == ProviderHealthStatus.Unavailable)
                ? DashboardState.Error
                : failing.Length > 0
                    ? DashboardState.Warning
                    : observed == 0 ? DashboardState.Neutral : DashboardState.Online;
            foreach (var entry in failing)
            {
                problems.Add(new DashboardProblem(
                    entry.Health.Status == ProviderHealthStatus.Unavailable ? DashboardSeverity.Error : DashboardSeverity.Warning,
                    problemCode,
                    href,
                    Name: entry.Descriptor.DisplayName,
                    Detail: entry.Health.LastError));
            }

            tiles.Add(new DashboardServiceTile(
                kind,
                state,
                healthy,
                members.Length,
                Reason: failing.Length > 0 ? failing[0].Health.LastError : null));
        }

        AddGroup(DashboardServiceKind.Metadata, ProviderCapabilities.Metadata, "metadataDown", "/Admin/System");
        AddGroup(DashboardServiceKind.Subtitles, ProviderCapabilities.Subtitles, "subtitlesDown", "/Admin/Subtitles");
        return (tiles, problems);
    }

    private async Task<DashboardWork> ReadWorkAsync(CancellationToken cancellationToken)
    {
        var active = await new OperationStore(db).ListAsync(
            new OperationListFilter(View: "active", Limit: ActiveScanLimit),
            cancellationToken);

        var downloads = active.Where(operation => operation.IsDownload).ToArray();
        var tasks = active.Where(operation => !operation.IsDownload).ToArray();
        return new DashboardWork(
            downloads.Take(ListLimit).ToArray(),
            downloads.Count(operation => operation.Status == OperationStatus.Running),
            downloads.Count(operation => operation.Status == OperationStatus.Queued),
            tasks.Take(ListLimit).ToArray(),
            tasks.Count(operation => operation.Status == OperationStatus.Running),
            tasks.Count(operation => operation.Status == OperationStatus.Queued));
    }

    private async Task<DashboardWaiting> ReadWaitingAsync(AdminOverviewSnapshot? counts, CancellationToken cancellationToken)
    {
        var open = await acquisitionAccess.ListAsync(
            kind: null,
            requestedByProfileId: null,
            openOnly: true,
            limit: ActiveScanLimit,
            cancellationToken);
        var page = AdminWantedQuery.Build(await wanted.LoadAsync(cancellationToken), new AdminWantedFilter());

        return new DashboardWaiting(
            open.Count(request => request.Status == AcquisitionRequestStatus.Pending),
            page.TabCounts[AdminWantedTab.All],
            page.TabCounts[AdminWantedTab.Failed],
            counts?.UnresolvedMappings ?? 0,
            counts?.MissingLearningText ?? 0);
    }

    private static void AddOperationProblems(List<DashboardProblem> problems, AdminOverviewSnapshot counts)
    {
        if (counts.FailedOperations > 0)
        {
            problems.Add(new DashboardProblem(
                DashboardSeverity.Error,
                "jobsFailed",
                AdminActivityQuery.Href("/Admin/Operations", new AdminActivityFilter(AdminActivityTab.Failed, Status: OperationStatus.Failed)),
                Count: counts.FailedOperations));
        }

        if (counts.BlockedJobs > 0)
        {
            problems.Add(new DashboardProblem(
                DashboardSeverity.Warning,
                "jobsInterrupted",
                AdminActivityQuery.Href("/Admin/Operations", new AdminActivityFilter(AdminActivityTab.Failed, Status: OperationStatus.Interrupted)),
                Count: counts.BlockedJobs));
        }
    }

    private async Task<T?> TryAsync<T>(string section, Func<Task<T>> read)
        where T : class
    {
        try
        {
            return await read();
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            logger.LogWarning(exception, "The dashboard section {Section} could not be read.", section);
            return null;
        }
    }

    private static bool IsExpected(Exception exception) =>
        exception is DbException or InvalidOperationException or IOException or JsonException
            or InvalidDataException or UnauthorizedAccessException or TimeoutException or FormatException
            or NotSupportedException or System.Security.Cryptography.CryptographicException;
}
