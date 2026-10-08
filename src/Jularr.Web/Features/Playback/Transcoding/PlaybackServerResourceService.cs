using Jularr.Web.Features.Instance;
using Jularr.Web.Features.Playback.Decision;

namespace Jularr.Web.Features.Playback.Transcoding;

/// <summary>
/// The lifecycle of the playback server resource policy: loads the stored policy before the
/// first request, detects hardware encoders once the host is up (never delaying application
/// start), then keeps the HLS cache within its policy so cleanup does not depend on playback
/// requests. It follows the repository's hosted-service pattern (<see cref="BackgroundService"/>
/// with a <see cref="PeriodicTimer"/>).
/// </summary>
public sealed class PlaybackServerResourceService(
    PlaybackTranscodingSettingsStore settings,
    PlaybackHardwareService hardware,
    HlsPlaybackSessionManager hls,
    PlaybackStreamSessionStore sessions,
    TimeProvider time,
    ILogger<PlaybackServerResourceService> logger,
    IInstanceModuleService? modules = null) : BackgroundService
{
    public static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(1);

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await settings.LoadAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            // The server keeps running on the defaults; the Admin page reports the broken file when it is opened.
            logger.LogError(exception, "The stored playback transcoding settings are invalid; the defaults apply until they are saved again.");
        }

        await base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Yielding first hands control back to the host, so a slow ffmpeg cannot hold up startup.
        await Task.Yield();
        // Detection runs beside the sweeper, never in front of it: the cache needs its cleanup whether or not ffmpeg answers. An instance that
        // does not play media (Playback off, the Media Manager preset) neither probes ffmpeg nor sweeps a cache that cannot exist; the first tick
        // after Playback is switched on starts both.
        var detection = Task.CompletedTask;
        var detectionStarted = false;
        try
        {
            using var timer = new PeriodicTimer(SweepInterval, time);
            do
            {
                if (modules is null || await modules.IsEnabledAsync(InstanceModule.Playback, stoppingToken))
                {
                    if (detection.IsCompleted && (!detectionStarted || hardware.IsRedetectionDue()))
                    {
                        detection = hardware.DetectAsync(stoppingToken);
                        detectionStarted = true;
                    }

                    SweepOnce();
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            // The detection in flight is awaited so shutdown leaves no running ffmpeg probe behind.
            try
            {
                await detection;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
            }
        }
    }

    private void SweepOnce()
    {
        try
        {
            // A too-slow conversion whose player paused or left would run on unthrottled for nothing; ending its session ends its encode.
            var released = sessions.ReleaseAbandonedSlowEncodes();
            if (released > 0)
            {
                logger.LogInformation("Released {Count} too-slow conversions whose player stopped using them.", released);
            }

            sessions.CleanupExpired();
            var result = hls.Sweep();
            if (result.ExpiredSessions + result.PrunedForPolicy + result.OrphanDirectories + result.CrashedSessions > 0)
            {
                logger.LogInformation(
                    "HLS cache sweep removed {Expired} expired and {Crashed} crashed sessions, {Pruned} sessions for the cache policy and {Orphans} orphaned directories.",
                    result.ExpiredSessions,
                    result.CrashedSessions,
                    result.PrunedForPolicy,
                    result.OrphanDirectories);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "The HLS cache sweep failed; it is retried on the next interval.");
        }
    }
}
