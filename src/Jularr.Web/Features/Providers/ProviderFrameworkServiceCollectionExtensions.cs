namespace Jularr.Web.Features.Providers;

/// <summary>
/// Registers the shared external-provider framework (#438): rate limiting,
/// health/circuit tracking, response caching, the execution pipeline and the
/// provider catalog. Additive and idempotent — new provider families call this
/// (already invoked once from <c>Program</c>) and then resolve
/// <see cref="ProviderExecutor"/>/<see cref="ProviderResponseCache"/> etc.
/// </summary>
public static class ProviderFrameworkServiceCollectionExtensions
{
    public static IServiceCollection AddProviderFramework(this IServiceCollection services)
    {
        services.AddSingleton<ProviderRateLimiter>();
        services.AddSingleton<ProviderHealthTracker>();
        services.AddSingleton<ProviderResponseCache>();
        services.AddSingleton<ProviderExecutor>();
        services.AddSingleton(_ =>
        {
            var catalog = new ProviderCatalog();
            catalog.Register(new ExternalProviderDescriptor(
                ProviderKeys.AniList,
                "AniList",
                ProviderCapabilities.Metadata | ProviderCapabilities.ReleaseSchedule | ProviderCapabilities.Sync));
            catalog.Register(new ExternalProviderDescriptor(
                ProviderKeys.Tmdb,
                "TMDB",
                ProviderCapabilities.Metadata | ProviderCapabilities.Search | ProviderCapabilities.ReleaseSchedule));
            catalog.Register(new ExternalProviderDescriptor(
                ProviderKeys.MusicBrainz,
                "MusicBrainz",
                ProviderCapabilities.Metadata | ProviderCapabilities.Search));
            catalog.Register(new ExternalProviderDescriptor(
                ProviderKeys.Newznab,
                "Newznab indexer",
                ProviderCapabilities.Search));
            catalog.Register(new ExternalProviderDescriptor(
                ProviderKeys.Prowlarr,
                "Prowlarr",
                ProviderCapabilities.Search));
            catalog.Register(new ExternalProviderDescriptor(
                ProviderKeys.OpenSubtitles,
                "OpenSubtitles",
                ProviderCapabilities.Subtitles));
            return catalog;
        });
        return services;
    }
}
