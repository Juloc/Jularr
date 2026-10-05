using Jularr.Web.Features.Localization;
using Jularr.Web.Features.Manga;
using Jularr.Web.Features.Metadata;
using Jularr.Web.Features.MediaCore;
using Jularr.Web.Features.MediaMapping;
using Jularr.Web.Features.Progress;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Web.Features.Tracking;

public enum ExternalProgressMediaKind
{
    Anime,
    Episode,
    Manga,
    Novel
}

/// <summary>
/// How the local work is tied to its AniList entry. Numeric match scores are
/// not persisted, so this is the strongest canonical statement available:
/// an open Mapping Review task, explicit range/segment mappings, a plain
/// metadata match, or no match at all.
/// </summary>
public enum ExternalMappingBasis
{
    NotMatched,
    NeedsReview,
    Matched,
    EpisodeRanges,
    ManualSegments,
    AutomaticSegments
}

/// <summary>
/// Local-only half of the external progress card. Built from local data during
/// a normal page GET and never contacts AniList; remote progress is loaded
/// separately through <see cref="ExternalProgressRemoteView"/>.
/// </summary>
public sealed record ExternalProgressSummary(
    ExternalProgressMediaKind MediaKind,
    bool HasLocalProgress,
    string LocalProgressText,
    string? MatchedTitle,
    int? MatchedMediaId,
    ExternalMappingBasis MappingBasis,
    string? ReviewReason)
{
    public bool IsMatched => MatchedMediaId is not null;

    public string? AniListUrl => MatchedMediaId is int mediaId
        ? AniListMediaUrl(MediaKind, mediaId)
        : null;

    public static string AniListMediaUrl(
        ExternalProgressMediaKind mediaKind,
        int mediaId) =>
        mediaKind is ExternalProgressMediaKind.Anime or ExternalProgressMediaKind.Episode
            ? $"https://anilist.co/anime/{mediaId}"
            : $"https://anilist.co/manga/{mediaId}";
}

/// <summary>View model of the shared <c>_ExternalProgress</c> partial.</summary>
/// <remarks>
/// <paramref name="Ui"/> defaults to English because the partial is shared by
/// pages that are not all migrated to the per-request UI catalog yet (issue
/// #185); a migrated page passes its own resolved <c>Model.Ui</c>.
/// </remarks>
public sealed record ExternalProgressPanel(
    ExternalProgressSummary Summary,
    string RemoteStateUrl,
    string? MatchedCoverUrl = null,
    string? MatchUrl = null,
    string? MappingUrl = null,
    bool CanManageMapping = false,
    UiTextBundle? Ui = null);

/// <summary>
/// View model of the lazily loaded <c>_ExternalProgressState</c> fragment. Mapping state is an
/// owner-only diagnostic that lives inside the owner's Manage sheet, never as a card in this
/// consumer-facing fragment (#519, part of epic #510: "a normal user must not see mapping state
/// at all"; "for the owner, ... not a card").
/// </summary>
public sealed record ExternalProgressRemoteView(
    ExternalProgressMediaKind MediaKind,
    AniListExternalProgressState State,
    string? SyncHandler = null,
    UiTextBundle? Ui = null);

public sealed partial class AniListAccountService
{
    private static readonly string[] AnimeReviewPurposes = ["identity", "episode-ranges"];
    private static readonly string[] ReadingReviewPurposes = ["identity", "reading-segments"];

    /// <summary>
    /// Anime-level AniList state. The canonical local position of an anime is
    /// its furthest watched regular episode (specials excluded); that episode
    /// is resolved and compared through the same context used by sync.
    /// </summary>
    public async Task<AniListExternalProgressState> GetAnimeProgressStateAsync(
        Guid animeId,
        CancellationToken cancellationToken)
    {
        var review = await mappingReviewStore.FindPendingAsync(
            "anime",
            animeId.ToString(),
            AnimeReviewPurposes,
            cancellationToken);
        if (review is not null)
        {
            return ClassifyProgressState(
                review.LocalTitle,
                0,
                null,
                review.Reason,
                canSync: false,
                forcedKind: AniListExternalProgressStateKind.MappingNeedsReview);
        }

        var latest = await FindLatestWatchedEpisodeAsync(
            animeId,
            cancellationToken);
        if (latest is null)
        {
            return ClassifyProgressState(
                null,
                0,
                null,
                "Watch an episode in Jularr to compare progress with AniList.",
                canSync: false,
                forcedKind: AniListExternalProgressStateKind.NoLocalProgress);
        }

        return await GetEpisodeProgressStateAsync(
            latest.Id,
            cancellationToken);
    }

