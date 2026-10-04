using Jularr.Web.Data;
using Jularr.Web.Features.Library;
using Jularr.Web.Features.MediaCore;
using Jularr.Web.Features.MediaSegments;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Jularr.Web.Features.Playback.Decision;

/// <summary>
/// Canonical adapter for optional Player navigation assets. Legacy Anime segment
/// markers are bridged internally; Movie/TV are not forced into the legacy Episode
/// table. Trickplay is keyed by canonical StoredFile identity for every video type.
/// </summary>
public sealed class CanonicalPlayerNavigationAssetService(
    AppDbContext db,
    IOptions<MediaSegmentOptions> options,
    TrickplayGenerator trickplay)
{
    public async Task<EpisodeSegmentDescriptor> GetSegmentsAsync(
        PlaybackVideoTarget target,
        CancellationToken cancellationToken)
    {
        var threshold = MediaSegmentPolicy.ClampConfidence(options.Value.SkipConfidenceThreshold);
        if (target.WorkEpisodeId is not { } workEpisodeId)
        {
            return new EpisodeSegmentDescriptor(threshold, []);
        }

        var legacyEpisodeId = await (
                from canonical in db.WorkEpisodes.AsNoTracking()
                join animeLink in db.WorkSourceLinks.AsNoTracking()
                    on canonical.WorkId equals animeLink.WorkId
                join legacy in db.Episodes.AsNoTracking()
                    on animeLink.SourceId equals legacy.AnimeId
                where canonical.Id == workEpisodeId &&
                      canonical.WorkId == target.WorkId &&
                      animeLink.SourceKind == WorkSourceKind.Anime &&
                      legacy.SeasonNumber == canonical.SeasonNumber &&
                      legacy.Number == canonical.EpisodeNumber
                select (Guid?)legacy.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (legacyEpisodeId is null)
        {
            return new EpisodeSegmentDescriptor(threshold, []);
        }

        var rows = await db.EpisodeMediaSegments
            .AsNoTracking()
            .Where(x => x.EpisodeId == legacyEpisodeId.Value)
            .OrderBy(x => x.Kind)
            .ThenBy(x => x.Source)
            .ToListAsync(cancellationToken);

        return new EpisodeSegmentDescriptor(
            threshold,
            MediaSegmentPolicy.Resolve(rows, threshold));
    }

    public async Task<TrickplayDescriptor> GetTrickplayAsync(
        PlaybackVideoTarget target,
        CanonicalPlayableFile file,
        bool queue,
        CancellationToken cancellationToken)
    {
        var source = await GetTrickplaySourceAsync(file.StoredFileId, cancellationToken);
        if (source is null)
        {
            return TrickplayDescriptor.Unavailable;
        }

        if (queue && source.DurationSeconds is > 0 and var duration)
        {
            await trickplay.EnsureQueuedAsync(
                new TrickplayRequest(
                    target.IdentityId,
                    file.StoredFileId,
                    source.Identity,
                    file.Path,
                    duration,
                    Path.GetFileName(file.Path)),
                cancellationToken);
        }

        return trickplay.Describe(file.StoredFileId, source.Identity);
    }

    public async Task<TrickplayDescriptor> DescribeAsync(
        Guid storedFileId,
        CancellationToken cancellationToken)
    {
        var source = await GetTrickplaySourceAsync(storedFileId, cancellationToken);
        return source is null
            ? TrickplayDescriptor.Unavailable
            : trickplay.Describe(storedFileId, source.Identity);
    }

    public async Task<TrickplayAsset?> GetAssetAsync(
        Guid storedFileId,
        string fileName,
        CancellationToken cancellationToken)
    {
        if (!TrickplayGenerator.IsAllowedAssetName(fileName))
        {
            return null;
        }

        var isCanonicalVideo = await (
                from file in db.StoredFiles.AsNoTracking()
                join asset in db.MediaAssets.AsNoTracking()
                    on file.MediaAssetId equals (Guid?)asset.Id
                where file.Id == storedFileId && asset.Kind == MediaAssetKind.Video
                select file.Id)
            .AnyAsync(cancellationToken);
        if (!isCanonicalVideo)
        {
            return null;
        }

        var source = await GetTrickplaySourceAsync(storedFileId, cancellationToken);
        return source is null
            ? null
            : trickplay.GetAsset(storedFileId, source.Identity, fileName);
    }

    private async Task<TrickplaySource?> GetTrickplaySourceAsync(
        Guid storedFileId,
        CancellationToken cancellationToken)
    {
        var analysis = await db.MediaTechnicalAnalyses
            .AsNoTracking()
            .Where(x =>
                x.MediaFileId == storedFileId &&
                x.Status == MediaAnalysisStatus.Succeeded)
            .Select(x => new
            {
                x.SourceFingerprint,
                x.SourceSizeBytes,
                x.SourceLastWriteTimeUtc,
                x.DurationSeconds
            })
            .SingleOrDefaultAsync(cancellationToken);
        if (analysis is null)
        {
            return null;
        }

        return new TrickplaySource(
            MediaIdentity.Compute(
                storedFileId,
                analysis.SourceFingerprint,
                analysis.SourceSizeBytes,
                analysis.SourceLastWriteTimeUtc),
            analysis.DurationSeconds is > 0 and var seconds && double.IsFinite(seconds)
                ? seconds
                : null);
    }

    private sealed record TrickplaySource(
        string Identity,
        double? DurationSeconds);
}
