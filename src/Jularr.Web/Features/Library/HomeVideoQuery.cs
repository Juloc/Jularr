using Jularr.Web.Data;
using Jularr.Web.Features.Artwork;
using Jularr.Web.Features.Learning;
using Jularr.Web.Features.MediaCore;
using Jularr.Web.Features.Progress;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Web.Features.Library;

/// <summary>
/// What Home shows about one video title (Anime, Series or Movie), resolved for the viewer. <see cref="Score"/> is on the 0-100 scale of
/// AniList cards; persisted Movie and Series ratings are 0-10 and converted.
/// </summary>
public sealed record HomeVideoTitle(
    WorkMediaType MediaType,
    Guid WorkId,
    string Title,
    string DetailHref,
    string? PosterUrl,
    string? BackdropUrl,
    string? Description,
    int? Year,
    int? Score,
    int SeasonCount);

/// <summary>One Continue Watching item: the title plus where to resume or what plays next. <see cref="PlayHref"/> is the player address.</summary>
public sealed record HomeContinueVideo(
    VideoContinueWatchingKind Kind,
    HomeVideoTitle Title,
    int? SeasonNumber,
    int? EpisodeNumber,
    string? EpisodeTitle,
    string PlayHref,
    string? PosterUrl,
    long ResumePositionMs,
    long? DurationMs,
    DateTime UpdatedAt)
{
    public int Percent => DurationMs is > 0 ? Math.Clamp((int)Math.Round(ResumePositionMs * 100d / DurationMs.Value), 0, 100) : 0;

    public long? RemainingMs => DurationMs is > 0 ? Math.Max(0, DurationMs.Value - ResumePositionMs) : null;
}

/// <summary>A title in the library with its most recently added playable unit; Movies have no season or episode number.</summary>
/// <param name="LegacyEpisodeId">The legacy episode record of an Anime unit, which the vocabulary coverage of Home is still keyed by.</param>
public sealed record HomeRecentVideo(HomeVideoTitle Title, int? SeasonNumber, int? EpisodeNumber, DateTime AddedAt, Guid? LegacyEpisodeId);

/// <summary>One playback session of the profile's history, addressed by the player route of its media type.</summary>
public sealed record HomePlaybackEntry(Guid Id, HomeVideoTitle Title, int? SeasonNumber, int? EpisodeNumber, string PlayHref, DateTime LastPlayedAt, long PositionMs, bool ReachedEnd);

/// <summary>
/// The Home read model for video: Continue Watching, recently added titles and playback history across Anime, Series and Movies. Progress
/// and history come from the canonical <see cref="VideoProgressService"/>; titles, artwork and files come from the canonical Work,
/// WorkEpisode and MediaAsset rows. Every read is profile-scoped where it is personal, bounded and set-based, and none of them writes.
/// Anime is still routed by its legacy records (detail and player) until its pages move to the Work.
/// </summary>
public sealed class HomeVideoQuery(AppDbContext db, VideoProgressService videoProgress)
{
    public async Task<IReadOnlyList<HomeContinueVideo>> GetContinueAsync(string profileId, IReadOnlyCollection<WorkMediaType> mediaTypes, int limit, CancellationToken cancellationToken)
    {
        var items = await videoProgress.GetContinueWatchingAsync(profileId, limit, mediaTypes, cancellationToken);
        if (items.Count == 0)
        {
            return [];
        }

        var titles = await LoadTitlesAsync(profileId, [.. items.Select(item => item.WorkId)], includeDescriptions: true, cancellationToken);
        var identities = await videoProgress.GetLegacyEpisodeIdentitiesAsync([.. items.Where(item => item.WorkEpisodeId.HasValue).Select(item => item.WorkEpisodeId!.Value)], cancellationToken);

        var result = new List<HomeContinueVideo>(items.Count);
        foreach (var item in items)
        {
            if (!titles.TryGetValue(item.WorkId, out var title))
            {
                continue;
            }

            var playHref = VideoDetailView.WatchHref(item.WorkId, item.WorkEpisodeId);
            var posterUrl = title.PosterUrl;
            if (item.MediaType == WorkMediaType.Anime)
            {
                if (item.WorkEpisodeId is not { } workEpisodeId || !identities.TryGetValue(workEpisodeId, out var identity))
                {
                    continue;
                }

                playHref = $"/Library/Episode/{identity.EpisodeId}";
                posterUrl = AnimeArtworkStore.ResolveSeasonPosterUrl(identity.AnimeId, item.SeasonNumber ?? 0, title.PosterUrl);
            }

            var resumeMs = item.ResumePositionMs >= VideoProgressService.MinimumResumeMs ? item.ResumePositionMs : 0;
            result.Add(new HomeContinueVideo(item.Kind, title, item.SeasonNumber, item.EpisodeNumber, item.EpisodeTitle, playHref, posterUrl, resumeMs, item.DurationMs, item.UpdatedAt));
        }

        return result;
    }

