namespace Jularr.Web.Features.Playback.Transcoding;

public sealed record PlaybackBreakerState(
    PlaybackHardwareBackend Backend,
    int ConsecutiveFailures,
    bool IsOpen,
    DateTimeOffset? OpenUntilUtc,
    string? LastFailureReason,
    string? LastFailureDetail,
    DateTimeOffset? LastFailureAtUtc);

/// <summary>
/// Stops sending sessions to a hardware encoder that keeps failing. Three consecutive failures
/// open a backend for ten minutes; afterwards sessions may use it again (the count is kept,
/// so the next failure reopens it at once and a success clears the count).
/// </summary>
public sealed class PlaybackBackendBreaker(TimeProvider time)
{
    public const int FailureThreshold = 3;
    public static readonly TimeSpan OpenDuration = TimeSpan.FromMinutes(10);

    private readonly Lock _gate = new();
    private readonly Dictionary<PlaybackHardwareBackend, Entry> _entries = [];

    public PlaybackBreakerState State(PlaybackHardwareBackend backend)
    {
        lock (_gate)
        {
            return _entries.TryGetValue(backend, out var entry)
                ? new PlaybackBreakerState(backend, entry.ConsecutiveFailures, entry.OpenUntil > time.GetUtcNow(), entry.OpenUntil, entry.LastFailureReason, entry.LastFailureDetail, entry.LastFailureAt)
                : new PlaybackBreakerState(backend, 0, false, null, null, null, null);
        }
    }

    public void RecordFailure(PlaybackHardwareBackend backend, string reason, string? detail = null)
    {
        lock (_gate)
        {
            var now = time.GetUtcNow();
            var entry = _entries.GetValueOrDefault(backend) ?? new Entry();
            entry.ConsecutiveFailures++;
            entry.LastFailureReason = reason;
            entry.LastFailureDetail = detail;
            entry.LastFailureAt = now;
            if (entry.ConsecutiveFailures >= FailureThreshold)
            {
                entry.OpenUntil = now + OpenDuration;
            }

            _entries[backend] = entry;
        }
    }

    /// <summary>A session that really started proves the encoder works: the failure run ends and the breaker closes.</summary>
    public void RecordSuccess(PlaybackHardwareBackend backend)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(backend, out var entry))
            {
                entry.ConsecutiveFailures = 0;
                entry.OpenUntil = null;
            }
        }
    }

    private sealed class Entry
    {
        public int ConsecutiveFailures { get; set; }
        public DateTimeOffset? OpenUntil { get; set; }
        public string? LastFailureReason { get; set; }
        public string? LastFailureDetail { get; set; }
        public DateTimeOffset? LastFailureAt { get; set; }
    }
}

/// <summary>A detected backend that is skipped for now because its breaker is open.</summary>
public sealed record PlaybackBackendSuspension(PlaybackHardwareBackend Backend, string? Reason, DateTimeOffset? UntilUtc);

/// <summary>The encoder the next delivery uses, plus the preferred backend that was skipped on the way (for the plan's "why").</summary>
public sealed record PlaybackEncoderChoice(PlaybackEncoderTarget Target, PlaybackBackendSuspension? Suspended);

/// <summary>
/// The server's view of its encoders: the cached detection result and the breaker over it. It
/// is the one place that decides which backend a delivery uses; detection runs at startup and on
/// an Admin "Re-detect", never on a playback request.
/// </summary>
public sealed class PlaybackHardwareService(PlaybackHardwareProbe probe, PlaybackBackendBreaker breaker, TimeProvider time, ILogger<PlaybackHardwareService> logger)
{
    public static readonly TimeSpan RetryStart = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan RetryMax = TimeSpan.FromMinutes(10);

    private readonly Lock _decodeGate = new();
    private readonly HashSet<PlaybackHardwareBackend> _decodeDisabled = [];
    private int _failedRuns;
    private DateTimeOffset _nextRetryAt;
    private readonly SemaphoreSlim _detectGate = new(1, 1);
    private PlaybackHardwareCapabilities? _detected;
    private string? _detectionError;

    public PlaybackBackendBreaker Breaker => breaker;

    /// <summary>Null until the first detection finished; the server then behaves like a software-only server.</summary>
    public PlaybackHardwareCapabilities? Detected => Volatile.Read(ref _detected);

    /// <summary>The reason the last detection run failed unexpectedly, null when it completed; Admin shows it next to the stale result.</summary>
    public string? DetectionError => Volatile.Read(ref _detectionError);

