using System.Threading.RateLimiting;
using Jularr.Web.Features.InstantPlay;
using Microsoft.AspNetCore.RateLimiting;

namespace Jularr.Web.Features.Discovery;

public static class DiscoveryRegistration
{
    /// <summary>
    /// Per-account limits of the Discover page, one budget for each kind of request so the cheap ones can never use up the others:
    /// the body (every one can start provider calls, and a search holds a few follow-ups open), the live status of requested titles
    /// (a polling client, cheap and local) and everything else, which is the page itself and its actions.
    /// </summary>
    public const string RateLimitPolicy = "discover";

    public const int BodyRequestsPerMinute = 240;
    public const int StatusRequestsPerMinute = 120;
    public const int PageRequestsPerMinute = 60;

    /// <summary>Provider-driven discovery (#595): the source flights, the coordinator behind browse and search and the shelf board it feeds.</summary>
    public static IServiceCollection AddDiscovery(this IServiceCollection services)
    {
        services.AddSingleton<DiscoverySourceFlights>();
        services.AddScoped<DiscoveryCoordinator>();
        services.AddScoped<IDiscoveryFeed>(provider => provider.GetRequiredService<DiscoveryCoordinator>());
        services.AddScoped<DiscoveryShelfService>();
        services.Configure<RateLimiterOptions>(options => options.AddPolicy(RateLimitPolicy, httpContext => PartitionFor(httpContext)));
        return services;
    }

    /// <summary>The budget a request is counted against: the account and what the request asks the page for. An unknown handler counts as the page.</summary>
    public static RateLimitPartition<string> PartitionFor(HttpContext httpContext)
    {
        var (kind, permits) = httpContext.Request.Query["handler"].ToString() switch
        {
            "Body" => ("body", BodyRequestsPerMinute),
            "RequestStatus" => ("status", StatusRequestsPerMinute),
            _ => ("page", PageRequestsPerMinute)
        };

        return RateLimitPartition.GetFixedWindowLimiter(
            $"{InstantPlayRegistration.AccountPartitionKey(httpContext)}|{kind}",
            _ => new FixedWindowRateLimiterOptions
            {
                AutoReplenishment = true,
                PermitLimit = permits,
                Window = TimeSpan.FromMinutes(1),
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = 0
            });
    }
}