    /// <summary>
    /// The titles of the library with a playable video file, newest addition first, one entry per title carrying its newest added unit. The
    /// order follows when the file was discovered, so a new episode of a long-known series moves that series to the front.
    /// </summary>
    public async Task<IReadOnlyList<HomeRecentVideo>> GetRecentlyAddedAsync(string profileId, IReadOnlyCollection<WorkMediaType> mediaTypes, int limit, CancellationToken cancellationToken)
    {
        if (mediaTypes.Count == 0)
        {
            return [];
        }

        var types = mediaTypes.Select(type => (int)type).ToArray();
        var rows = await db.Database.SqlQuery<RecentDbRow>(
                $"""
                SELECT latest."WorkId", latest."WorkEpisodeId", latest."AddedAt", latest."SeasonNumber", latest."EpisodeNumber"
                FROM (
                    SELECT DISTINCT ON (asset."WorkId")
                        asset."WorkId", asset."WorkEpisodeId", file."DiscoveredAt" AS "AddedAt", episode."SeasonNumber", episode."EpisodeNumber"
                    FROM "MediaAssets" AS asset
                    INNER JOIN "StoredFiles" AS file ON file."MediaAssetId" = asset."Id"
                    INNER JOIN "Works" AS work ON work."Id" = asset."WorkId"
                    LEFT JOIN "WorkEpisodes" AS episode ON episode."Id" = asset."WorkEpisodeId"
                    WHERE asset."Kind" = {(int)MediaAssetKind.Video} AND work."MediaType" = ANY({types})
                    ORDER BY asset."WorkId", file."DiscoveredAt" DESC, episode."SeasonNumber" DESC NULLS LAST, episode."EpisodeNumber" DESC NULLS LAST
                ) AS latest
                ORDER BY latest."AddedAt" DESC, latest."WorkId"
                LIMIT {limit}
                """)
            .ToListAsync(cancellationToken);
        if (rows.Count == 0)
        {
            return [];
        }

        var titles = await LoadTitlesAsync(profileId, [.. rows.Select(row => row.WorkId)], includeDescriptions: false, cancellationToken);
        var identities = await videoProgress.GetLegacyEpisodeIdentitiesAsync([.. rows.Where(row => row.WorkEpisodeId.HasValue).Select(row => row.WorkEpisodeId!.Value)], cancellationToken);

        return
        [
            .. rows
                .Where(row => titles.ContainsKey(row.WorkId))
                .Select(row => new HomeRecentVideo(
                    titles[row.WorkId],
                    row.SeasonNumber,
                    row.EpisodeNumber,
                    row.AddedAt,
                    row.WorkEpisodeId is { } workEpisodeId && identities.TryGetValue(workEpisodeId, out var identity) ? identity.EpisodeId : null))
        ];
    }

