using System.Security.Claims;
using System.Threading.RateLimiting;
using Jularr.Web.Features.Playback.Transcoding;
using Jularr.Web.Infrastructure;
using Microsoft.AspNetCore.RateLimiting;

namespace Jularr.Web.Features.Playback.Decision;

public static class PlaybackDecisionRegistration
{
    /// <summary>Name of the per-account rate limit on playback plans and stream starts.</summary>
    public const string RateLimitPolicy = "playbackStart";

    /// <summary>
    /// The canonical playback decision (#403): the server resource policy (settings, hardware
    /// detection, slots by cost class, HLS cache), the server capability view, the stream-session
    /// store and the plan service every client uses.
    /// </summary>
    public static IServiceCollection AddPlaybackDecision(this IServiceCollection services)
    {
        services.AddSingleton(_ => new PlaybackTranscodingSettingsStore("/data"));
        services.AddSingleton<PlaybackTranscodeSlots>();
        services.AddSingleton<PlaybackBackendBreaker>();
        services.AddSingleton(provider => new PlaybackHardwareProbe(
            provider.GetRequiredService<IMediaProcessRunner>(),
            provider.GetRequiredService<TimeProvider>(),
            PlaybackRenderDevices.Discover));
        services.AddSingleton<PlaybackHardwareService>();
        services.AddSingleton<HlsPlaybackSessionManager>();
        services.AddSingleton<PlaybackAdmissionService>();
        services.AddSingleton<PlaybackServerCapabilityProvider>();
        services.AddSingleton<PlaybackServerResourceService>();
        services.AddHostedService(provider => provider.GetRequiredService<PlaybackServerResourceService>());
        services.AddSingleton(provider =>
        {
            var store = new PlaybackStreamSessionStore(provider.GetRequiredService<TimeProvider>());
            // A replaced, expired or ended playback session takes its HLS output with it.
            store.Removed += session =>
            {
                if (session.HlsSessionId is { } hlsSessionId)
                {
                    provider.GetRequiredService<HlsPlaybackSessionManager>().Stop(hlsSessionId, session.ProfileId);
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
