using Jularr.Web.Data;
using Jularr.Web.Features.Library;
using Jularr.Web.Features.MediaCore;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Web.Features.Storage.Insights;

// Storage usage from what the library already knows. Sizes come from the media inventory the
// reconciliation keeps (MediaFile.SizeBytes), so no filesystem scan is needed and a sleeping NAS
// is never touched: a root's state is read from the cached availability, and a root that could be
// a sleeping Wake-on-LAN NAS whose state was never observed is simply reported as not checked.
// Only a root without Wake-on-LAN, which nothing here could wake, is observed once when unknown.
public sealed class StorageUsageService(
    AppDbContext db,
    LibraryRootAvailabilityService availability,
    StorageIntegrityService integrity)
{
    public const int DefaultLargestItems = 15;

    public async Task<StorageUsageReport> GetAsync(
        int largestItems,
        CancellationToken cancellationToken)
    {
        var limit = Math.Clamp(largestItems, 1, 100);

        var roots = await db.LibraryRoots
            .AsNoTracking()
            .OrderBy(x => x.Name)
            .ToListAsync(cancellationToken);

        var perRoot = (await db.MediaFiles
                .AsNoTracking()
                .GroupBy(x => x.LibraryRootId)
                .Select(group => new
                {
                    RootId = group.Key,
                    Files = group.Count(),
                    Bytes = group.Sum(x => x.SizeBytes)
                })
                .ToListAsync(cancellationToken))
            .ToDictionary(x => x.RootId);

        var integritySummaries = await integrity.SummarizeAsync(cancellationToken);

        var rootUsage = new List<StorageRootUsage>(roots.Count);
        var healthByRoot = new Dictionary<Guid, StorageHealthState?>();
        foreach (var root in roots)
        {
            var snapshot = await ObserveAsync(root, cancellationToken);
            var health = snapshot?.Health;
            healthByRoot[root.Id] = health;
            perRoot.TryGetValue(root.Id, out var totals);
            rootUsage.Add(new StorageRootUsage(
                root.Id,
                root.Name,
                root.Path,
                health,
                totals?.Files ?? 0,
                totals?.Bytes ?? 0,
                snapshot is { IsAvailable: true } ? snapshot.FreeSpaceBytes : null,
                integritySummaries.TryGetValue(root.Id, out var summary) ? summary.DuplicateEpisodes : 0,
                root.LastScannedAt));
        }

        var videoFiles = await db.Database
            .SqlQuery<VideoFileTotalsRow>(
                $"""
                SELECT work."MediaType" AS "WorkMediaType",
                       (file."EpisodeId" IS NOT NULL) AS "HasLegacyEpisode",
                       COUNT(*) AS "FileCount",
                       COALESCE(SUM(file."SizeBytes"), 0)::bigint AS "Bytes"
                FROM "StoredFiles" AS file
                LEFT JOIN "MediaAssets" AS asset ON asset."Id" = file."MediaAssetId"
                LEFT JOIN "Works" AS work ON work."Id" = asset."WorkId"
                GROUP BY work."MediaType", (file."EpisodeId" IS NOT NULL)
                """)
            .ToListAsync(cancellationToken);

        var videoByKind = videoFiles
            .GroupBy(VideoKindOf)
            .ToDictionary(group => group.Key, group => new StorageMediaTypeUsage(group.Key, group.Sum(x => x.FileCount), group.Sum(x => x.Bytes)));
        var mediaTypes = new List<StorageMediaTypeUsage>
        {
            videoByKind.GetValueOrDefault(StorageMediaKind.Movies, new StorageMediaTypeUsage(StorageMediaKind.Movies, 0, 0)),
            videoByKind.GetValueOrDefault(StorageMediaKind.Episodes, new StorageMediaTypeUsage(StorageMediaKind.Episodes, 0, 0)),
            new(
                StorageMediaKind.Audiobooks,
                await db.AudiobookFiles.AsNoTracking().LongCountAsync(cancellationToken),
                await db.AudiobookFiles.AsNoTracking().SumAsync(x => (long?)x.SizeBytes, cancellationToken) ?? 0),
            new(
                StorageMediaKind.Books,
                await db.BookFiles.AsNoTracking().LongCountAsync(cancellationToken),
                await db.BookFiles.AsNoTracking().SumAsync(x => (long?)x.SizeBytes, cancellationToken) ?? 0)
        };

        // Files no Work or episode claims are only listed when they exist, so a healthy library shows no empty row.
        if (videoByKind.TryGetValue(StorageMediaKind.UnmatchedVideo, out var unmatched))
        {
            mediaTypes.Insert(2, unmatched);
        }

        var rootNames = roots.ToDictionary(x => x.Id, x => x.Name);
        // A movie has a Work but no episode; an anime/series file resolves its title and numbers through its Work episode or, for
        // files the media core has not claimed yet, through the legacy episode bridge. Files that resolve to neither carry no title and are skipped.
        var largest = await db.Database
            .SqlQuery<LargestFileRow>(
                $"""
                SELECT file."Id" AS "Id",
                       COALESCE(work."CanonicalTitle", anime."Title") AS "Title",
                       COALESCE(workEpisode."SeasonNumber", episode."SeasonNumber") AS "SeasonNumber",
                       COALESCE(workEpisode."EpisodeNumber", episode."Number") AS "EpisodeNumber",
                       file."LibraryRootId" AS "LibraryRootId",
                       file."SizeBytes" AS "SizeBytes"
                FROM "StoredFiles" AS file
                LEFT JOIN "MediaAssets" AS asset ON asset."Id" = file."MediaAssetId"
                LEFT JOIN "Works" AS work ON work."Id" = asset."WorkId"
                LEFT JOIN "WorkEpisodes" AS workEpisode ON workEpisode."Id" = asset."WorkEpisodeId"
                LEFT JOIN "Episodes" AS episode ON episode."Id" = file."EpisodeId"
                LEFT JOIN "Anime" AS anime ON anime."Id" = episode."AnimeId"
                WHERE COALESCE(work."CanonicalTitle", anime."Title") IS NOT NULL
                ORDER BY file."SizeBytes" DESC, file."Id"
                LIMIT {limit}
                """)
            .ToListAsync(cancellationToken);

        return new StorageUsageReport(
            rootUsage,
            mediaTypes,
            [
                .. largest.Select(x => new StorageLargestItem(
                    x.Id,
                    x.Title,
                    x.SeasonNumber,
                    x.EpisodeNumber,
                    x.LibraryRootId,
                    rootNames.GetValueOrDefault(x.LibraryRootId, ""),
                    x.SizeBytes,
                    healthByRoot.GetValueOrDefault(x.LibraryRootId)))
            ]);
    }

    private async Task<LibraryRootAvailabilitySnapshot?> ObserveAsync(
        LibraryRoot root,
        CancellationToken cancellationToken)
    {
        var cached = availability.GetCached(root);
        if (cached is not null)
        {
            return cached;
        }

        // Nothing observed yet. A Wake-on-LAN root may be asleep on purpose; leave it alone.
        var wakeConfigured =
            root.WakeOnLanEnabled &&
            WakeOnLanService.TryNormalizeMacAddress(root.WakeMacAddress, out _);
        return wakeConfigured
            ? null
            : await availability.CheckAsync(root.Id, force: false, cancellationToken);
    }

    // The Work media type decides the kind; a file the media core does not claim yet counts as an episode only through the legacy bridge.
    private static StorageMediaKind VideoKindOf(VideoFileTotalsRow row) =>
        (WorkMediaType?)row.WorkMediaType switch
        {
            WorkMediaType.Movie => StorageMediaKind.Movies,
            WorkMediaType.Series or WorkMediaType.Anime => StorageMediaKind.Episodes,
            _ => row.HasLegacyEpisode ? StorageMediaKind.Episodes : StorageMediaKind.UnmatchedVideo
        };

    private sealed record VideoFileTotalsRow(int? WorkMediaType, bool HasLegacyEpisode, long FileCount, long Bytes);

    private sealed record LargestFileRow(Guid Id, string Title, int? SeasonNumber, int? EpisodeNumber, Guid LibraryRootId, long SizeBytes);
}