    /// <summary>The profile's recent playback sessions of the given media types, newest first; at most <see cref="VideoProgressService.HistoryLimit"/>.</summary>
    public async Task<IReadOnlyList<HomePlaybackEntry>> GetHistoryAsync(string profileId, IReadOnlyCollection<WorkMediaType> mediaTypes, CancellationToken cancellationToken)
    {
        var entries = (await videoProgress.GetHistoryAsync(profileId, cancellationToken)).Where(entry => mediaTypes.Contains(entry.MediaType)).ToArray();
        if (entries.Length == 0)
        {
            return [];
        }

        var titles = await LoadTitlesAsync(profileId, [.. entries.Select(entry => entry.WorkId).Distinct()], includeDescriptions: false, cancellationToken);
        var identities = await videoProgress.GetLegacyEpisodeIdentitiesAsync([.. entries.Where(entry => entry.WorkEpisodeId.HasValue).Select(entry => entry.WorkEpisodeId!.Value)], cancellationToken);

        var result = new List<HomePlaybackEntry>(entries.Length);
        foreach (var entry in entries)
        {
            if (!titles.TryGetValue(entry.WorkId, out var title))
            {
                continue;
            }

            var playHref = VideoDetailView.WatchHref(entry.WorkId, entry.WorkEpisodeId);
            if (entry.MediaType == WorkMediaType.Anime)
            {
                if (entry.WorkEpisodeId is not { } workEpisodeId || !identities.TryGetValue(workEpisodeId, out var identity))
                {
                    continue;
                }

                playHref = $"/Library/Episode/{identity.EpisodeId}";
            }

            result.Add(new HomePlaybackEntry(entry.Id, title, entry.SeasonNumber, entry.EpisodeNumber, playHref, entry.LastPlayedAt, entry.PositionMs, entry.ReachedEnd));
        }

        return result;
    }

