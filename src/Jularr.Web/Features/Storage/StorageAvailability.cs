using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Jularr.Web.Data;
using Jularr.Web.Features.Events;
using Jularr.Web.Features.Library;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Web.Features.Storage;

public enum StorageAvailabilityState
{
    Unknown,
    Available,
    Starting,
    Offline,
    Unreachable,
    FileMissing
}

public sealed record LibraryRootAvailabilitySnapshot(
    Guid RootId,
    StorageAvailabilityState State,
    DateTimeOffset CheckedAtUtc,
    DateTimeOffset? LastAvailableAtUtc,
    bool WakeConfigured,
    string? DiagnosticCode = null)
{
    public bool IsAvailable => State == StorageAvailabilityState.Available;
    public bool IsRetryable =>
        State is StorageAvailabilityState.Unknown
            or StorageAvailabilityState.Starting
            or StorageAvailabilityState.Offline;

    public StorageHealthState Health =>
        StorageHealth.Resolve(State, WakeConfigured, DiagnosticCode);

    // Free space of the volume behind the root, measured by the last successful probe.
    public long? FreeSpaceBytes { get; init; }
}

public sealed record MediaAvailabilitySnapshot(
    Guid MediaFileId,
    Guid RootId,
    StorageAvailabilityState State,
    bool Retryable,
    int RetryAfterMs,
    bool WakeConfigured,
    DateTimeOffset CheckedAtUtc)
{
    public bool IsAvailable => State == StorageAvailabilityState.Available;

    // The root's diagnostic code (for example a failed start attempt).
    public string? DiagnosticCode { get; init; }

    public StorageHealthState Health =>
        StorageHealth.Resolve(State, WakeConfigured, DiagnosticCode);
}

public sealed record WakeOnLanResult(
    bool Accepted,
    bool AlreadyStarting,
    string Message,
    LibraryRootAvailabilitySnapshot? Availability);

public static class PlaybackAvailabilityRetry
{
    public static readonly IReadOnlyList<TimeSpan> Delays =
    [
        TimeSpan.Zero,
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(4),
        TimeSpan.FromSeconds(5)
    ];

    public static TimeSpan MaximumAutomaticRetryWindow => TimeSpan.FromSeconds(60);

    public static TimeSpan DelayForAttempt(int attempt) =>
        attempt < 0
            ? TimeSpan.Zero
            : attempt < Delays.Count
                ? Delays[attempt]
                : Delays[^1];
}

