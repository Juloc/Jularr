using System.Threading.RateLimiting;
using Jularr.Web.Features.InstantPlay;
using Microsoft.AspNetCore.RateLimiting;

namespace Jularr.Web.Features.Discovery;

public static class DiscoveryRegistration
{
    /// <summary>
    /// Per-account limit of the Discover page and its handlers. One search is a body request plus at most a few follow-ups that the server
    /// holds open, and every request can start provider calls, so the limit protects the providers' own quotas.
    /// </summary>
    public const string RateLimitPolicy = "discover";

    private const int RequestsPerMinute = 240;

    /// <summary>Provider-driven discovery (#595): the source flights, the coordinator behind browse and search and the shelf board it feeds.</summary>
    public static IServiceCollection AddDiscovery(this IServiceCollection services)
    {
        services.AddSingleton<DiscoverySourceFlights>();
        services.AddScoped<DiscoveryCoordinator>();
        services.AddScoped<IDiscoveryFeed>(provider => provider.GetRequiredService<DiscoveryCoordinator>());
        services.AddScoped<DiscoveryShelfService>();
        services.Configure<RateLimiterOptions>(options => options.AddPolicy(RateLimitPolicy, httpContext =>
            RateLimitPartition.GetFixedWindowLimiter(
                InstantPlayRegistration.AccountPartitionKey(httpContext),
                _ => new FixedWindowRateLimiterOptions
                {
                    AutoReplenishment = true,
                    PermitLimit = RequestsPerMinute,
                    Window = TimeSpan.FromMinutes(1),
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                    QueueLimit = 0
                })));
        return services;
    }
}
