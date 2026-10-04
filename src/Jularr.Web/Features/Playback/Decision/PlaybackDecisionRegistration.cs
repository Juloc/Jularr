using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace Jularr.Web.Features.Playback.Decision;

public static class PlaybackDecisionRegistration
{
    /// <summary>Name of the per-account rate limit on playback plans and stream starts.</summary>
    public const string RateLimitPolicy = "playbackStart";

    /// <summary>
    /// The canonical playback decision (#403): bounded transcode slots, the server capability
    /// view, the stream-session store and the plan service every client uses.
    /// </summary>
    public static IServiceCollection AddPlaybackDecision(this IServiceCollection services)
    {
        services.AddSingleton(_ => new PlaybackTranscodeSlots());
        services.AddSingleton<PlaybackServerCapabilityProvider>();
        services.AddSingleton(provider =>
        {
            var store = new PlaybackStreamSessionStore(provider.GetRequiredService<TimeProvider>());
            // A replaced, expired or ended playback session takes its HLS output with it.
            store.Removed += session =>
            {
                if (session.HlsSessionId is { } hlsSessionId)
                {
                    HlsPlaybackSessionManager.Shared.Stop(hlsSessionId, session.ProfileId);
                }
            };
            return store;
        });
        services.AddScoped<PlaybackPlanService>();
        services.AddScoped<CanonicalPlayerNavigationAssetService>();
        services.AddScoped<CanonicalVideoPlayerService>();

        // Plans and stream starts per account: every seek of a live stream restarts it, so the
        // limit is generous but still stops a client from spawning ffmpeg in a loop.
        services.Configure<RateLimiterOptions>(options =>
            options.AddPolicy(RateLimitPolicy, httpContext =>
                RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier)
                        ?? httpContext.Connection.RemoteIpAddress?.ToString()
                        ?? "unknown",
                    factory: _ => new FixedWindowRateLimiterOptions
                    {
                        AutoReplenishment = true,
                        PermitLimit = 120,
                        Window = TimeSpan.FromMinutes(1),
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                        QueueLimit = 0
                    })));
        return services;
    }
}
