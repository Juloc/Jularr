using System.Threading.RateLimiting;
using Jularr.Web.Features.InstantPlay;
using Jularr.Web.Features.Providers;
using Microsoft.AspNetCore.DataProtection;
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

    /// <summary>
    /// Per-account limits of the TMDB form of Admin → Providers: a connection test is a real provider call, so it gets a small budget of its own, while
    /// the page and the other handlers stay generous.
    /// </summary>
    public const string ProviderSettingsRateLimitPolicy = "provider-settings";

    public const int ProviderTestsPerMinute = 6;
    public const int ProviderSettingsRequestsPerMinute = 60;

    /// <summary>Provider-driven discovery (#595): the source flights, the coordinator behind browse and search and the shelf board it feeds.</summary>
    public static IServiceCollection AddDiscovery(this IServiceCollection services)
    {
        services.AddSingleton<DiscoverySourceFlights>();
        services.AddScoped<DiscoverySnapshotStore>();
        services.AddScoped<DiscoveryCoordinator>();
        services.AddScoped<IDiscoveryFeed>(provider => provider.GetRequiredService<DiscoveryCoordinator>());
        services.AddScoped<DiscoveryShelfService>();
        services.AddSingleton(provider => new TmdbCredentialStore(
            provider.GetRequiredService<IConfiguration>(),
            provider.GetRequiredService<IDataProtectionProvider>(),
            provider.GetRequiredService<TimeProvider>(),
            TmdbCredentialStore.DefaultDirectory,
            provider.GetService<ILogger<TmdbCredentialStore>>()));
        services.AddScoped<TmdbSettingsService>();
        services.AddScoped<IProviderSettings>(provider => provider.GetRequiredService<TmdbSettingsService>());
        services.Configure<RateLimiterOptions>(options =>
        {
            options.AddPolicy(RateLimitPolicy, httpContext => PartitionFor(httpContext));
            options.AddPolicy(ProviderSettingsRateLimitPolicy, httpContext => ProviderSettingsPartitionFor(httpContext));
        });
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

        return PerAccountMinute(httpContext, kind, permits);
    }

    /// <summary>The budget a request to the TMDB form is counted against: the account and whether it is a connection test.</summary>
    public static RateLimitPartition<string> ProviderSettingsPartitionFor(HttpContext httpContext) =>
        httpContext.Request.Query["handler"].ToString() == "Test"
            ? PerAccountMinute(httpContext, "test", ProviderTestsPerMinute)
            : PerAccountMinute(httpContext, "page", ProviderSettingsRequestsPerMinute);

    private static RateLimitPartition<string> PerAccountMinute(HttpContext httpContext, string kind, int permits) =>
        RateLimitPartition.GetFixedWindowLimiter(
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