    public async Task<AniListProgressSyncResult> SyncAnimeProgressAsync(
        Guid animeId,
        CancellationToken cancellationToken)
    {
        var latest = await FindLatestWatchedEpisodeAsync(
            animeId,
            cancellationToken);
        return latest is null
            ? new AniListProgressSyncResult(
                Success: false,
                Changed: false,
                "Watch an episode in Jularr before syncing progress.")
            : await SyncEpisodeProgressAsync(
                latest.Id,
                cancellationToken);
    }

    public async Task<ExternalProgressSummary?> GetAnimeProgressSummaryAsync(
        Guid animeId,
        CancellationToken cancellationToken)
    {
        var exists = await db.Anime
            .AsNoTracking()
            .AnyAsync(x => x.Id == animeId, cancellationToken);
        if (!exists)
        {
            return null;
        }

        var episodeCount = await db.Episodes
            .AsNoTracking()
            .CountAsync(
                x => x.AnimeId == animeId && x.SeasonNumber > 0 && x.Number > 0,
                cancellationToken);
        var progress = await new VideoProgressService(db).GetLegacyEpisodeProgressAsync(
            currentAccount.ProfileId,
            [animeId],
            cancellationToken);
        var watchedCount = progress.Count(x => x.IsCompleted && x.SeasonNumber > 0 && x.EpisodeNumber > 0);

        var latest = await FindLatestWatchedEpisodeAsync(
            animeId,
            cancellationToken);
        var localText = latest is null
            ? "No episodes watched yet"
            : $"{watchedCount} of {episodeCount} episodes watched · latest S{latest.SeasonNumber:00}E{latest.Number:00}";

        var identity = await ResolveAnimeIdentityAsync(
            animeId,
            latest?.Id,
            cancellationToken);

        return new ExternalProgressSummary(
            ExternalProgressMediaKind.Anime,
            latest is not null,
            localText,
            identity.Title,
            identity.MediaId,
            identity.Basis,
            identity.ReviewReason);
    }

    public async Task<ExternalProgressSummary?> GetEpisodeProgressSummaryAsync(
        Guid episodeId,
        CancellationToken cancellationToken)
    {
        var episode = await db.Episodes
            .AsNoTracking()
            .Where(x => x.Id == episodeId)
            .Select(x => new
            {
                x.AnimeId,
                x.SeasonNumber,
                x.Number
            })
            .SingleOrDefaultAsync(cancellationToken);

        if (episode is null)
        {
            return null;
        }

        var progress = (await new VideoProgressService(db).GetLegacyEpisodeProgressAsync(
                currentAccount.ProfileId,
                [episode.AnimeId],
                cancellationToken))
            .SingleOrDefault(x => x.EpisodeId == episodeId);

        var code = $"S{episode.SeasonNumber:00}E{episode.Number:00}";
        var hasProgress = progress is not null &&
            (progress.IsCompleted || progress.PositionMs > 0);
        var localText = progress switch
        {
            { IsCompleted: true } => $"{code} watched",
            { DurationMs: > 0, PositionMs: > 0 } =>
                $"{code} · {Math.Clamp((int)Math.Round(progress.PositionMs * 100d / progress.DurationMs.Value), 0, 100)}% watched",
            _ => $"{code} not watched yet"
        };

        var identity = await ResolveAnimeIdentityAsync(
            episode.AnimeId,
            episodeId,
            cancellationToken);

        return new ExternalProgressSummary(
            ExternalProgressMediaKind.Episode,
            hasProgress,
            localText,
            identity.Title,
            identity.MediaId,
            identity.Basis,
            identity.ReviewReason);
    }

