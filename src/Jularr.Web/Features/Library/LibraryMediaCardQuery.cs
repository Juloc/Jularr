using Jularr.Web.Data;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Artwork;
using Jularr.Web.Features.InstantPlay;
using Jularr.Web.Features.MediaCore;
using Jularr.Web.Features.Playback;
using Jularr.Web.Features.Progress;
using Jularr.Web.Features.Providers;
using Jularr.Web.Ui;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Web.Features.Library;

/// <summary>
/// The one profile-scoped Library read model: Anime, Series and Movie titles as canonical <see cref="Work"/>s. It loads
/// the whole grid with a fixed number of set-based queries regardless of library size, then assembles each card in memory.
/// <list type="bullet">
/// <item>Membership: a Work with a legacy library record (Anime, Movie, Series), a playable video file or an open request.
/// Provider candidates that were only searched never reach the Library.</item>
/// <item>Availability and languages come from canonical <c>MediaAsset -> StoredFile -> MediaTrack</c>; known units come from
/// <see cref="WorkEpisode"/> (Movies are a single unit). Embedded streams and imported sidecar subtitles are unioned, most
/// common first; sidecar subtitles have no canonical track yet and reach the Work through their legacy episode link.</item>
/// <item>Progress and the next episode come from the profile's canonical <c>MediaProgress</c>; see <see cref="NextRequiredEpisode"/>.</item>
/// <item>Requested and partial states are projections of the shared acquisition requests and of the files, never Library state.</item>
/// <item>Movie and Series posters, backdrops and ratings come from the persisted Work metadata (#820) in one query for the whole page;
/// a title the spool has not fetched yet simply has none.</item>
/// <item>Anime keeps its legacy-keyed detail and player routes plus its provider metadata and artwork until those move to the
/// Work (#820); they are reached through the Anime <see cref="WorkSourceLink"/>, which the canonical backfill guarantees.</item>
/// </list>
/// </summary>
public sealed class LibraryMediaCardQuery(AppDbContext db, TimeProvider? clock = null)
{
    private static readonly MediaAcquisitionKind[] RequestKinds = [MediaAcquisitionKind.Anime, MediaAcquisitionKind.Movie, MediaAcquisitionKind.Tv];


    /// <summary>
    /// The Library page's read model for the given media types: the banner-card facts plus what browsing needs (poster,
    /// format, when the title was added and last watched, how many units are playable or missing). A failing open-request
    /// lookup only drops the requested state and flags the result as degraded.
    /// </summary>
    public Task<LibraryEntries> GetEntriesAsync(string profileId, IReadOnlyCollection<WorkMediaType> mediaTypes, CancellationToken cancellationToken) =>
        LoadEntriesAsync(profileId, mediaTypes, null, null, cancellationToken);

    /// <summary>The same entries, restricted to the Anime Works that link to the given legacy anime records: a page that looks at a few titles does not read the whole Anime library.</summary>
    public Task<LibraryEntries> GetAnimeEntriesAsync(string profileId, IReadOnlyCollection<Guid> legacyAnimeIds, CancellationToken cancellationToken) =>
        LoadEntriesAsync(profileId, [WorkMediaType.Anime], legacyAnimeIds, null, cancellationToken);

    /// <summary>The same entries, restricted to the given Movie and Series Works.</summary>
    public Task<LibraryEntries> GetVideoWorkEntriesAsync(string profileId, IReadOnlyCollection<Guid> workIds, CancellationToken cancellationToken) =>
        LoadEntriesAsync(profileId, [WorkMediaType.Movie, WorkMediaType.Series], null, workIds, cancellationToken);

