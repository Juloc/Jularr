using System.Collections.Concurrent;
using Jularr.Web.Features.Playback.Transcoding;

namespace Jularr.Web.Features.Playback.Decision;

/// <summary>
/// Bounds concurrent server deliveries per <see cref="PlaybackCostClass"/>. A lease is held for
/// the lifetime of one live delivery (a progressive response or an HLS session) and released
/// when it ends. The limits are the Admin-editable settings, read on every acquisition, so a
/// lowered limit only stops new deliveries and never kills a running one.
/// </summary>
public sealed class PlaybackTranscodeSlots(PlaybackTranscodingSettingsStore settings)
{
    /// <summary>How many deliveries one profile may run at once across all cost classes (two HLS sessions plus a progressive stream or a seek restart in flight).</summary>
    public const int MaxPerProfile = 3;

    private readonly Lock _gate = new();
    private readonly int[] _active = new int[Enum.GetValues<PlaybackCostClass>().Length];
    private readonly Dictionary<string, int> _perProfile = [];

    public int Capacity(PlaybackCostClass costClass) => settings.Current.LimitFor(costClass);

    public int Active(PlaybackCostClass costClass)
    {
        lock (_gate)
        {
            return _active[(int)costClass];
        }
    }

    public int Available(PlaybackCostClass costClass) => Math.Max(0, Capacity(costClass) - Active(costClass));

    public int ActiveFor(string profileId)
    {
        lock (_gate)
        {
            return _perProfile.GetValueOrDefault(profileId);
        }
    }

    /// <summary>Takes a slot of the class, and one of the profile's <see cref="MaxPerProfile"/>; null when either is exhausted.</summary>
    public IDisposable? TryAcquire(PlaybackCostClass costClass, string? profileId = null)
    {
        lock (_gate)
        {
            if (_active[(int)costClass] >= Capacity(costClass) || (profileId is not null && _perProfile.GetValueOrDefault(profileId) >= MaxPerProfile))
            {
                return null;
            }

            _active[(int)costClass]++;
            if (profileId is not null)
            {
                _perProfile[profileId] = _perProfile.GetValueOrDefault(profileId) + 1;
            }

            return new Lease(this, costClass, profileId);
        }
    }

    private sealed class Lease(PlaybackTranscodeSlots owner, PlaybackCostClass costClass, string? profileId) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0)
            {
                return;
            }

            lock (owner._gate)
            {
                owner._active[(int)costClass]--;
                if (profileId is not null && owner._perProfile.TryGetValue(profileId, out var count))
                {
                    if (count <= 1)
                    {
                        owner._perProfile.Remove(profileId);
                    }
                    else
                    {
                        owner._perProfile[profileId] = count - 1;
                    }
                }
            }
        }
    }
}

/// <summary>
/// One playback session: who plays what, with which selections, under which resolved plan.
/// The source path is resolved by the server when the plan is made; clients only ever hold
/// the session id. Position and diagnostics are the live view; durable progress stays in the
/// canonical progress store.
/// </summary>
public sealed class PlaybackStreamSession(
    Guid id,
    string profileId,
    PlaybackVideoTarget target,
    Guid? legacyEpisodeId,
    Guid mediaFileId,
    string sourcePath,
    double? durationSeconds,
    PlaybackPlan plan,
    PlaybackStreamSelections selections,
    DateTimeOffset createdAtUtc)
{
    private readonly object gate = new();

    public Guid Id { get; } = id;
    public string ProfileId { get; } = profileId;
    public PlaybackVideoTarget Target { get; } = target;
    public Guid WorkId => Target.WorkId;
    public Guid? WorkEpisodeId => Target.WorkEpisodeId;
    public Guid TargetId => Target.IdentityId;
    public Guid? LegacyEpisodeId { get; } = legacyEpisodeId;

    /// <summary>
    /// Compatibility identity for old Anime callers. New code uses <see cref="Target"/>.
    /// For canonical callers without a legacy Anime id this resolves to the canonical target id.
    /// </summary>
    public Guid EpisodeId => LegacyEpisodeId ?? TargetId;

    public Guid MediaFileId { get; } = mediaFileId;
    public string SourcePath { get; } = sourcePath;
    public double? DurationSeconds { get; } = durationSeconds;
    public PlaybackPlan Plan { get; } = plan;
    public PlaybackStreamSelections Selections { get; } = selections;
    public DateTimeOffset CreatedAtUtc { get; } = createdAtUtc;
    public DateTimeOffset LastSeenUtc { get; private set; } = createdAtUtc;
    public Guid? HlsSessionId { get; private set; }
    public double? HlsStartSeconds { get; private set; }

    /// <summary>What the player reported about this session's playback; ephemeral, never persisted.</summary>
    public PlaybackSessionTelemetry Telemetry { get; } = new();

    public void Touch(DateTimeOffset now)
    {
        lock (gate)
        {
            LastSeenUtc = now;
        }
    }

    /// <summary>
    /// The running HLS output when it started at (about) the requested position. Players
    /// request the stream URL more than once (probing, playlist reloads); only a new
    /// position may restart ffmpeg.
    /// </summary>
    public Guid? HlsSessionAt(double startSeconds)
    {
        lock (gate)
        {
            return HlsSessionId is { } id &&
                   HlsStartSeconds is { } start &&
                   Math.Abs(start - startSeconds) < 0.5
                ? id
                : null;
        }
    }

    public Guid? ReplaceHlsSession(Guid? hlsSessionId, double? startSeconds = null)
    {
        lock (gate)
        {
            var previous = HlsSessionId;
            HlsSessionId = hlsSessionId;
            HlsStartSeconds = hlsSessionId is null ? null : startSeconds;
            return previous;
        }
    }

    /// <summary>
    /// Idempotent HLS start per position. The running output is reused when it started at
    /// (about) the requested position and is still alive; otherwise the previous output is
    /// stopped and exactly one new output is started, even when a player fires several
    /// requests at once (probe, playlist reload, a quick double seek). <paramref name="start"/>
    /// returns null when it cannot start (e.g. no free transcode slot).
    /// </summary>
    public async Task<Guid?> EnsureHlsAsync(
        double startSeconds,
        Func<Guid, bool> isActive,
        Func<CancellationToken, Task<Guid?>> start,
        Action<Guid> stop,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(isActive);
        ArgumentNullException.ThrowIfNull(start);
        ArgumentNullException.ThrowIfNull(stop);

        await hlsStartGate.WaitAsync(cancellationToken);
        try
        {
            if (HlsSessionAt(startSeconds) is { } running && isActive(running))
            {
                return running;
            }

            if (ReplaceHlsSession(null) is { } previous)
            {
                stop(previous);
            }

            var started = await start(cancellationToken);
            if (started is { } id)
            {
                ReplaceHlsSession(id, startSeconds);
            }

            return started;
        }
        finally
        {
            hlsStartGate.Release();
        }
    }

    private readonly SemaphoreSlim hlsStartGate = new(1, 1);
}