public sealed class StorageAvailabilityCoordinator
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan OnlineCache = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan OfflineCache = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan DefaultWakeStartingWindow = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan WakeDebounce = TimeSpan.FromSeconds(10);

    private readonly ConcurrentDictionary<Guid, RootRuntimeState> roots = new();

    public async Task<LibraryRootAvailabilitySnapshot> ProbeAsync(
        Guid rootId,
        string path,
        bool wakeConfigured,
        bool force,
        CancellationToken cancellationToken,
        bool expectedNonEmpty = false)
    {
        var runtime = roots.GetOrAdd(rootId, _ => new RootRuntimeState());
        var now = DateTimeOffset.UtcNow;
        Task<RootProbeResult> probeTask;

        lock (runtime.Gate)
        {
            if (!force &&
                runtime.Last is { } cached &&
                now < runtime.CacheUntilUtc)
            {
                return DecorateStarting(runtime, cached, wakeConfigured, now);
            }

            if (runtime.ProbeTask is null || runtime.ProbeTask.IsCompleted)
            {
                runtime.ProbeTask = Task.Run(() => ProbePath(path, expectedNonEmpty));
            }

            probeTask = runtime.ProbeTask;
        }

        RootProbeResult result;
        try
        {
            result = await probeTask.WaitAsync(ProbeTimeout, cancellationToken);
        }
        catch (TimeoutException)
        {
            lock (runtime.Gate)
            {
                var state = runtime.StartingUntilUtc is { } startingUntil && startingUntil > now
                    ? StorageAvailabilityState.Starting
                    : StorageAvailabilityState.Offline;

                var timedOut = new LibraryRootAvailabilitySnapshot(
                    rootId,
                    state,
                    now,
                    runtime.LastAvailableAtUtc,
                    wakeConfigured,
                    runtime.StartFailureCode ?? "probe_timeout");

                runtime.Last = timedOut;
                runtime.CacheUntilUtc = now + OfflineCache;
                return timedOut;
            }
        }

        lock (runtime.Gate)
        {
            now = DateTimeOffset.UtcNow;
            var state = result.State;

            var diagnosticCode = result.DiagnosticCode;
            if (state == StorageAvailabilityState.Available)
            {
                runtime.LastAvailableAtUtc = now;
                runtime.StartingUntilUtc = null;
                runtime.LastWakeAtUtc = null;
                runtime.StartFailureCode = null;
            }
            else if (runtime.StartingUntilUtc is { } startingUntil && startingUntil > now)
            {
                state = StorageAvailabilityState.Starting;
            }
            else if (runtime.StartFailureCode is { } failure)
            {
                // A failed start is the actual problem until the storage is readable again.
                diagnosticCode = failure;
            }

            var snapshot = new LibraryRootAvailabilitySnapshot(
                rootId,
                state,
                now,
                runtime.LastAvailableAtUtc,
                wakeConfigured,
                diagnosticCode)
            {
                FreeSpaceBytes = result.FreeSpaceBytes
            };

            runtime.Last = snapshot;
            runtime.CacheUntilUtc = now +
                (state == StorageAvailabilityState.Available ? OnlineCache : OfflineCache);

            return snapshot;
        }
    }

    // Marks the root as starting; false when a wake packet was sent moments ago (the caller
    // then waits for that one instead of sending another).
    public bool TryMarkWakeStarting(Guid rootId, TimeSpan? startingWindow = null)
    {
        var runtime = roots.GetOrAdd(rootId, _ => new RootRuntimeState());
        var now = DateTimeOffset.UtcNow;

        lock (runtime.Gate)
        {
            if (runtime.LastWakeAtUtc is { } lastWake &&
                now - lastWake < WakeDebounce)
            {
                return false;
            }

            runtime.LastWakeAtUtc = now;
            runtime.StartingUntilUtc = now + (startingWindow ?? DefaultWakeStartingWindow);
            runtime.StartFailureCode = null;
            runtime.CacheUntilUtc = DateTimeOffset.MinValue;
            return true;
        }
    }

    // Ends the starting state. With a failure code (see StorageDiagnosticCodes) the root
    // reports that error until it is readable again or a new start attempt begins.
    public void MarkWakeFailed(Guid rootId, string? failureCode = null)
    {
        var runtime = failureCode is null
            ? roots.GetValueOrDefault(rootId)
            : roots.GetOrAdd(rootId, _ => new RootRuntimeState());
        if (runtime is null)
        {
            return;
        }

        lock (runtime.Gate)
        {
            runtime.StartingUntilUtc = null;
            runtime.LastWakeAtUtc = null;
            runtime.StartFailureCode = failureCode;
            runtime.CacheUntilUtc = DateTimeOffset.MinValue;
        }
    }

    public LibraryRootAvailabilitySnapshot? GetCached(Guid rootId, bool wakeConfigured)
    {
        if (!roots.TryGetValue(rootId, out var runtime))
        {
            return null;
        }

        lock (runtime.Gate)
        {
            var now = DateTimeOffset.UtcNow;
            var last = runtime.Last;
            if (last is null)
            {
                // Starting before the first probe finished.
                if (runtime.StartingUntilUtc is not { } startingUntil || startingUntil <= now)
                {
                    return null;
                }

                last = new LibraryRootAvailabilitySnapshot(
                    rootId,
                    StorageAvailabilityState.Starting,
                    now,
                    runtime.LastAvailableAtUtc,
                    wakeConfigured);
            }

            return DecorateStarting(
                runtime,
                last,
                wakeConfigured,
                now);
        }
    }

    private static LibraryRootAvailabilitySnapshot DecorateStarting(
        RootRuntimeState runtime,
        LibraryRootAvailabilitySnapshot snapshot,
        bool wakeConfigured,
        DateTimeOffset now)
    {
        if (snapshot.State != StorageAvailabilityState.Available &&
            runtime.StartingUntilUtc is { } startingUntil &&
            startingUntil > now)
        {
            return snapshot with
            {
                State = StorageAvailabilityState.Starting,
                WakeConfigured = wakeConfigured
            };
        }

        if (snapshot.State == StorageAvailabilityState.Starting)
        {
            // The start window passed since this snapshot was taken.
            snapshot = snapshot with { State = StorageAvailabilityState.Offline };
        }

        if (snapshot.State != StorageAvailabilityState.Available &&
            runtime.StartFailureCode is { } failure)
        {
            return snapshot with
            {
                WakeConfigured = wakeConfigured,
                DiagnosticCode = failure
            };
        }

        return snapshot with { WakeConfigured = wakeConfigured };
    }

    private static RootProbeResult ProbePath(
        string path,
        bool expectedNonEmpty)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            if (!Directory.Exists(fullPath))
            {
                return new RootProbeResult(
                    StorageAvailabilityState.Offline,
                    "root_not_found");
            }

            using var enumerator = Directory
                .EnumerateFileSystemEntries(fullPath, "*", SearchOption.TopDirectoryOnly)
                .GetEnumerator();
            var hasEntries = enumerator.MoveNext();

            if (!hasEntries && expectedNonEmpty)
            {
                return new RootProbeResult(
                    StorageAvailabilityState.Offline,
                    "unexpectedly_empty_root");
            }

            return new RootProbeResult(
                StorageAvailabilityState.Available,
                hasEntries ? null : "root_empty",
                TryGetFreeSpace(fullPath));
        }
        catch (UnauthorizedAccessException)
        {
            return new RootProbeResult(
                StorageAvailabilityState.Unreachable,
                "permission_denied");
        }
        catch (IOException)
        {
            return new RootProbeResult(
                StorageAvailabilityState.Offline,
                "io_unavailable");
        }
        catch (Exception) when (
            !System.Diagnostics.Debugger.IsAttached)
        {
            return new RootProbeResult(
                StorageAvailabilityState.Unreachable,
                "probe_failed");
        }
    }

    // Unix reports the volume of any path; Windows only of a drive root (UNC shares: none).
    private static long? TryGetFreeSpace(string fullPath)
    {
        try
        {
            var drive = new DriveInfo(
                OperatingSystem.IsWindows()
                    ? Path.GetPathRoot(fullPath) ?? fullPath
                    : fullPath);
            return drive.IsReady ? drive.AvailableFreeSpace : null;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private sealed record RootProbeResult(
        StorageAvailabilityState State,
        string? DiagnosticCode,
        long? FreeSpaceBytes = null);

    private sealed class RootRuntimeState
    {
        public object Gate { get; } = new();
        public Task<RootProbeResult>? ProbeTask { get; set; }
        public LibraryRootAvailabilitySnapshot? Last { get; set; }
        public DateTimeOffset CacheUntilUtc { get; set; }
        public DateTimeOffset? LastAvailableAtUtc { get; set; }
        public DateTimeOffset? StartingUntilUtc { get; set; }
        public DateTimeOffset? LastWakeAtUtc { get; set; }
        public string? StartFailureCode { get; set; }
    }
}

// CheckAsync only observes storage and never wakes it: browsing, cached artwork, metadata
// pages, health polling and background maintenance use it. RequireAsync is for work that
// needs the media now (play, open, download-dependent import) and may start a sleeping NAS.
public sealed class LibraryRootAvailabilityService(
    AppDbContext db,
    StorageAvailabilityCoordinator coordinator,
    StorageWakeCoordinator? wake = null,
    IJularrEventPublisher? events = null)
{
    // #579 StorageProblem: this service is scoped (a fresh instance per request/background pass),
    // so "did the last probe of this root already report a problem" is tracked process-wide here
    // instead of on the instance — otherwise every single probe would publish again. Keyed by root
    // id; a transition back to available simply clears the flag (no "recovered" event is required).
    private static readonly ConcurrentDictionary<Guid, bool> LastProbeWasProblem = new();

    public async Task<LibraryRootAvailabilitySnapshot?> CheckAsync(
        Guid rootId,
        bool force,
        CancellationToken cancellationToken)
    {
        var root = await LoadAsync(rootId, cancellationToken);
        if (root is null)
        {
            return null;
        }

        var snapshot = await coordinator.ProbeAsync(
            root.Id,
            root.Path,
            root.WakeConfigured,
            force,
            cancellationToken,
            root.ExpectedNonEmpty);
        await PublishIfNewlyUnavailableAsync(root, snapshot, cancellationToken);
        return snapshot;
    }

    // An import needs its destination storage now: a sleeping Wake-on-LAN NAS is started and the import waits for the bounded start
    // attempt. A root whose folder does not exist yet is created when that is safe: nothing is known to live there, no wake target is
    // configured and the parent exists, so it is a new folder rather than an unmounted share. False when the root is gone or still not
    // readable, so no file is written or moved onto an unmounted mount point.
    public async Task<bool> IsReadyForImportAsync(Guid rootId, CancellationToken cancellationToken)
    {
        var status = await RequireAsync(rootId, waitForStart: true, cancellationToken);
        if (status is { DiagnosticCode: "root_not_found" } && await TryCreateNewRootFolderAsync(rootId, cancellationToken))
        {
            status = await CheckAsync(rootId, force: true, cancellationToken);
        }

        return status is { IsAvailable: true };
    }

    private async Task<bool> TryCreateNewRootFolderAsync(Guid rootId, CancellationToken cancellationToken)
    {
        if (await LoadAsync(rootId, cancellationToken) is not { ExpectedNonEmpty: false, WakeConfigured: false } root)
        {
            return false;
        }

        try
        {
            var fullPath = Path.GetFullPath(root.Path);
            if (Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(fullPath)) is not { } parent || !Directory.Exists(parent))
            {
                return false;
            }

            Directory.CreateDirectory(fullPath);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    // Returns the root's state for a media-dependent operation. A readable root is returned as
    // is; a Wake-on-LAN root that is not readable is woken through the shared start attempt of
    // StorageWakeCoordinator (concurrent callers join one attempt). With waitForStart the call
    // returns once the storage is online or the bounded start attempt gave up; otherwise it
    // returns the Starting state at once and the caller polls.
    public async Task<LibraryRootAvailabilitySnapshot?> RequireAsync(
        Guid rootId,
        bool waitForStart,
        CancellationToken cancellationToken)
    {
        var root = await LoadAsync(rootId, cancellationToken);
        if (root is null)
        {
            return null;
        }

        // A cached "online" is trusted (every range request of a stream lands here); a cached
        // "offline" is re-checked because the storage may have come back meanwhile.
        var current = await coordinator.ProbeAsync(
            root.Id,
            root.Path,
            root.WakeConfigured,
            force: false,
            cancellationToken,
            root.ExpectedNonEmpty);
        if (!current.IsAvailable && current.State != StorageAvailabilityState.Starting)
        {
            current = await coordinator.ProbeAsync(
                root.Id,
                root.Path,
                root.WakeConfigured,
                force: true,
                cancellationToken,
                root.ExpectedNonEmpty);
        }

        await PublishIfNewlyUnavailableAsync(root, current, cancellationToken);

        if (current.IsAvailable ||
            current.State == StorageAvailabilityState.Unreachable ||
            wake is null ||
            root.WakeTarget is not { } target)
        {
            return current;
        }

        var attempt = wake.StartAsync(target);
        if (waitForStart)
        {
            return await attempt.WaitAsync(cancellationToken);
        }

        return coordinator.GetCached(root.Id, root.WakeConfigured) ?? current;
    }

    private async Task<RootAccess?> LoadAsync(
        Guid rootId,
        CancellationToken cancellationToken)
    {
        var root = await db.LibraryRoots
            .AsNoTracking()
            .Where(x => x.Id == rootId)
            .Select(x => new
            {
                x.Id,
                x.Path,
                x.WakeOnLanEnabled,
                x.WakeMacAddress,
                x.WakeBroadcastAddress
            })
            .SingleOrDefaultAsync(cancellationToken);

        if (root is null)
        {
            return null;
        }

        string? mac = null;
        var wakeConfigured =
            root.WakeOnLanEnabled &&
            WakeOnLanService.TryNormalizeMacAddress(
                root.WakeMacAddress,
                out mac);

        var expectedNonEmpty = await db.MediaFiles
            .AsNoTracking()
            .AnyAsync(
                x => x.LibraryRootId == root.Id,
                cancellationToken);

        var target = wakeConfigured &&
            WakeOnLanService.TryResolveBroadcastEndpoint(
                root.WakeBroadcastAddress,
                out var broadcast)
            ? new StorageWakeTarget(root.Id, root.Path, mac!, broadcast!, expectedNonEmpty)
            : null;

        return new RootAccess(root.Id, root.Path, wakeConfigured, expectedNonEmpty, target);
    }

    /// <summary>
    /// #579 StorageProblem: publishes only on the transition into a non-"Starting" unavailable
    /// state (a sleeping Wake-on-LAN root waking up is not a problem), so this fires once per
    /// outage rather than on every poll. <see cref="JularrEventCategories"/> already routes
    /// StorageProblem to Admin (owner/media manager), never a normal profile.
    /// </summary>
    private Task PublishIfNewlyUnavailableAsync(
        RootAccess root,
        LibraryRootAvailabilitySnapshot? snapshot,
        CancellationToken cancellationToken)
    {
        if (events is null || snapshot is null)
        {
            return Task.CompletedTask;
        }

        var isProblem = !snapshot.IsAvailable && snapshot.State != StorageAvailabilityState.Starting;
        var wasProblem = LastProbeWasProblem.GetValueOrDefault(root.Id);
        LastProbeWasProblem[root.Id] = isProblem;

        if (!isProblem || wasProblem)
        {
            return Task.CompletedTask;
        }

        var message = $"{root.Path} is unavailable ({snapshot.DiagnosticCode ?? snapshot.State.ToString()}).";
        return events.PublishAsync(
            JularrEvent.Create(
                JularrEventCategory.StorageProblem,
                subjectId: root.Id.ToString(),
                messageParams: new Dictionary<string, string> { ["message"] = message },
                dedupKey: $"storage-root:{root.Id}"),
            cancellationToken);
    }

    private sealed record RootAccess(
        Guid Id,
        string Path,
        bool WakeConfigured,
        bool ExpectedNonEmpty,
        StorageWakeTarget? WakeTarget);

    public LibraryRootAvailabilitySnapshot? GetCached(
        LibraryRoot root) =>
        coordinator.GetCached(
            root.Id,
            root.WakeOnLanEnabled &&
            WakeOnLanService.TryNormalizeMacAddress(
                root.WakeMacAddress,
                out _));
}

public sealed class MediaAvailabilityService(
    AppDbContext db,
    LibraryRootAvailabilityService roots)
{
    // wake: the caller needs the media now (play, open, download), so a sleeping
    // Wake-on-LAN root is started; the result is Starting until it is readable.
    public async Task<MediaAvailabilitySnapshot?> CheckMediaAsync(
        Guid mediaFileId,
        bool force,
        CancellationToken cancellationToken,
        bool wake = false)
    {
        var media = await db.MediaFiles
            .AsNoTracking()
            .Where(x => x.Id == mediaFileId)
            .Select(x => new
            {
                x.Id,
                x.LibraryRootId,
                x.Path
            })
            .SingleOrDefaultAsync(cancellationToken);

        if (media is null)
        {
            return null;
        }

        var root = wake
            ? await roots.RequireAsync(
                media.LibraryRootId,
                waitForStart: false,
                cancellationToken)
            : await roots.CheckAsync(
                media.LibraryRootId,
                force,
                cancellationToken);

        if (root is null)
        {
            return new MediaAvailabilitySnapshot(
                media.Id,
                media.LibraryRootId,
                StorageAvailabilityState.Unreachable,
                false,
                0,
                false,
                DateTimeOffset.UtcNow);
        }

        if (!root.IsAvailable)
        {
            return new MediaAvailabilitySnapshot(
                media.Id,
                media.LibraryRootId,
                root.State,
                root.IsRetryable,
                root.State == StorageAvailabilityState.Starting ? 1000 : 2000,
                root.WakeConfigured,
                root.CheckedAtUtc)
            {
                DiagnosticCode = root.DiagnosticCode
            };
        }

        var exists = false;
        try
        {
            exists = File.Exists(media.Path);
        }
        catch
        {
        }

        return new MediaAvailabilitySnapshot(
            media.Id,
            media.LibraryRootId,
            exists
                ? StorageAvailabilityState.Available
                : StorageAvailabilityState.FileMissing,
            false,
            0,
            root.WakeConfigured,
            DateTimeOffset.UtcNow);
    }

    public async Task<MediaAvailabilitySnapshot?> CheckEpisodeAsync(
        Guid episodeId,
        bool force,
        CancellationToken cancellationToken,
        bool wake = false)
    {
        var mediaFileId = await db.MediaFiles
            .AsNoTracking()
            .Where(x => x.EpisodeId == episodeId)
            .OrderBy(x => x.Path)
            .Select(x => (Guid?)x.Id)
            .FirstOrDefaultAsync(cancellationToken);

        return mediaFileId is null
            ? null
            : await CheckMediaAsync(
                mediaFileId.Value,
                force,
                cancellationToken,
                wake);
    }
}

// The owner's explicit "Wake NAS" action. It joins the same coalesced start attempt as
// on-demand wakes from playback, so it never sends a second packet beside one.
public sealed class WakeOnLanService(
    AppDbContext db,
    LibraryRootAvailabilityService availability,
    StorageWakeCoordinator wake)
{
    public async Task<WakeOnLanResult> WakeAsync(
        Guid rootId,
        CancellationToken cancellationToken)
    {
        var root = await db.LibraryRoots
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == rootId, cancellationToken);

        if (root is null)
        {
            return new WakeOnLanResult(
                false,
                false,
                "Library root was not found.",
                null);
        }

        if (!root.WakeOnLanEnabled ||
            !TryNormalizeMacAddress(root.WakeMacAddress, out _))
        {
            return new WakeOnLanResult(
                false,
                false,
                "Wake-on-LAN is not configured for this library root.",
                await availability.CheckAsync(rootId, false, cancellationToken));
        }

        if (!TryResolveBroadcastEndpoint(
                root.WakeBroadcastAddress,
                out _))
        {
            return new WakeOnLanResult(
                false,
                false,
                "The configured Wake-on-LAN broadcast address is invalid.",
                await availability.CheckAsync(rootId, false, cancellationToken));
        }

        var alreadyStarting = wake.IsStarting(rootId);
        var current = await availability.RequireAsync(
            rootId,
            waitForStart: false,
            cancellationToken);

        if (current is { IsAvailable: true })
        {
            return new WakeOnLanResult(
                true,
                false,
                "Media storage is already online.",
                current);
        }

        if (current is { DiagnosticCode: StorageDiagnosticCodes.WakeSendFailed })
        {
            return new WakeOnLanResult(
                false,
                false,
                "Wake-on-LAN could not be sent from the Jularr container. Check the configured LAN broadcast address and Docker networking.",
                current);
        }

        return new WakeOnLanResult(
            true,
            alreadyStarting,
            alreadyStarting
                ? "Wake-on-LAN was already requested recently."
                : "Wake-on-LAN sent. Waiting for media storage.",
            current);
    }

    public static bool TryNormalizeMacAddress(
        string? value,
        out string? normalized)
    {
        normalized = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var compact = new string(
            value.Where(Uri.IsHexDigit).ToArray());

        if (compact.Length != 12)
        {
            return false;
        }

        try
        {
            var physical = PhysicalAddress.Parse(compact);
            var bytes = physical.GetAddressBytes();
            if (bytes.Length != 6)
            {
                return false;
            }

            normalized = string.Join(
                ":",
                bytes.Select(x => x.ToString("X2")));
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    // Standard Wake-on-LAN UDP port used when the owner's broadcast setting omits one.
    public const int DefaultBroadcastPort = 9;

    // The owner's WakeBroadcastAddress setting resolves to an endpoint instead of a bare
    // address so the same field can carry a directed LAN broadcast plus a non-default port
    // (e.g. "192.168.1.255:7"). This is the one canonical parse of that field: every caller
    // (validation, wake, diagnostics) goes through it instead of re-deriving a broadcast target.
    //
    // The default (no value configured) is the limited broadcast 255.255.255.255. That default
    // only reaches devices on the same L2 segment as the sender: from inside a Docker bridge
    // network the packet never leaves the container's bridge subnet, so it never reaches a NAS
    // on the physical LAN. An explicit directed subnet broadcast (e.g. 192.168.1.255) is the
    // supported way to make Wake-on-LAN work from a bridge-networked container without
    // requiring host networking; see docs/ADMIN_OPERATIONS.md.
    public static bool TryResolveBroadcastEndpoint(
        string? value,
        out IPEndPoint? endpoint)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            endpoint = new IPEndPoint(IPAddress.Broadcast, DefaultBroadcastPort);
            return true;
        }

        var trimmed = value.Trim();
        var separatorIndex = trimmed.LastIndexOf(':');
        var hostPart = separatorIndex < 0 ? trimmed : trimmed[..separatorIndex];
        var portPart = separatorIndex < 0 ? null : trimmed[(separatorIndex + 1)..];

        var port = DefaultBroadcastPort;
        if (portPart is not null &&
            (!int.TryParse(portPart, out port) || port is <= 0 or > 65535))
        {
            endpoint = null;
            return false;
        }

        if (!IPAddress.TryParse(hostPart, out var parsed) ||
            parsed.AddressFamily != AddressFamily.InterNetwork)
        {
            endpoint = null;
            return false;
        }

        endpoint = new IPEndPoint(parsed, port);
        return true;
    }

    public static byte[] BuildMagicPacket(string normalizedMacAddress)
    {
        if (!TryNormalizeMacAddress(
                normalizedMacAddress,
                out var normalized))
        {
            throw new ArgumentException(
                "A valid 6-byte MAC address is required.",
                nameof(normalizedMacAddress));
        }

        var mac = PhysicalAddress
            .Parse(normalized!.Replace(":", "", StringComparison.Ordinal))
            .GetAddressBytes();

        var packet = new byte[6 + (16 * mac.Length)];
        Array.Fill(packet, (byte)0xFF, 0, 6);

        for (var i = 0; i < 16; i++)
        {
            Buffer.BlockCopy(
                mac,
                0,
                packet,
                6 + (i * mac.Length),
                mac.Length);
        }

        return packet;
    }
}