    /// <summary>
    /// The display facts of the given Works. Movie and Series come from the persisted Work metadata, Anime from its legacy record and
    /// provider metadata; a Work without a displayable identity (an Anime without legacy record, which has no page to open) is absent.
    /// </summary>
    private async Task<IReadOnlyDictionary<Guid, HomeVideoTitle>> LoadTitlesAsync(string profileId, Guid[] workIds, bool includeDescriptions, CancellationToken cancellationToken)
    {
        var works = await db.Database.SqlQuery<WorkDbRow>($"""SELECT "Id", "MediaType", "CanonicalTitle", "Year" FROM "Works" WHERE "Id" = ANY({workIds})""").ToListAsync(cancellationToken);
        var animeWorkIds = works.Where(work => work.MediaType == (int)WorkMediaType.Anime).Select(work => work.Id).ToArray();
        var animeRows = animeWorkIds.Length == 0
            ? new Dictionary<Guid, AnimeDbRow>()
            : (await db.Database.SqlQuery<AnimeDbRow>(
                    $"""
                    SELECT DISTINCT ON (link."WorkId")
                        link."WorkId", anime."Id" AS "AnimeId", COALESCE(metadata."PreferredTitle", anime."Title") AS "Title", metadata."CoverImageUrl",
                        metadata."BannerImageUrl", metadata."Description", metadata."SeasonYear", metadata."AverageScore"
                    FROM "WorkSourceLinks" AS link
                    INNER JOIN "Anime" AS anime ON anime."Id" = link."SourceId"
                    LEFT JOIN "AnimeMetadata" AS metadata ON metadata."AnimeId" = anime."Id"
                    WHERE link."SourceKind" = {(int)WorkSourceKind.Anime} AND link."WorkId" = ANY({animeWorkIds})
                    ORDER BY link."WorkId", anime."CreatedAt", anime."Id"
                    """)
                .ToListAsync(cancellationToken))
                .ToDictionary(row => row.WorkId);

        var seasonCounts = (await db.Database.SqlQuery<SeasonCountDbRow>(
                $"""
                SELECT episode."WorkId", COUNT(DISTINCT episode."SeasonNumber")::int AS "Seasons"
                FROM "WorkEpisodes" AS episode
                WHERE episode."WorkId" = ANY({workIds}) AND episode."SeasonNumber" > 0
                  AND EXISTS (
                      SELECT 1
                      FROM "MediaAssets" AS asset
                      INNER JOIN "StoredFiles" AS file ON file."MediaAssetId" = asset."Id"
                      WHERE asset."Kind" = {(int)MediaAssetKind.Video} AND asset."WorkEpisodeId" = episode."Id")
                GROUP BY episode."WorkId"
                """)
            .ToListAsync(cancellationToken))
            .ToDictionary(row => row.WorkId, row => row.Seasons);

        var videoWorkIds = works.Where(work => work.MediaType is (int)WorkMediaType.Movie or (int)WorkMediaType.Series).Select(work => work.Id).ToArray();
        IReadOnlyDictionary<Guid, WorkCardMetadata> cardMetadata = new Dictionary<Guid, WorkCardMetadata>();
        IReadOnlyDictionary<Guid, string> overviews = new Dictionary<Guid, string>();
        if (videoWorkIds.Length > 0)
        {
            var locale = await WorkMetadataLocales.ForProfileAsync(db, profileId, cancellationToken);
            var metadataStore = new WorkMetadataStore(db);
            var cardRows = await metadataStore.LoadCardMetadataAsync(videoWorkIds, cancellationToken);
            cardMetadata = cardRows.Artwork.Count == 0 && cardRows.Titles.Count == 0 && cardRows.Facts.Count == 0 ? cardMetadata : WorkMetadataPresentation.ResolveCards(cardRows, locale);
            overviews = includeDescriptions ? await metadataStore.LoadOverviewsAsync(videoWorkIds, locale, cancellationToken) : overviews;
        }

        var titles = new Dictionary<Guid, HomeVideoTitle>();
        foreach (var work in works)
        {
            var mediaType = (WorkMediaType)work.MediaType;
            var seasons = seasonCounts.GetValueOrDefault(work.Id);
            if (mediaType == WorkMediaType.Anime)
            {
                if (animeRows.TryGetValue(work.Id, out var anime))
                {
                    var backdrop = AnimeArtworkStore.ResolveFanartUrl(anime.AnimeId, anime.BannerImageUrl);
                    titles[work.Id] = new HomeVideoTitle(
                        mediaType,
                        work.Id,
                        anime.Title,
                        LibraryBrowse.DetailHref(WorkMediaType.Anime, anime.AnimeId),
                        AnimeArtworkStore.ResolvePosterUrl(anime.AnimeId, anime.CoverImageUrl),
                        string.IsNullOrWhiteSpace(backdrop) ? null : backdrop,
                        includeDescriptions && !string.IsNullOrWhiteSpace(anime.Description) ? anime.Description.Trim() : null,
                        anime.SeasonYear ?? work.Year,
                        anime.AverageScore,
                        seasons);
                }

                continue;
            }

            cardMetadata.TryGetValue(work.Id, out var metadata);
            titles[work.Id] = new HomeVideoTitle(
                mediaType,
                work.Id,
                metadata?.Title ?? work.CanonicalTitle,
                LibraryBrowse.DetailHref(mediaType, work.Id),
                metadata?.PosterUrl,
                metadata?.BackdropUrl,
                overviews.GetValueOrDefault(work.Id),
                work.Year,
                metadata?.Rating is { } rating ? (int)Math.Round(rating * 10, MidpointRounding.AwayFromZero) : null,
                seasons);
        }

        return titles;
    }

    private sealed record RecentDbRow(Guid WorkId, Guid? WorkEpisodeId, DateTime AddedAt, int? SeasonNumber, int? EpisodeNumber);

    private sealed record WorkDbRow(Guid Id, int MediaType, string CanonicalTitle, int? Year);

    private sealed record AnimeDbRow(Guid WorkId, Guid AnimeId, string Title, string? CoverImageUrl, string? BannerImageUrl, string? Description, int? SeasonYear, int? AverageScore);

    private sealed record SeasonCountDbRow(Guid WorkId, int Seasons);
}