/// <summary>The session's selections, kept so a re-plan (fallback, quality change) starts from them.</summary>
public sealed record PlaybackStreamSelections(
    int? AudioStreamIndex,
    int? SubtitleStreamIndex,
    bool BurnInSubtitle,
    PlaybackQualityPreset Quality,
    PlaybackModePreference ModePreference,
    string ClientKind);

public sealed class PlaybackStreamSessionStore(TimeProvider time)
{
    public const int MaxSessions = 64;
    public const int MaxSessionsPerProfile = 6;
    public static readonly TimeSpan IdleLifetime = TimeSpan.FromMinutes(30);

    private readonly ConcurrentDictionary<Guid, PlaybackStreamSession> sessions = new();
    private readonly object gate = new();

    /// <summary>Invoked with a removed session so its delivery (an HLS session) can be stopped.</summary>
    public event Action<PlaybackStreamSession>? Removed;

    public int Count => sessions.Count;

    /// <summary>Legacy Anime/test compatibility overload. New playback orchestration supplies a canonical target.</summary>
    public PlaybackStreamSession Create(
        string profileId,
        Guid episodeId,
        Guid mediaFileId,
        string sourcePath,
        double? durationSeconds,
        PlaybackPlan plan,
        PlaybackStreamSelections selections,
        Guid? replaces = null) =>
        Create(
            profileId,
            new PlaybackVideoTarget(episodeId, episodeId),
            mediaFileId,
            sourcePath,
            durationSeconds,
            plan,
            selections,
            replaces,
            episodeId);

    public PlaybackStreamSession Create(
        string profileId,
        PlaybackVideoTarget target,
        Guid mediaFileId,
        string sourcePath,
        double? durationSeconds,
        PlaybackPlan plan,
        PlaybackStreamSelections selections,
        Guid? replaces = null,
        Guid? legacyEpisodeId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        ArgumentNullException.ThrowIfNull(target);
        var now = time.GetUtcNow();
        var removed = new List<PlaybackStreamSession>();
        PlaybackStreamSession session;
        lock (gate)
        {
            if (replaces is { } previousId &&
                sessions.TryGetValue(previousId, out var previous) &&
                previous.ProfileId == profileId &&
                sessions.TryRemove(previousId, out _))
            {
                removed.Add(previous);
            }

            removed.AddRange(RemoveExpired(now));
            while (sessions.Values.Count(x => x.ProfileId == profileId) >= MaxSessionsPerProfile &&
                   RemoveOldest(x => x.ProfileId == profileId) is { } oldestOfProfile)
            {
                removed.Add(oldestOfProfile);
            }

            while (sessions.Count >= MaxSessions && RemoveOldest(_ => true) is { } oldest)
            {
                removed.Add(oldest);
            }

            session = new PlaybackStreamSession(
                Guid.NewGuid(),
                profileId,
                target,
                legacyEpisodeId,
                mediaFileId,
                sourcePath,
                durationSeconds,
                plan,
                selections,
                now);
            sessions[session.Id] = session;
        }

        foreach (var item in removed)
        {
            Removed?.Invoke(item);
        }

        return session;
    }

