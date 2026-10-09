using Jularr.Web.Data;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.Monitoring;
using Jularr.Web.Features.MediaCore;
using Jularr.Web.Features.Monitoring;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Jularr.Tests;

/// <summary>The canonical Monitoring services over a test database, built the way the application wires them.</summary>
internal static class MonitoringTestSupport
{
    /// <summary>The canonical Monitoring services a page host needs, registered as the application does.</summary>
    public static IServiceCollection AddMonitoringForTests(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddScoped<WorkService>();
        services.TryAddScoped<WorkStructureService>();
        services.TryAddScoped<LegacyWorkBridge>();
        services.TryAddScoped<MonitoringResolver>();
        services.TryAddScoped<MonitoringCommands>();
        services.TryAddScoped<VideoRequestScopeResolver>();
        services.TryAddScoped<AnimeMonitoring>();
        return services;
    }

    public static MonitoringResolver Resolver(AppDbContext db) => new(db);

    public static MonitoringCommands Commands(AppDbContext db) => new(db, TimeProvider.System);

    public static VideoRequestScopeResolver Scopes(AppDbContext db) => new(db, Commands(db), Resolver(db));

    public static AnimeMonitoring Anime(AppDbContext db)
    {
        var works = new WorkService(db);
        return new AnimeMonitoring(db, Resolver(db), Commands(db), new LegacyWorkBridge(db, works, new WorkStructureService(db)));
    }

    /// <summary>The Music Works the canonical Monitoring state currently wants.</summary>
    public static async Task<IReadOnlyList<Guid>> MonitoredMusicAsync(AppDbContext db) =>
        await Resolver(db).MonitoredWorkIdsAsync(WorkMediaType.Music, Guid.Empty, int.MaxValue, CancellationToken.None);

    /// <summary>The payload of a request that still carries what its requester chose to monitor (applied when the request first runs).</summary>
    public static VideoRequestPayload Choosing(Guid workId, string title, int? year, VideoRequestScope scope, IReadOnlyCollection<Guid>? episodes = null, bool future = false, IReadOnlyCollection<Guid>? seasons = null) =>
        new(workId, title, year) { Requested = new VideoRequestScopeChoice(scope, seasons ?? [], episodes ?? [], future) };

    /// <summary>Applies a choice as monitoring decisions of the Work right away, the way the first run of a request does.</summary>
    public static Task ApplyAsync(AppDbContext db, Guid workId, VideoRequestScope scope, IReadOnlyCollection<Guid>? episodes = null, bool future = false, IReadOnlyCollection<Guid>? seasons = null) =>
        Scopes(db).ApplyAsync(workId, new VideoRequestScopeChoice(scope, seasons ?? [], episodes ?? [], future), CancellationToken.None);
}