    public async Task<ExternalProgressSummary?> GetMangaProgressSummaryAsync(
        Guid seriesId,
        CancellationToken cancellationToken)
    {
        var repository = new MangaRepository(db);
        var source = await repository.GetAutoMatchSourceAsync(
            seriesId,
            cancellationToken);
        if (source is null)
        {
            return null;
        }

        var local = await repository.GetAniListProgressContextAsync(
            currentAccount.ProfileId,
            seriesId,
            cancellationToken);

        var localText = local is null
            ? "Not started"
            : FormatMangaPosition(local);

        var segment = local is null
            ? null
            : await segmentMappings.ResolveAsync(
                "manga",
                seriesId.ToString(),
                local.ChapterNumber,
                local.VolumeNumber,
                local.PageIndex,
                Math.Max(0, local.PageCount - 1),
                cancellationToken);

        var identity = await ResolveReadingIdentityAsync(
            "manga",
            seriesId,
            local?.Title ?? source.Title,
            source.MetadataExternalId,
            segment,
            cancellationToken);

        return new ExternalProgressSummary(
            ExternalProgressMediaKind.Manga,
            local is not null,
            localText,
            identity.Title,
            identity.MediaId,
            identity.Basis,
            identity.ReviewReason);
    }

    public async Task<ExternalProgressSummary?> GetNovelProgressSummaryAsync(
        Guid workId,
        CancellationToken cancellationToken)
    {
        var work = await db.NovelWorks
            .AsNoTracking()
            .Where(x => x.Id == workId)
            .Select(x => new
            {
                Title = x.MetadataTitle ?? x.Title,
                x.MetadataProvider,
                x.MetadataExternalId
            })
            .SingleOrDefaultAsync(cancellationToken);

        if (work is null)
        {
            return null;
        }

        var local = await (
            from progress in db.NovelProgress.AsNoTracking()
            join chapter in db.NovelChapters.AsNoTracking()
                on progress.ChapterId equals chapter.Id
            where progress.ProfileId == currentAccount.ProfileId &&
                  progress.WorkId == workId &&
                  chapter.WorkId == workId
            select new
            {
                chapter.Number,
                progress.PositionPermille
            })
            .SingleOrDefaultAsync(cancellationToken);

        var localText = local is null
            ? "Not started"
            : $"Chapter {local.Number} · {Math.Clamp(local.PositionPermille / 10, 0, 100)}% read";

        var segment = local is null
            ? null
            : await segmentMappings.ResolveAsync(
                "novel",
                workId.ToString(),
                local.Number,
                localVolumeNumber: null,
                local.PositionPermille,
                completedThreshold: 950,
                cancellationToken);

        var identity = await ResolveReadingIdentityAsync(
            "novel",
            workId,
            work.Title,
            string.Equals(
                work.MetadataProvider,
                "anilist",
                StringComparison.OrdinalIgnoreCase)
                ? work.MetadataExternalId
                : null,
            segment,
            cancellationToken);

        return new ExternalProgressSummary(
            ExternalProgressMediaKind.Novel,
            local is not null,
            localText,
            identity.Title,
            identity.MediaId,
            identity.Basis,
            identity.ReviewReason);
    }

    /// <summary>
    /// The anime's canonical CompletedThrough episode (see <see cref="VideoProgressService.GetCompletedThroughAsync"/>)
    /// addressed by its legacy episode identity: the only episode whose number AniList write-back may use.
    /// </summary>
    private async Task<LatestWatchedEpisode?> FindLatestWatchedEpisodeAsync(
        Guid animeId,
        CancellationToken cancellationToken)
    {
        var workId = await db.WorkSourceLinks
            .AsNoTracking()
            .Where(x => x.SourceKind == WorkSourceKind.Anime && x.SourceId == animeId)
            .Select(x => (Guid?)x.WorkId)
            .SingleOrDefaultAsync(cancellationToken);
        if (workId is null)
        {
            return null;
        }

        var videoProgress = new VideoProgressService(db);
        var completion = (await videoProgress.GetCompletedThroughAsync(currentAccount.ProfileId, workId, cancellationToken)).SingleOrDefault();
        if (completion?.CompletedThrough is not { } through)
        {
            return null;
        }

        var identities = await videoProgress.GetLegacyEpisodeIdentitiesAsync([through.WorkEpisodeId], cancellationToken);
        return identities.TryGetValue(through.WorkEpisodeId, out var identity)
            ? new LatestWatchedEpisode(identity.EpisodeId, through.SeasonNumber, through.EpisodeNumber)
            : null;
    }