    /// <summary>
    /// The session of the profile without counting as activity, so that polling a status never keeps an abandoned
    /// session alive. Expired sessions are not returned (the cleanup removes them).
    /// </summary>
    public PlaybackStreamSession? Peek(Guid sessionId, string profileId) =>
        sessions.TryGetValue(sessionId, out var session) &&
        string.Equals(session.ProfileId, profileId, StringComparison.Ordinal) &&
        time.GetUtcNow() - session.LastSeenUtc <= IdleLifetime
            ? session
            : null;

    /// <summary>
    /// Stores a telemetry report of the profile's session; false when the session is not the profile's or expired. A player that
    /// plays on or keeps waiting for media is using the session, so a report showing that counts as activity like a stream request does.
    /// A paused player, a repeated or older report and a report that shows no progress (see <see cref="PlaybackSessionTelemetry.Apply"/>) do not:
    /// telemetry never keeps an abandoned session, its slot and its process alive.
    /// </summary>
    public bool ReportTelemetry(Guid sessionId, string profileId, PlaybackTelemetry report)
    {
        var session = Peek(sessionId, profileId);
        if (session is null)
        {
            return false;
        }

        if (session.Telemetry.Apply(report) == PlaybackTelemetryOutcome.ShowsProgress)
        {
            session.Touch(time.GetUtcNow());
        }

        return true;
    }

    /// <summary>The runtime evidence the session's player reported, as of now; null when it never reported.</summary>
    public PlaybackTelemetryEvidence? TelemetryEvidence(PlaybackStreamSession session) => session.Telemetry.Evidence(time.GetUtcNow());

    /// <summary>Returns the session only to the profile that created it.</summary>
    public PlaybackStreamSession? Get(Guid sessionId, string profileId)
    {
        if (!sessions.TryGetValue(sessionId, out var session) ||
            !string.Equals(session.ProfileId, profileId, StringComparison.Ordinal))
        {
            return null;
        }

        var now = time.GetUtcNow();
        if (now - session.LastSeenUtc > IdleLifetime)
        {
            Remove(sessionId, profileId);
            return null;
        }

        session.Touch(now);
        return session;
    }

    public bool Remove(Guid sessionId, string profileId)
    {
        if (!sessions.TryGetValue(sessionId, out var session) ||
            !string.Equals(session.ProfileId, profileId, StringComparison.Ordinal) ||
            !sessions.TryRemove(sessionId, out _))
        {
            return false;
        }

        Removed?.Invoke(session);
        return true;
    }

    /// <summary>Every live session, newest activity first. Owner-only diagnostics (Admin &gt; Sessions).</summary>
    public IReadOnlyList<PlaybackStreamSession> ListAll() =>
        sessions.Values.OrderByDescending(x => x.LastSeenUtc).ToArray();

    /// <summary>One profile's own live sessions, newest activity first (Profile &gt; Devices).</summary>
    public IReadOnlyList<PlaybackStreamSession> ListForProfile(string profileId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        return sessions.Values
            .Where(x => string.Equals(x.ProfileId, profileId, StringComparison.Ordinal))
            .OrderByDescending(x => x.LastSeenUtc)
            .ToArray();
    }

    /// <summary>
    /// Owner-only removal that ends any profile's session (Admin &gt; Sessions "Stop"), unlike
    /// <see cref="Remove"/> which only lets a profile end its own session.
    /// </summary>
    public bool RemoveAny(Guid sessionId)
    {
        if (!sessions.TryRemove(sessionId, out var session))
        {
            return false;
        }

        Removed?.Invoke(session);
        return true;
    }

    public void CleanupExpired()
    {
        List<PlaybackStreamSession> removed;
        lock (gate)
        {
            removed = RemoveExpired(time.GetUtcNow());
        }

        foreach (var item in removed)
        {
            Removed?.Invoke(item);
        }
    }

    private List<PlaybackStreamSession> RemoveExpired(DateTimeOffset now)
    {
        var removed = new List<PlaybackStreamSession>();
        foreach (var session in sessions.Values)
        {
            if (now - session.LastSeenUtc > IdleLifetime && sessions.TryRemove(session.Id, out _))
            {
                removed.Add(session);
            }
        }

        return removed;
    }

    private PlaybackStreamSession? RemoveOldest(Func<PlaybackStreamSession, bool> filter)
    {
        var oldest = sessions.Values.Where(filter).OrderBy(x => x.LastSeenUtc).FirstOrDefault();
        return oldest is not null && sessions.TryRemove(oldest.Id, out _) ? oldest : null;
    }
}
