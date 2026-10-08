using Jularr.Web.Data;
using Jularr.Web.Features.MediaCore;
using Jularr.Web.Features.Monitoring;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Web.Features.Acquisition.Monitoring;

/// <summary>
/// The canonical Monitoring of Anime, addressed the way the Anime pipeline names things: by anime key and by season and episode number. It only
/// translates; the state is the one canonical state of the anime's Work, so the pipeline, the admin pages and the calendar read what everything else reads.
/// </summary>
public sealed class AnimeMonitoring(AppDbContext db, MonitoringResolver monitoring, MonitoringCommands commands, LegacyWorkBridge bridge)
{
    /// <summary>The views of several anime by key, loaded with numbers; an anime that has no Work yet is not monitored.</summary>
    public async Task<IReadOnlyDictionary<string, WorkMonitoringView>> LoadAsync(IReadOnlyCollection<string> animeKeys, CancellationToken cancellationToken)
    {
        var keys = animeKeys.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var links = await (
                from anime in db.Anime.AsNoTracking()
                join link in db.WorkSourceLinks.AsNoTracking() on anime.Id equals link.SourceId
                where keys.Contains(anime.Key) && link.SourceKind == WorkSourceKind.Anime
                select new { anime.Key, link.WorkId })
            .ToListAsync(cancellationToken);
        var views = await monitoring.LoadManyAsync([.. links.Select(link => link.WorkId)], cancellationToken, withNumbers: true);
        var byKey = links.ToDictionary(link => link.Key, link => views[link.WorkId], StringComparer.OrdinalIgnoreCase);
        return keys.ToDictionary(key => key, key => byKey.GetValueOrDefault(key) ?? WorkMonitoringView.Unmonitored(Guid.Empty), StringComparer.OrdinalIgnoreCase);
    }

    public async Task<WorkMonitoringView> LoadAsync(string animeKey, CancellationToken cancellationToken) =>
        (await LoadAsync([animeKey], cancellationToken))[animeKey];

    /// <summary>Whether a unit of the pipeline is monitored: the whole item, a season pack or one episode.</summary>
    public static bool IsUnitMonitored(WorkMonitoringView view, MonitoredUnitKey key) => key.Granularity switch
    {
        MonitoringGranularity.Item => view.IsWorkMonitored,
        MonitoringGranularity.Season => view.IsSeasonMonitored(key.SeasonNumber),
        _ => view.IsEpisodeMonitored(key.SeasonNumber, key.EpisodeNumber)
    };

    /// <summary>The keys of the anime that have anything monitored, in key order.</summary>
    public async Task<IReadOnlyList<string>> MonitoredKeysAsync(CancellationToken cancellationToken)
    {
        var workIds = new List<Guid>();
        var after = Guid.Empty;
        while (true)
        {
            var page = await monitoring.MonitoredWorkIdsAsync(WorkMediaType.Anime, after, 500, cancellationToken);
            workIds.AddRange(page);
            if (page.Count < 500)
            {
                break;
            }

            after = page[^1];
        }

        return await (
                from link in db.WorkSourceLinks.AsNoTracking()
                join anime in db.Anime.AsNoTracking() on link.SourceId equals anime.Id
                where link.SourceKind == WorkSourceKind.Anime && workIds.Contains(link.WorkId)
                orderby anime.Key
                select anime.Key)
            .ToListAsync(cancellationToken);
    }

    /// <summary>The canonical Work of an anime, created from the anime record when it has none yet.</summary>
    public async Task<Guid> EnsureWorkAsync(Guid animeId, CancellationToken cancellationToken) =>
        await bridge.EnsureWorkForAnimeAsync(await db.Anime.AsNoTracking().SingleAsync(anime => anime.Id == animeId, cancellationToken), cancellationToken);

    /// <summary>Switches the anime as a whole on or off; the decisions of its seasons and episodes stay as they are.</summary>
    public async Task SetMonitoredAsync(Guid animeId, bool monitored, CancellationToken cancellationToken) =>
        await commands.SetAsync(MonitoringTargetKind.Work, await EnsureWorkAsync(animeId, cancellationToken), monitored, cancellationToken, replaceChildren: false);

    /// <summary>Switches seasons and episodes on or off by number in one batch.</summary>
    public async Task SetUnitsAsync(Guid animeId, IReadOnlyCollection<int> seasons, IReadOnlyCollection<(int Season, int Episode)> episodes, bool monitored, CancellationToken cancellationToken)
    {
        var workId = await EnsureWorkAsync(animeId, cancellationToken);
        await commands.SetSeasonsByNumberAsync(workId, seasons, monitored, cancellationToken);
        await commands.SetEpisodesByNumberAsync(workId, episodes, monitored, cancellationToken);
    }
}