    private async Task<ExternalIdentity> ResolveAnimeIdentityAsync(
        Guid animeId,
        Guid? episodeId,
        CancellationToken cancellationToken)
    {
        var review = await mappingReviewStore.FindPendingAsync(
            "anime",
            animeId.ToString(),
            AnimeReviewPurposes,
            cancellationToken);

        var ranges = await store.LoadEpisodeMappingsAsync(
            animeId,
            cancellationToken);

        // The episode resolver is local-only; it is the same resolver used by sync.
        var resolved = episodeId is Guid id
            ? await metadataService.ResolveEpisodeAsync(id, cancellationToken)
            : null;

        string? title;
        int? mediaId;
        if (resolved is not null)
        {
            title = resolved.PreferredTitle;
            mediaId = ParseAniListId(resolved.Provider, resolved.ExternalId);
        }
        else
        {
            var metadata = await metadataService.GetAsync(
                animeId,
                cancellationToken);
            title = metadata?.PreferredTitle;
            mediaId = metadata is null
                ? null
                : ParseAniListId(metadata.Provider, metadata.ExternalId);
        }

        var basis = review is not null
            ? ExternalMappingBasis.NeedsReview
            : ranges.Count > 0
                ? ExternalMappingBasis.EpisodeRanges
                : mediaId is not null
                    ? ExternalMappingBasis.Matched
                    : ExternalMappingBasis.NotMatched;

        return new ExternalIdentity(
            mediaId is null ? null : title,
            mediaId,
            basis,
            review?.Reason);
    }

    private async Task<ExternalIdentity> ResolveReadingIdentityAsync(
        string mediaType,
        Guid localId,
        string localTitle,
        string? metadataExternalId,
        ReadingSegmentResolution? activeSegment,
        CancellationToken cancellationToken)
    {
        var review = await mappingReviewStore.FindPendingAsync(
            mediaType,
            localId.ToString(),
            ReadingReviewPurposes,
            cancellationToken);

        var segments = await segmentMappings.ListAsync(
            mediaType,
            localId.ToString(),
            cancellationToken);

        string? title;
        int? mediaId;
        if (activeSegment is { CanSync: true })
        {
            title = activeSegment.PreferredTitle ?? localTitle;
            mediaId = ParseAniListId(activeSegment.Provider, activeSegment.ExternalId);
        }
        else
        {
            title = localTitle;
            mediaId = ParseAniListId("anilist", metadataExternalId);
        }

        var basis = review is not null
            ? ExternalMappingBasis.NeedsReview
            : segments.Count > 0
                ? segments.Any(x => !string.Equals(
                        x.Source,
                        "automatic",
                        StringComparison.OrdinalIgnoreCase))
                    ? ExternalMappingBasis.ManualSegments
                    : ExternalMappingBasis.AutomaticSegments
                : mediaId is not null
                    ? ExternalMappingBasis.Matched
                    : ExternalMappingBasis.NotMatched;

        return new ExternalIdentity(
            mediaId is null ? null : title,
            mediaId,
            basis,
            review?.Reason);
    }

    private static string FormatMangaPosition(MangaAniListProgressContext local)
    {
        var chapter = local.ChapterNumber.ToString(
            "0.##",
            System.Globalization.CultureInfo.InvariantCulture);
        var volume = local.VolumeNumber is int number
            ? $"Vol. {number} · "
            : "";
        return local.PageCount > 0
            ? $"{volume}Chapter {chapter} · page {Math.Min(local.PageIndex + 1, local.PageCount)} of {local.PageCount}"
            : $"{volume}Chapter {chapter}";
    }

    private static int? ParseAniListId(string? provider, string? externalId) =>
        string.Equals(
            provider,
            AniListMetadataProvider.ProviderKey,
            StringComparison.OrdinalIgnoreCase) &&
        int.TryParse(externalId, out var id) &&
        id > 0
            ? id
            : null;

    private sealed record LatestWatchedEpisode(
        Guid Id,
        int SeasonNumber,
        int Number);

    private sealed record ExternalIdentity(
        string? Title,
        int? MediaId,
        ExternalMappingBasis Basis,
        string? ReviewReason);
}
