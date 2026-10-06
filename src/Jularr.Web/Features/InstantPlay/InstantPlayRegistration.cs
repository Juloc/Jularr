using System.Security.Claims;
using System.Threading.RateLimiting;
using Jularr.Web.Features.Acquisition.Access;
using Microsoft.AspNetCore.RateLimiting;

namespace Jularr.Web.Features.InstantPlay;

public static class InstantPlayRegistration
{
    /// <summary>Per-account limit of playback intents: one can start an indexer search and a download, so it is far below the playback-plan limit.</summary>
    public const string IntentRateLimitPolicy = "playbackIntent";

    /// <summary>Per-account limit of acquisition status reads; a waiting client polls every few seconds.</summary>
    public const string StatusRateLimitPolicy = "acquisitionStatus";

    public static IServiceCollection AddInstantPlay(this IServiceCollection services)
    {
        services.AddScoped<InstantPlayPolicyService>();
        services.AddScoped<VideoPlaybackFactsQuery>();
        services.AddScoped<ConsumerAcquisitionQuery>();
        services.AddScoped<PlaybackIntentService>();
        services.Configure<RateLimiterOptions>(options =>
        {
            options.AddPolicy(IntentRateLimitPolicy, httpContext => PerAccount(httpContext, permits: 20));
            options.AddPolicy(StatusRateLimitPolicy, httpContext => PerAccount(httpContext, permits: 120));
        });
        return services;
    }

    /// <summary>The signed-in account, else the client address: one household behind one address must not share a limit.</summary>
    public static string AccountPartitionKey(HttpContext httpContext) =>
        httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    private static RateLimitPartition<string> PerAccount(HttpContext httpContext, int permits) =>
        RateLimitPartition.GetFixedWindowLimiter(
            AccountPartitionKey(httpContext),
            _ => new FixedWindowRateLimiterOptions
            {
                AutoReplenishment = true,
                PermitLimit = permits,
                Window = TimeSpan.FromMinutes(1),
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = 0
            });
}
