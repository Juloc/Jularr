namespace Jularr.Web.Features.MediaCore;

/// <summary>
/// Registers the universal media core services (#592) — the provider-independent query and write
/// surface the other #556 children build on. All services are scoped over the request's
/// <c>AppDbContext</c>, matching the rest of the application.
/// </summary>
public static class MediaCoreRegistration
{
    public static IServiceCollection AddMediaCore(this IServiceCollection services)
    {
        services.AddScoped<WorkService>();
        services.AddScoped<WorkStructureService>();
        services.AddScoped<WorkQueryService>();
        services.AddScoped<LegacyWorkBridge>();
        services.AddScoped<WorkMetadataStore>();
        return services;
    }
}
