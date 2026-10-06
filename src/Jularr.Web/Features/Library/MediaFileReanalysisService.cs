using Jularr.Web.Data;
using Jularr.Web.Features.Operations;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Web.Features.Library;

/// <summary>The result of a re-analysis request: <see cref="Found"/> is false when the file is not one of the title's files.</summary>
public readonly record struct MediaReanalysis(bool Found, MediaAnalysisStatus? Status);

/// <summary>
/// The one owner of forcing a single stored file through technical analysis again as a visible, non-retryable operation. It checks that the
/// file belongs to the title the Admin page shows, so no caller can reach another title's file by posting its id.
/// </summary>
public sealed class MediaFileReanalysisService(AppDbContext db, MediaInventoryService inventory, OperationRunner operations)
{
    /// <summary>Re-analyses a local video file of a Movie or Series Work.</summary>
    public async Task<MediaReanalysis> ReanalyzeVideoFileAsync(Guid workId, Guid fileId, OperationDescriptor descriptor, CancellationToken cancellationToken)
    {
        var owned = await (
                from asset in db.MediaAssets.AsNoTracking()
                join file in db.StoredFiles.AsNoTracking() on (Guid?)asset.Id equals file.MediaAssetId
                where asset.WorkId == workId && asset.Kind == MediaAssetKind.Video && file.Id == fileId
                select file.Id)
            .AnyAsync(cancellationToken);
        return owned ? await RunAsync(fileId, descriptor, cancellationToken) : new MediaReanalysis(false, null);
    }

    /// <summary>Re-analyses a media file of an anime.</summary>
    public async Task<MediaReanalysis> ReanalyzeAnimeFileAsync(Guid animeId, Guid fileId, OperationDescriptor descriptor, CancellationToken cancellationToken)
    {
        var owned = await (
                from media in db.MediaFiles.AsNoTracking()
                join episode in db.Episodes.AsNoTracking() on media.EpisodeId equals episode.Id
                where media.Id == fileId && episode.AnimeId == animeId
                select media.Id)
            .AnyAsync(cancellationToken);
        return owned ? await RunAsync(fileId, descriptor, cancellationToken) : new MediaReanalysis(false, null);
    }

    private async Task<MediaReanalysis> RunAsync(Guid fileId, OperationDescriptor descriptor, CancellationToken cancellationToken)
    {
        var entry = await operations.RunAsync(
            descriptor with { Lane = OperationLane.Normal, Retryable = false },
            async (_, token) =>
            {
                await inventory.InvalidateAsync([fileId], token);
                return await inventory.EnsureAnalyzedAsync(fileId, token);
            },
            null,
            cancellationToken);
        return new MediaReanalysis(true, entry?.Status);
    }
}