    private async Task<LibraryEntries> LoadEntriesAsync(
        string profileId,
        IReadOnlyCollection<WorkMediaType> mediaTypes,
        IReadOnlyCollection<Guid>? onlyLegacyAnimeIds,
        IReadOnlyCollection<Guid>? onlyWorkIds,
        CancellationToken cancellationToken)
    {
        var scope = LibraryBrowse.VideoMediaTypes.Where(mediaTypes.Contains).ToArray();
        // Anime is a classification of a Movie or Series: while the Anime section is shown the classified titles belong to it, otherwise they are ordinary Movies and Series.
        var animeVisible = scope.Contains(WorkMediaType.Anime);
        var inAnime = animeVisible;
        var inSeries = scope.Contains(WorkMediaType.Series);
        var inMovies = scope.Contains(WorkMediaType.Movie);
        if (scope.Length == 0)
        {
            return new LibraryEntries([], false);
        }

        // The stage of an open request per provider id, for the availability badge (#597). Losing it only hides the
        // requested state, so the grid still renders.
        var degraded = false;
        Dictionary<(WorkMediaType MediaType, string ExternalId), AcquisitionRequestStatus> openRequests;
        try
        {
            openRequests = await LoadOpenRequestsAsync(scope, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            openRequests = [];
            degraded = true;
        }

        var requestByWork = new Dictionary<Guid, AcquisitionRequestStatus>();
        var videoRequestIds = openRequests.Keys.Where(x => x.MediaType != WorkMediaType.Anime).Select(x => x.ExternalId).Distinct().ToArray();
        if (videoRequestIds.Length > 0)
        {
            var identities = await db.WorkExternalIdentities
                .AsNoTracking()
                .Where(x => x.Provider == ProviderKeys.Tmdb && videoRequestIds.Contains(x.ExternalId) && (x.MediaType == WorkMediaType.Movie || x.MediaType == WorkMediaType.Series))
                .Select(x => new { x.WorkId, x.MediaType, x.ExternalId })
                .ToListAsync(cancellationToken);
            foreach (var identity in identities.Where(x => openRequests.ContainsKey((x.MediaType, x.ExternalId))))
            {
                requestByWork[identity.WorkId] = openRequests[(identity.MediaType, identity.ExternalId)];
            }
        }

        var requestedWorkIds = requestByWork.Keys.ToArray();
        var works = await db.Works
            .AsNoTracking()
            .Where(work => ((work.IsAnime && inAnime && (work.MediaType == WorkMediaType.Series || work.MediaType == WorkMediaType.Movie))
                    || (!(work.IsAnime && inAnime) && ((work.MediaType == WorkMediaType.Series && inSeries) || (work.MediaType == WorkMediaType.Movie && inMovies))))
                && (onlyWorkIds == null || onlyWorkIds.Contains(work.Id))
                && (onlyLegacyAnimeIds == null
                    || db.WorkSourceLinks.Any(link => link.WorkId == work.Id && link.SourceKind == WorkSourceKind.Anime && onlyLegacyAnimeIds.Contains(link.SourceId)))
                && (requestedWorkIds.Contains(work.Id)
                    || db.WorkSourceLinks.Any(link => link.WorkId == work.Id
                        && (link.SourceKind == WorkSourceKind.Anime || link.SourceKind == WorkSourceKind.Movie || link.SourceKind == WorkSourceKind.Series))
                    || db.MediaAssets.Any(asset => asset.WorkId == work.Id
                        && asset.Kind == MediaAssetKind.Video
                        && db.StoredFiles.Any(file => file.MediaAssetId == asset.Id))))
            .Select(work => new WorkRow(work.Id, work.IsAnime && animeVisible ? WorkMediaType.Anime : work.MediaType, work.MediaType, work.CanonicalTitle, work.Year, work.CreatedAt))
            .ToListAsync(cancellationToken);
        if (works.Count == 0)
        {
            return new LibraryEntries([], degraded);
        }

        var workIds = works.Select(x => x.Id).ToArray();
        var animeWorkIds = works.Where(x => x.MediaType == WorkMediaType.Anime).Select(x => x.Id).ToArray();
        var episodicWorkIds = works.Where(x => x.Technical == WorkMediaType.Series).Select(x => x.Id).ToArray();
        var movieWorkIds = works.Where(x => x.Technical == WorkMediaType.Movie).Select(x => x.Id).ToArray();

        var animeRows = new Dictionary<Guid, AnimeRow>();
        var legacyEpisodeIds = new Dictionary<(Guid WorkId, int SeasonNumber, int Number), Guid>();
        var sidecarSubtitles = new List<LanguageRow>();
        if (animeWorkIds.Length > 0)
        {
            var anime = await (
                from link in db.WorkSourceLinks.AsNoTracking()
                where link.SourceKind == WorkSourceKind.Anime && animeWorkIds.Contains(link.WorkId)
                join animeValue in db.Anime.AsNoTracking() on link.SourceId equals animeValue.Id
                join metadataValue in db.AnimeMetadata.AsNoTracking() on animeValue.Id equals metadataValue.AnimeId into metadataRows
                from metadata in metadataRows.DefaultIfEmpty()
                join localValue in db.AnimeLocalMetadata.AsNoTracking() on animeValue.Id equals localValue.AnimeId into localRows
                from local in localRows.DefaultIfEmpty()
                select new AnimeRow(link.WorkId, animeValue.Id, metadata == null ? animeValue.Title : metadata.PreferredTitle, metadata == null ? null : metadata.Status,
                    metadata != null && metadata.SeasonYear != null ? metadata.SeasonYear : local == null ? null : local.Year, metadata == null ? null : metadata.AverageScore,
                    metadata == null ? null : metadata.EpisodeCount, metadata == null ? null : metadata.BannerImageUrl, metadata == null ? null : metadata.CoverImageUrl,
                    metadata == null ? null : metadata.Provider, metadata == null ? null : metadata.ExternalId, metadata == null ? null : metadata.Format, animeValue.CreatedAt))
                .ToListAsync(cancellationToken);
            animeRows = anime.GroupBy(x => x.WorkId).ToDictionary(group => group.Key, group => group.OrderBy(x => x.CreatedAt).ThenBy(x => x.AnimeId).First());

            foreach (var row in animeRows.Values.Where(x => x.Provider == ProviderKeys.AniList && x.ExternalId is not null))
            {
                if (openRequests.TryGetValue((WorkMediaType.Anime, row.ExternalId!), out var status))
                {
                    requestByWork[row.WorkId] = status;
                }
            }

            var legacyEpisodes = await (
                from link in db.WorkSourceLinks.AsNoTracking()
                where link.SourceKind == WorkSourceKind.Episode && animeWorkIds.Contains(link.WorkId)
                join episode in db.Episodes.AsNoTracking() on link.SourceId equals episode.Id
                select new { link.WorkId, episode.SeasonNumber, episode.Number, episode.Id })
                .ToListAsync(cancellationToken);
            legacyEpisodeIds = legacyEpisodes
                .GroupBy(x => (x.WorkId, x.SeasonNumber, x.Number))
                .ToDictionary(group => group.Key, group => group.Min(x => x.Id));

            sidecarSubtitles = await (
                from track in db.SubtitleTracks.AsNoTracking()
                join link in db.WorkSourceLinks.AsNoTracking() on track.EpisodeId equals link.SourceId
                where link.SourceKind == WorkSourceKind.Episode && animeWorkIds.Contains(link.WorkId)
                group track by new { link.WorkId, track.Language } into languageGroup
                select new LanguageRow(languageGroup.Key.WorkId, MediaTrackKind.Subtitle, languageGroup.Key.Language, languageGroup.Count(), int.MaxValue))
                .ToListAsync(cancellationToken);
        }

        var units = episodicWorkIds.Length == 0
            ? Enumerable.Empty<UnitRow>().ToLookup(x => x.WorkId)
            : (await db.WorkEpisodes
                    .AsNoTracking()
                    .Where(episode => episodicWorkIds.Contains(episode.WorkId))
                    .Select(episode => new UnitRow(
                        episode.Id,
                        episode.WorkId,
                        episode.SeasonNumber,
                        episode.EpisodeNumber,
                        db.MediaAssets.Any(asset => asset.WorkEpisodeId == episode.Id
                            && asset.Kind == MediaAssetKind.Video
                            && db.StoredFiles.Any(file => file.MediaAssetId == asset.Id)),
                        episode.AiredAt))
                    .ToListAsync(cancellationToken))
                .ToLookup(x => x.WorkId);

        var movieFiles = movieWorkIds.Length == 0
            ? Enumerable.Empty<MovieFileRow>().ToLookup(x => x.WorkId)
            : (await (
                    from asset in db.MediaAssets.AsNoTracking()
                    join file in db.StoredFiles.AsNoTracking() on (Guid?)asset.Id equals file.MediaAssetId
                    where asset.Kind == MediaAssetKind.Video && movieWorkIds.Contains(asset.WorkId)
                    select new MovieFileRow(
                        asset.WorkId,
                        db.MediaTechnicalAnalyses
                            .Where(analysis => analysis.MediaFileId == file.Id && analysis.Status == MediaAnalysisStatus.Succeeded)
                            .Select(analysis => analysis.DurationSeconds)
                            .FirstOrDefault()))
                .ToListAsync(cancellationToken))
                .ToLookup(x => x.WorkId);

        var embeddedTracks = await (
            from track in db.MediaTracks.AsNoTracking()
            join file in db.StoredFiles.AsNoTracking() on track.MediaFileId equals file.Id
            join asset in db.MediaAssets.AsNoTracking() on file.MediaAssetId equals (Guid?)asset.Id
            where asset.Kind == MediaAssetKind.Video && workIds.Contains(asset.WorkId) && track.Language != null
            group track by new { asset.WorkId, track.Kind, track.Language } into languageGroup
            select new LanguageRow(languageGroup.Key.WorkId, languageGroup.Key.Kind, languageGroup.Key.Language!, languageGroup.Count(), languageGroup.Min(x => x.StreamIndex)))
            .ToListAsync(cancellationToken);

        // Movie and Series cards show the locally persisted title, artwork and rating (#820); Anime keeps its own until it moves to the Work.
        IReadOnlyDictionary<Guid, WorkCardMetadata> cardMetadata = new Dictionary<Guid, WorkCardMetadata>();
        var videoWorkIds = works.Select(x => x.Id).ToArray();
        if (videoWorkIds.Length > 0)
        {
            var rows = await new WorkMetadataStore(db).LoadCardMetadataAsync(videoWorkIds, cancellationToken);
            var empty = rows.Artwork.Count == 0 && rows.Titles.Count == 0 && rows.Facts.Count == 0;
            cardMetadata = empty ? cardMetadata : WorkMetadataPresentation.ResolveCards(rows, await WorkMetadataLocales.ForProfileAsync(db, profileId, cancellationToken));
        }

        var progress = await new VideoProgressService(db).ListAsync(profileId, workIds, cancellationToken);
        var context = new ReadContext(
            units,
            legacyEpisodeIds,
            progress
                .Where(x => x.WorkEpisodeId.HasValue)
                .ToDictionary(x => x.WorkEpisodeId!.Value, x => new EpisodeProgressState(x.WorkEpisodeId!.Value, x.PositionMs, x.IsCompleted, x.UpdatedAt!.Value)),
            progress.Where(x => !x.WorkEpisodeId.HasValue).ToDictionary(x => x.WorkId),
            movieFiles,
            embeddedTracks.Concat(sidecarSubtitles).ToLookup(x => (x.WorkId, x.Kind)),
            animeRows,
            requestByWork,
            cardMetadata,
            (clock ?? TimeProvider.System).GetUtcNow().UtcDateTime);

        var entries = new List<LibraryCardEntry>(works.Count);
        foreach (var work in works)
        {
            if (work.Technical == WorkMediaType.Movie)
            {
                entries.Add(BuildMovie(work, context));
            }
            else
            {
                // An Anime detail page is still keyed by the legacy Anime record; a classified Series without one opens as an ordinary Series.
                entries.Add(BuildEpisodic(work, context.Anime.GetValueOrDefault(work.Id), context));
            }
        }

        return new LibraryEntries(entries, degraded);
    }

    private async Task<Dictionary<(WorkMediaType MediaType, string ExternalId), AcquisitionRequestStatus>> LoadOpenRequestsAsync(
        IReadOnlyCollection<WorkMediaType> scope,
        CancellationToken cancellationToken)
    {
        var store = new AcquisitionAccessStore(db);
        var requests = new Dictionary<(WorkMediaType MediaType, string ExternalId), AcquisitionRequestStatus>();
        foreach (var kind in RequestKinds.Where(kind => scope.Contains(AcquisitionAccessNames.WorkType(kind))))
        {
            var mediaType = AcquisitionAccessNames.WorkType(kind);
            var provider = mediaType == WorkMediaType.Anime ? ProviderKeys.AniList : ProviderKeys.Tmdb;
            var open = await store.ListAsync(kind, requestedByProfileId: null, openOnly: true, limit: 500, cancellationToken);
            foreach (var request in open.Where(x => x.Provider == provider))
            {
                requests.TryAdd((mediaType, request.ExternalId), request.Status);
            }
        }

        return requests;
    }

    private static LibraryCardEntry BuildEpisodic(WorkRow work, AnimeRow? anime, ReadContext context)
    {
        var episodes = context.Units[work.Id].ToArray();
        var playable = episodes
            .Where(x => x.HasMedia)
            .Select(x => new EpisodeOrderKey(x.Id, x.SeasonNumber, x.Number))
            .ToArray();
        var localSeasons = playable
            .Where(x => x.SeasonNumber > 0)
            .Select(x => x.SeasonNumber)
            .Distinct()
            .Order()
            .ToArray();

        // Units the library knows, one per number: the regular episodes, or the specials of a title
        // that has only those. A unit with a file is playable, one without is missing.
        var knownUnits = episodes.Any(x => x.SeasonNumber > 0)
            ? episodes.Where(x => x.SeasonNumber > 0)
            : episodes;
        var unitHasMedia = knownUnits
            .GroupBy(x => (x.SeasonNumber, x.Number))
            .Select(group => group.Any(x => x.HasMedia))
            .ToArray();
        var playableUnits = unitHasMedia.Count(hasMedia => hasMedia);
        var missingUnits = unitHasMedia.Length - playableUnits;
        if (string.Equals(anime?.Status, "FINISHED", StringComparison.OrdinalIgnoreCase)
            && localSeasons.Length <= 1
            && anime?.ProviderEpisodeCount is int providerCount
            && providerCount > unitHasMedia.Length)
        {
            // A finished title whose provider lists more episodes than the library holds.
            missingUnits += providerCount - unitHasMedia.Length;
        }

        var lastWatched = episodes
            .Select(x => context.EpisodeProgress.TryGetValue(x.Id, out var row)
                && (row.IsCompleted || row.PositionMs >= EpisodeProgressService.MinimumResumeMs)
                    ? (DateTime?)row.UpdatedAt
                    : null)
            .Max();

        var href = anime is null ? LibraryBrowse.DetailHref(work.Technical, work.Id) : LibraryBrowse.DetailHref(WorkMediaType.Anime, anime.AnimeId);
        context.CardMetadata.TryGetValue(work.Id, out var metadata);
        var poster = anime is null ? metadata?.PosterUrl : AnimeArtworkStore.ResolvePosterUrl(anime.AnimeId, anime.CoverImageUrl);
        var fanart = anime is null ? metadata?.BackdropUrl : AnimeArtworkStore.ResolveFanartUrl(anime.AnimeId, anime.BannerImageUrl);
        var card = new MediaBannerCardData(
            anime is null && work.MediaType != WorkMediaType.Anime ? MediaBannerKind.Series : MediaBannerKind.Anime,
            anime?.Title ?? metadata?.Title ?? work.Title,
            href,
            fanart ?? poster,
            anime?.Status,
            anime?.Year ?? work.Year,
            anime is null ? AverageScore(metadata) : anime.AverageScore,
            OrderLanguages(context.Languages[(work.Id, MediaTrackKind.Audio)]),
            OrderLanguages(context.Languages[(work.Id, MediaTrackKind.Subtitle)]),
            localSeasons.Length > 0 ? localSeasons.Length : null,
            BuildProgress(work, anime, href, episodes, playable, localSeasons, context),
            new MediaAvailabilityFacts(InLibrary: true, HasPlayableContent: playable.Length > 0, Request: context.RequestByWork.TryGetValue(work.Id, out var requestStatus) ? requestStatus : null));

        return new LibraryCardEntry(card, poster ?? fanart, anime?.Format, playableUnits, missingUnits, anime?.CreatedAt ?? work.CreatedAt, lastWatched, work.Id, work.MediaType, null, null);
    }

    private static LibraryCardEntry BuildMovie(WorkRow work, ReadContext context)
    {
        var files = context.MovieFiles[work.Id].ToArray();
        context.MovieProgress.TryGetValue(work.Id, out var row);
        var meaningful = row is not null && (row.IsCompleted || row.PositionMs >= VideoProgressService.MinimumResumeMs);
        var href = LibraryBrowse.DetailHref(work.Technical, work.Id);

        var state = row is { IsCompleted: true }
            ? MediaBannerProgressState.Completed
            : meaningful ? MediaBannerProgressState.InProgress : MediaBannerProgressState.NotStarted;
        MediaBannerProgress? progress = files.Length == 0
            ? null
            : new MediaBannerProgress(state, MediaBannerUnit.Episode, 1, href, Percent: row?.Percent);

        int? runtimeMinutes = files.Max(x => x.DurationSeconds) is { } seconds and > 0 ? (int)Math.Round(seconds / 60d) : null;
        int? remainingMinutes = state == MediaBannerProgressState.InProgress && row!.DurationMs is { } durationMs && durationMs > row.PositionMs
            ? (int)Math.Ceiling((durationMs - row.PositionMs) / 60000d)
            : null;

        context.CardMetadata.TryGetValue(work.Id, out var metadata);
        var card = new MediaBannerCardData(
            work.MediaType == WorkMediaType.Anime ? MediaBannerKind.Anime : MediaBannerKind.Movie,
            metadata?.Title ?? work.Title,
            href,
            BackdropUrl: metadata?.BackdropUrl ?? metadata?.PosterUrl,
            Year: work.Year,
            AverageScore: AverageScore(metadata),
            AudioLanguages: OrderLanguages(context.Languages[(work.Id, MediaTrackKind.Audio)]),
            SubtitleLanguages: OrderLanguages(context.Languages[(work.Id, MediaTrackKind.Subtitle)]),
            Progress: progress,
            Availability: new MediaAvailabilityFacts(InLibrary: true, HasPlayableContent: files.Length > 0, Request: context.RequestByWork.TryGetValue(work.Id, out var requestStatus) ? requestStatus : null));

        var poster = metadata?.PosterUrl ?? metadata?.BackdropUrl;
        return new LibraryCardEntry(card, poster, null, files.Length > 0 ? 1 : 0, 0, work.CreatedAt, meaningful ? row!.UpdatedAt : null, work.Id, work.MediaType, runtimeMinutes, remainingMinutes);
    }

    // Cards show provider scores on the 0-100 scale of AniList; persisted Work ratings are 0-10.
    private static int? AverageScore(WorkCardMetadata? metadata) => metadata?.Rating is { } rating ? (int)Math.Round(rating * 10, MidpointRounding.AwayFromZero) : null;

    private static MediaBannerProgress? BuildProgress(WorkRow work, AnimeRow? anime, string href, IReadOnlyList<UnitRow> episodes, IReadOnlyList<EpisodeOrderKey> playable, IReadOnlyList<int> localSeasons, ReadContext context)
    {
        var units = episodes.Select(x => new SeriesUnit(x.Id, x.SeasonNumber, x.Number, x.HasMedia, x.AiredAt is null || x.AiredAt <= context.NowUtc)).ToArray();
        if (playable.Count == 0 || NextRequiredEpisode.Resolve(units, context.EpisodeProgress) is not { } resolved)
        {
            return null;
        }

        // Regular episodes known to the library (with or without a file yet), one per number.
        var regular = episodes
            .Where(x => x.SeasonNumber > 0)
            .GroupBy(x => (x.SeasonNumber, x.Number))
            .Select(x => x.ToArray())
            .ToArray();
        var multiSeason = localSeasons.Count > 1;

        int? total = null;
        if (!multiSeason && localSeasons.Count == 1)
        {
            var season = localSeasons[0];
            var localMax = regular.Where(x => x[0].SeasonNumber == season).Max(x => x[0].Number);
            total = season == 1 && anime?.ProviderEpisodeCount is int providerCount
                ? Math.Max(providerCount, localMax)
                : localMax;
        }

        var unitCount = total ?? regular.Length;
        var watched = regular.Count(group => group.Any(x =>
            context.EpisodeProgress.TryGetValue(x.Id, out var row) && row.IsCompleted));
        int? percent = unitCount > 0
            ? Math.Clamp((int)Math.Round(watched * 100d / unitCount), 0, 100)
            : null;

        var next = resolved.Unit;
        var special = next.SeasonNumber <= 0;
        var nextUrl = next.HasMedia && context.LegacyEpisodeIds.TryGetValue((work.Id, next.SeasonNumber, next.Number), out var legacyEpisodeId)
            ? $"/Library/Episode/{legacyEpisodeId}"
            : href;
        return new MediaBannerProgress(resolved.State, MediaBannerUnit.Episode, next.Number, nextUrl, special ? null : total, multiSeason && !special ? next.SeasonNumber : null, percent);
    }

    // Most common first; ties keep the files' own track order (sidecars after embedded tracks).
    private static IReadOnlyList<string> OrderLanguages(IEnumerable<LanguageRow> rows) =>
    [
        .. rows
            .Select(x => (Code: PlaybackLanguages.Normalize(x.Language), x.Count, x.FirstStreamIndex))
            .Where(x => x.Code is not null && x.Code != PlaybackLanguages.SubtitlesOff)
            .GroupBy(x => x.Code!, StringComparer.Ordinal)
            .OrderByDescending(x => x.Sum(row => row.Count))
            .ThenBy(x => x.Min(row => row.FirstStreamIndex))
            .ThenBy(x => x.Key, StringComparer.Ordinal)
            .Select(x => x.Key)
    ];

    // MediaType is how the title is presented (Anime while the Anime section is shown), Technical what it is (Movie or Series).
    private sealed record WorkRow(Guid Id, WorkMediaType MediaType, WorkMediaType Technical, string Title, int? Year, DateTime CreatedAt);

    private sealed record AnimeRow(
        Guid WorkId,
        Guid AnimeId,
        string Title,
        string? Status,
        int? Year,
        int? AverageScore,
        int? ProviderEpisodeCount,
        string? BannerImageUrl,
        string? CoverImageUrl,
        string? Provider,
        string? ExternalId,
        string? Format,
        DateTime CreatedAt);

    private sealed record UnitRow(Guid Id, Guid WorkId, int SeasonNumber, int Number, bool HasMedia, DateTime? AiredAt);

    private sealed record MovieFileRow(Guid WorkId, double? DurationSeconds);

    private sealed record LanguageRow(Guid WorkId, MediaTrackKind Kind, string Language, int Count, int FirstStreamIndex);

    private sealed record ReadContext(
        ILookup<Guid, UnitRow> Units,
        IReadOnlyDictionary<(Guid WorkId, int SeasonNumber, int Number), Guid> LegacyEpisodeIds,
        IReadOnlyDictionary<Guid, EpisodeProgressState> EpisodeProgress,
        IReadOnlyDictionary<Guid, MediaProgressSnapshot> MovieProgress,
        ILookup<Guid, MovieFileRow> MovieFiles,
        ILookup<(Guid WorkId, MediaTrackKind Kind), LanguageRow> Languages,
        IReadOnlyDictionary<Guid, AnimeRow> Anime,
        IReadOnlyDictionary<Guid, AcquisitionRequestStatus> RequestByWork,
        IReadOnlyDictionary<Guid, WorkCardMetadata> CardMetadata,
        DateTime NowUtc);
}

/// <summary>The canonical progress facts one media card needs for a single episode.</summary>
public sealed record EpisodeProgressState(
    Guid EpisodeId,
    long PositionMs,
    bool IsCompleted,
    DateTime UpdatedAt);
