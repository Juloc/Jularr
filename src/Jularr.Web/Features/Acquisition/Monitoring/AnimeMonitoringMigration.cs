using Jularr.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Web.Features.Acquisition.Monitoring;

/// <summary>
/// Moves what an older version kept in <c>monitoring.json</c> about whether an anime and its seasons and episodes are monitored into the canonical
/// Monitoring state, then removes it from the file. The decisions are written before they are removed, and writing them twice gives the same state, so a
/// crash in between only repeats the same write at the next start.
/// </summary>
public sealed class AnimeMonitoringMigration(AnimeMonitoringStore store, AnimeMonitoring monitoring, AppDbContext db, ILogger<AnimeMonitoringMigration> logger)
{
    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        var legacy = await store.ReadLegacyDecisionsAsync(cancellationToken);
        if (legacy.Count == 0)
        {
            return 0;
        }

        var moved = 0;
        foreach (var entry in legacy)
        {
            if (await db.Anime.AsNoTracking().Where(anime => anime.Key == entry.AnimeKey).Select(anime => (Guid?)anime.Id).FirstOrDefaultAsync(cancellationToken) is not { } animeId)
            {
                continue;
            }

            await monitoring.SetMonitoredAsync(animeId, entry.Monitored, cancellationToken);
            foreach (var value in new[] { true, false })
            {
                await monitoring.SetUnitsAsync(
                    animeId,
                    [.. entry.Seasons.Where(pair => pair.Value == value).Select(pair => pair.Key)],
                    [.. entry.Episodes.Where(pair => pair.Value == value).Select(pair => pair.Key)],
                    value,
                    cancellationToken);
            }

            moved++;
        }

        await store.RemoveLegacyDecisionsAsync(cancellationToken);
        logger.LogInformation("Moved the monitoring of {Count} anime into the canonical Monitoring state.", moved);
        return moved;
    }
}
