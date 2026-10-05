using Jularr.Web.Data;
using Jularr.Web.Features.Library;
using Jularr.Web.Features.MediaCore;
using Jularr.Web.Features.Progress;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jularr.Tests;

/// <summary>
/// Arranges and inspects canonical video progress of legacy-addressed Anime episodes. Seeding mimics the library
/// scan: every episode of the anime gets its canonical WorkEpisode before progress is written, so contiguity rules
/// see the whole episode list.
/// </summary>
internal static class CanonicalProgressSeed
{
    public static async Task SetAsync(
        AppDbContext db,
        string profileId,
        Guid episodeId,
        long positionMs,
        long? durationMs,
        bool completed,
        DateTime? updatedAt = null)
    {
        var resolver = new CanonicalVideoTargetResolver(db, new LegacyWorkBridge(db, new WorkService(db), new WorkStructureService(db)));
        var animeId = await db.Episodes.AsNoTracking().Where(x => x.Id == episodeId).Select(x => x.AnimeId).SingleAsync();
        var siblingIds = await db.Episodes.AsNoTracking().Where(x => x.AnimeId == animeId).Select(x => x.Id).ToListAsync();
        MediaProgressTarget? target = null;
        foreach (var siblingId in siblingIds)
        {
            var resolved = await resolver.ResolveLegacyEpisodeAsync(siblingId);
            if (siblingId == episodeId)
            {
                target = resolved;
            }
        }

        await new VideoProgressService(db).ImportProgressAsync(profileId, target!, positionMs, durationMs, completed, updatedAt ?? DateTime.UtcNow);
    }

    /// <summary>
    /// Gives every legacy episode file its canonical video Asset, as the library scan does, so canonical Continue
    /// Watching sees it. Files that exist only as database rows are created empty on disk first.
    /// </summary>
    public static async Task AttachCanonicalVideoAsync(AppDbContext db)
    {
        var missing = await db.StoredFiles.AsNoTracking().Where(x => x.EpisodeId != null && x.MediaAssetId == null).Select(x => x.Path).ToListAsync();
        foreach (var path in missing.Where(path => !File.Exists(path)))
        {
            await File.WriteAllBytesAsync(path, [0]);
        }

        var bridge = new LegacyWorkBridge(db, new WorkService(db), new WorkStructureService(db));
        await new CanonicalVideoStorageBackfillService(db, bridge, new CanonicalMediaStorageService(db), NullLogger<CanonicalVideoStorageBackfillService>.Instance).BackfillLegacyAnimeAsync(null, CancellationToken.None);
    }

    /// <summary>Canonical progress rows (any profile unless one is given) of the legacy episodes of one anime.</summary>
    public static async Task<IReadOnlyList<LegacyEpisodeProgress>> RowsAsync(AppDbContext db, Guid animeId, string? profileId = null) =>
        await new VideoProgressService(db).GetLegacyEpisodeProgressAsync(profileId, [animeId]);

    public static async Task<int> CountAsync(AppDbContext db, string table, string? profileId = null) =>
        await db.Database.SqlQueryRaw<int>(
                $$"""SELECT COUNT(*)::int AS "Value" FROM "{{table}}" WHERE {0}::text IS NULL OR "ProfileId" = {0}""",
                (object?)profileId ?? DBNull.Value)
            .SingleAsync();
}
