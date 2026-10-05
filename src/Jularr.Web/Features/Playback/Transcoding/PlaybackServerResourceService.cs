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
    TimeProvider time,
    ILogger<PlaybackServerResourceService> logger) : BackgroundService
{
    public static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(1);

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await settings.LoadAsync(cancellationToken);
        }
        catch (InvalidDataException exception)
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
        try
        {
            await hardware.DetectAsync(stoppingToken);
            using var timer = new PeriodicTimer(SweepInterval, time);
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                SweepOnce();
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    public void SweepOnce()
    {
        try
        {
            var result = hls.Sweep();
            if (result.ExpiredSessions + result.PrunedForPolicy + result.OrphanDirectories > 0)
            {
                logger.LogInformation(
                    "HLS cache sweep removed {Expired} expired sessions, {Pruned} sessions for the cache policy and {Orphans} orphaned directories.",
                    result.ExpiredSessions,
                    result.PrunedForPolicy,
                    result.OrphanDirectories);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "The HLS cache sweep failed; it is retried on the next interval.");
        }
    }
}