    /// <summary>
    /// Runs the probe and replaces the cached result. An unexpected failure keeps the previous result and
    /// is recorded in <see cref="DetectionError"/> instead of ending the caller (startup must not fail over detection).
    /// </summary>
    public async Task DetectAsync(CancellationToken cancellationToken)
    {
        await _detectGate.WaitAsync(cancellationToken);
        try
        {
            var result = await probe.DetectAsync(cancellationToken);
            Volatile.Write(ref _detected, result);
            Volatile.Write(ref _detectionError, null);
            NoteRun(result.FfmpegAvailable);

            // A fresh test encode that passes is stronger evidence than the failure run that opened the breaker.
            foreach (var status in result.Backends.Where(x => x.State == PlaybackBackendState.Available))
            {
                breaker.RecordSuccess(status.Backend);
                ClearDecodeDisabled(status.Backend);
            }

            logger.LogInformation("Playback hardware detection finished: {Backends}.", string.Join(", ", result.Backends.Select(x => $"{PlaybackHardwareBackends.Name(x.Backend)}={x.State}")));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Volatile.Write(ref _detectionError, exception.Message);
            NoteRun(succeeded: false);
            logger.LogError(exception, "Playback hardware detection failed.");
        }
        finally
        {
            _detectGate.Release();
        }
    }

    /// <summary>The backend new plans are made for: the first detected one in selection order whose breaker is closed.</summary>
    public PlaybackEncoderChoice Choose()
    {
        PlaybackBackendSuspension? suspended = null;
        if (Detected is { } capabilities)
        {
            foreach (var backend in PlaybackHardwareBackends.SelectionOrder)
            {
                if (capabilities.Status(backend) is not { State: PlaybackBackendState.Available } status)
                {
                    continue;
                }

                if (breaker.State(backend) is { IsOpen: true } open)
                {
                    suspended ??= new PlaybackBackendSuspension(backend, open.LastFailureReason, open.OpenUntilUtc);
                    continue;
                }

                return new PlaybackEncoderChoice(new PlaybackEncoderTarget(backend, status.Device, status.HardwareDecoding && !IsHardwareDecodingDisabled(backend)), suspended);
            }
        }

        return new PlaybackEncoderChoice(PlaybackEncoderTarget.Software, suspended);
    }

    /// <summary>
    /// The encoder a started session really uses. A plan is made before the stream starts, so the
    /// planned backend may meanwhile have been suspended or removed by a re-detection: such a
    /// session falls back to software automatically.
    /// </summary>
    public PlaybackEncoderTarget Resolve(string? plannedEncoder)
    {
        var backend = PlaybackHardwareBackends.FromEncoder(plannedEncoder);
        if (backend == PlaybackHardwareBackend.Software ||
            Detected?.Status(backend) is not { State: PlaybackBackendState.Available } status ||
            breaker.State(backend).IsOpen)
        {
            return PlaybackEncoderTarget.Software;
        }

        return new PlaybackEncoderTarget(backend, status.Device, status.HardwareDecoding && !IsHardwareDecodingDisabled(backend));
    }

    public bool IsHardwareDecodingDisabled(PlaybackHardwareBackend backend)
    {
        lock (_decodeGate)
        {
            return _decodeDisabled.Contains(backend);
        }
    }

    /// <summary>A start that failed on the device's decoder but worked with software decoding: the encoder stays in use, the decoder is off until the next detection that passes.</summary>
    public void DisableHardwareDecoding(PlaybackHardwareBackend backend)
    {
        lock (_decodeGate)
        {
            _decodeDisabled.Add(backend);
        }

        logger.LogWarning("Hardware decoding on {Backend} failed while its encoder works; decoding falls back to software.", PlaybackHardwareBackends.Name(backend));
    }

    /// <summary>
    /// Whether a detection should run again: the last one found ffmpeg missing, timed out or failing (or ended in an
    /// error), and its backoff (1 minute doubling to 10) has passed. A transient problem at startup must not
    /// block every remux and transcode until an Admin clicks Re-detect.
    /// </summary>
    public bool IsRedetectionDue() => Volatile.Read(ref _failedRuns) > 0 && time.GetUtcNow() >= _nextRetryAt;

    private void ClearDecodeDisabled(PlaybackHardwareBackend backend)
    {
        lock (_decodeGate)
        {
            _decodeDisabled.Remove(backend);
        }
    }

    private void NoteRun(bool succeeded)
    {
        if (succeeded)
        {
            Volatile.Write(ref _failedRuns, 0);
            return;
        }

        var failures = Interlocked.Increment(ref _failedRuns);
        var delay = TimeSpan.FromTicks(Math.Min(RetryMax.Ticks, RetryStart.Ticks << Math.Min(failures - 1, 8)));
        _nextRetryAt = time.GetUtcNow() + delay;
    }
}
