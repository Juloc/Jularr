using Jularr.Web.Data;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Discovery;
using Jularr.Web.Features.Franchises;
using Jularr.Web.Features.InstantPlay;
using Jularr.Web.Features.MediaCore;
using Jularr.Web.Features.Playback;
using Jularr.Web.Features.Progress;
using Jularr.Web.Ui;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Web.Features.Library;

/// <summary>One playable file of a Movie as the consumer sees it: quality and languages, never its name or location.</summary>
public sealed record VideoDetailVersion(int? Width, int? Height, string? DynamicRange, int? RuntimeMinutes, IReadOnlySet<string> Audio, IReadOnlySet<string> Subtitles)
{
    public bool IsUltraHd => Width is >= 3200 || Height is >= 2000;

    public bool IsDolbyVision => string.Equals(DynamicRange, "Dolby Vision", StringComparison.OrdinalIgnoreCase);

    public bool IsHdr => !IsDolbyVision && !string.IsNullOrWhiteSpace(DynamicRange) && !string.Equals(DynamicRange, "SDR", StringComparison.OrdinalIgnoreCase);
}

/// <summary>One episode of a Series with its file facts, the profile's progress and where it stands for that profile.</summary>
public sealed record VideoDetailEpisode(
    Guid Id,
    int SeasonNumber,
    int Number,
    string? Title,
    bool HasFile,
    int? RuntimeMinutes,
    IReadOnlySet<string> Audio,
    IReadOnlySet<string> Subtitles,
    AnimeEpisodeAvailability Availability,
    bool IsWatched,
    int? ResumePercent);

/// <summary>A canonical relation of the Work. <see cref="Href"/> is null when the related Work has no consumer page yet.</summary>
public sealed record VideoDetailRelated(Guid WorkId, WorkMediaType MediaType, string Title, int? Year, string GroupKey, string? Href);

/// <summary>What the shared Request flow needs to ask for this Work: the provider identity the dialog posts, and the open request if one exists.</summary>
public sealed record VideoDetailRequest(MediaAcquisitionKind Kind, string Category, string Provider, string ExternalId, AcquisitionRequest? Open);

/// <summary>
/// The profile-scoped read model of one Movie or Series Work, built from canonical Work, Asset, File, Track and progress rows.
/// <see cref="Playback"/> carries what <see cref="PrimaryActionResolver"/> needs to resolve the primary action under the capability policy.
/// </summary>
public sealed record VideoDetail(
    Guid WorkId,
    WorkMediaType MediaType,
    string Title,
    string? NativeTitle,
    IReadOnlyList<string> AlsoKnownAs,
    int? Year,
    LibraryLanguagePreference Preference,
    IReadOnlyList<VideoDetailVersion> Versions,
    MediaProgressSnapshot? MovieProgress,
    IReadOnlyList<VideoDetailEpisode> Episodes,
    PlaybackFacts Playback,
    IReadOnlyList<VideoDetailRelated> Related,
    VideoDetailRequest? Request)
{
    /// <summary>The languages of every version (Movie) or episode (Series), computed once.</summary>
    public IReadOnlySet<string> Audio { get; } = (MediaType == WorkMediaType.Movie ? Versions.SelectMany(x => x.Audio) : Episodes.SelectMany(x => x.Audio)).ToHashSet(StringComparer.Ordinal);

    public IReadOnlySet<string> Subtitles { get; } = (MediaType == WorkMediaType.Movie ? Versions.SelectMany(x => x.Subtitles) : Episodes.SelectMany(x => x.Subtitles)).ToHashSet(StringComparer.Ordinal);

    public int? MovieRuntimeMinutes => Versions.Select(x => x.RuntimeMinutes).Max();
}

/// <summary>
/// The one read model behind the Movie and Series detail pages. A fixed number of set-based queries, whatever the
/// episode count: Work, titles, provider identity, files, tracks, episodes, progress, relations and the open request.
/// Request state per episode comes from the shared request payload through <see cref="VideoRequestSelection"/>, so
/// the page and the acquisition executor agree on what a request covers.
/// </summary>
public sealed class VideoDetailQuery(AppDbContext db, AcquisitionAccessStore requests, VideoProgressService progress, TimeProvider clock)
{
    private static readonly List<string> GroupOrder = [.. FranchiseLabels.RelationGroupOrder];

    /// <summary>The detail of one Work, or null when the Work does not exist or is not of <paramref name="mediaType"/>.</summary>
    /// <param name="visibleMediaTypes">The media types the profile may browse; related Works of other types are not shown.</param>
    public async Task<VideoDetail?> GetAsync(string profileId, Guid workId, WorkMediaType mediaType, IReadOnlyCollection<WorkMediaType> visibleMediaTypes, CancellationToken cancellationToken)
    {
        var work = await db.Works.AsNoTracking().Where(x => x.Id == workId && x.MediaType == mediaType).Select(x => new { x.CanonicalTitle, x.Year }).SingleOrDefaultAsync(cancellationToken);
        if (work is null)
        {
            return null;
        }

        var titles = await db.WorkTitles.AsNoTracking().Where(x => x.WorkId == workId).Select(x => new { x.TitleType, x.Value }).ToListAsync(cancellationToken);
        var tmdbId = await db.WorkExternalIdentities
            .AsNoTracking()
            .Where(x => x.WorkId == workId && x.MediaType == mediaType && x.Provider == TmdbDiscoveryProvider.ProviderKey)
            .OrderByDescending(x => x.IsPrimary)
            .Select(x => x.ExternalId)
            .FirstOrDefaultAsync(cancellationToken);

        var files = await (
            from asset in db.MediaAssets.AsNoTracking()
            join file in db.StoredFiles.AsNoTracking() on (Guid?)asset.Id equals file.MediaAssetId
            join analysis in db.MediaTechnicalAnalyses.AsNoTracking().Where(x => x.Status == MediaAnalysisStatus.Succeeded) on file.Id equals analysis.MediaFileId into analyses
            from analysis in analyses.DefaultIfEmpty()
            where asset.WorkId == workId && asset.Kind == MediaAssetKind.Video
            select new FileRow(asset.WorkEpisodeId, file.Id, analysis.DurationSeconds, analysis.Width, analysis.Height, analysis.DynamicRange))
            .ToListAsync(cancellationToken);
        var tracks = await (
            from track in db.MediaTracks.AsNoTracking()
            join file in db.StoredFiles.AsNoTracking() on track.MediaFileId equals file.Id
            join asset in db.MediaAssets.AsNoTracking() on file.MediaAssetId equals (Guid?)asset.Id
            where asset.WorkId == workId && asset.Kind == MediaAssetKind.Video && track.Language != null && track.Kind != MediaTrackKind.Video
            select new TrackRow(asset.WorkEpisodeId, file.Id, track.Kind, track.Language!))
            .ToListAsync(cancellationToken);
        var preference = await LoadPreferenceAsync(profileId, cancellationToken);
        var snapshots = await progress.ListAsync(profileId, [workId], cancellationToken);
        var open = tmdbId is null ? null : await requests.FindOpenAsync(VideoWorkLinks.AcquisitionKind(mediaType), TmdbDiscoveryProvider.ProviderKey, tmdbId, cancellationToken);
        var related = await LoadRelatedAsync(workId, visibleMediaTypes, cancellationToken);

        IReadOnlyList<VideoDetailVersion> versions = [];
        MediaProgressSnapshot? movieProgress = null;
        IReadOnlyList<VideoDetailEpisode> episodes = [];
        PlaybackFacts playback;
        if (mediaType == WorkMediaType.Movie)
        {
            var movieFiles = files.Where(x => x.WorkEpisodeId is null).OrderByDescending(x => x.Height ?? 0).ThenBy(x => x.FileId);
            versions = [.. movieFiles.Select(file => ToVersion(file, tracks.Where(x => x.FileId == file.FileId)))];
            movieProgress = snapshots.FirstOrDefault(x => x.WorkEpisodeId is null);
            playback = new MoviePlaybackFacts(workId, tmdbId is not null, open is null ? null : OpenRequestFacts.ForMovie(open), versions.Count > 0, movieProgress);
        }
        else
        {
            (episodes, playback) = await LoadEpisodesAsync(workId, tmdbId is not null, files, tracks, snapshots, open, preference, cancellationToken);
        }

        var nativeTitle = titles.FirstOrDefault(x => x.TitleType == WorkTitleType.Native)?.Value;
        var shownTitles = new[] { work.CanonicalTitle, nativeTitle };
        var alsoKnownAs = titles
            .Where(x => x.TitleType is WorkTitleType.Original or WorkTitleType.English or WorkTitleType.Alternative or WorkTitleType.Localized or WorkTitleType.Synonym)
            .Select(x => x.Value.Trim())
            .Where(x => x.Length > 0 && !shownTitles.Contains(x, StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(6)
            .ToArray();

        return new VideoDetail(
            workId,
            mediaType,
            work.CanonicalTitle,
            nativeTitle,
            alsoKnownAs,
            work.Year,
            preference,
            versions,
            movieProgress,
            episodes,
            playback,
            related,
            tmdbId is null ? null : new VideoDetailRequest(VideoWorkLinks.AcquisitionKind(mediaType), mediaType == WorkMediaType.Movie ? "movie" : "tv", TmdbDiscoveryProvider.ProviderKey, tmdbId, open));
    }

    private async Task<LibraryLanguagePreference> LoadPreferenceAsync(string profileId, CancellationToken cancellationToken)
    {
        var row = await db.ProfilePlaybackPreferences.AsNoTracking().Where(x => x.ProfileId == profileId).Select(x => new { x.PreferredAudioLanguage, x.PreferredSubtitleLanguage }).SingleOrDefaultAsync(cancellationToken);
        return row is null ? LibraryLanguagePreference.None : LibraryLanguagePreference.From(row.PreferredAudioLanguage, row.PreferredSubtitleLanguage);
    }

    private async Task<IReadOnlyList<VideoDetailRelated>> LoadRelatedAsync(Guid workId, IReadOnlyCollection<WorkMediaType> visibleMediaTypes, CancellationToken cancellationToken)
    {
        var rows = await (
            from relation in db.WorkRelations.AsNoTracking()
            where relation.FromWorkId == workId || relation.ToWorkId == workId
            join other in db.Works.AsNoTracking() on (relation.FromWorkId == workId ? relation.ToWorkId : relation.FromWorkId) equals other.Id
            join link in db.WorkSourceLinks.AsNoTracking().Where(x => x.SourceKind == WorkSourceKind.Anime) on other.Id equals link.WorkId into animeLinks
            from animeLink in animeLinks.DefaultIfEmpty()
            select new RelatedRow(other.Id, other.MediaType, other.CanonicalTitle, other.Year, relation.RelationType, relation.FromWorkId == workId, (Guid?)animeLink.SourceId))
            .ToListAsync(cancellationToken);

        // Both directions of an edge are stored; an incoming edge reads from this Work's side through the inverse relation, and when
        // a Work is related twice the edge this Work declared wins, so the group never depends on row order.
        return
        [
            .. rows
                .Where(x => visibleMediaTypes.Contains(x.MediaType))
                .Select(x => x with { Relation = x.Outgoing ? x.Relation : WorkRelationTypes.Inverse(x.Relation) })
                .OrderByDescending(x => x.Outgoing)
                .DistinctBy(x => x.WorkId)
                .OrderBy(x => RelationOrder(x.Relation))
                .ThenBy(x => x.Year ?? int.MaxValue)
                .ThenBy(x => x.Title, StringComparer.OrdinalIgnoreCase)
                .Select(x => new VideoDetailRelated(x.WorkId, x.MediaType, x.Title, x.Year, FranchiseGroupKey(x.Relation), RelatedHref(x)))
        ];
    }

    private static string? RelatedHref(RelatedRow row) => row.MediaType switch
    {
        WorkMediaType.Movie or WorkMediaType.Series => LibraryBrowse.DetailHref(row.MediaType, row.WorkId),
        WorkMediaType.Anime => row.AnimeId is { } animeId ? LibraryBrowse.DetailHref(WorkMediaType.Anime, animeId) : null,
        _ => null
    };

    private static string FranchiseGroupKey(WorkRelationType relation) => FranchiseLabels.RelationGroupKey(WorkRelationTypes.ToStorage(relation));

    // The order of the Related / Franchise groups on every consumer page: adaptations first, the looser franchise last.
    private static int RelationOrder(WorkRelationType relation) => GroupOrder.IndexOf(FranchiseGroupKey(relation));

    private static VideoDetailVersion ToVersion(FileRow file, IEnumerable<TrackRow> tracks)
    {
        var rows = tracks.ToArray();
        var runtime = AnimeDetailView.RuntimeMinutes(file.DurationSeconds);
        return new VideoDetailVersion(file.Width, file.Height, file.DynamicRange, runtime, Languages(rows, MediaTrackKind.Audio), Languages(rows, MediaTrackKind.Subtitle));
    }

    private static IReadOnlySet<string> Languages(IEnumerable<TrackRow> tracks, MediaTrackKind kind) =>
        tracks.Where(x => x.Kind == kind).Select(x => PlaybackLanguages.Normalize(x.Language)).Where(x => x is not null && x != PlaybackLanguages.SubtitlesOff).Select(x => x!).ToHashSet(StringComparer.Ordinal);

    private async Task<(IReadOnlyList<VideoDetailEpisode> Episodes, SeriesPlaybackFacts Playback)> LoadEpisodesAsync(
        Guid workId,
        bool hasRequestIdentity,
        IReadOnlyList<FileRow> files,
        IReadOnlyList<TrackRow> tracks,
        IReadOnlyList<MediaProgressSnapshot> snapshots,
        AcquisitionRequest? open,
        LibraryLanguagePreference preference,
        CancellationToken cancellationToken)
    {
        var rows = await db.WorkEpisodes
            .AsNoTracking()
            .Where(x => x.WorkId == workId)
            .OrderBy(x => x.SeasonNumber)
            .ThenBy(x => x.EpisodeNumber)
            .Select(x => new EpisodeRow(x.Id, x.SeasonId, x.SeasonNumber, x.EpisodeNumber, x.Title, x.AiredAt))
            .ToListAsync(cancellationToken);

        var filesByEpisode = files.Where(x => x.WorkEpisodeId is not null).ToLookup(x => x.WorkEpisodeId!.Value);
        var tracksByEpisode = tracks.Where(x => x.WorkEpisodeId is not null).ToLookup(x => x.WorkEpisodeId!.Value);
        var progressByEpisode = snapshots.Where(x => x.WorkEpisodeId is not null).ToDictionary(x => x.WorkEpisodeId!.Value);
        var selection = open is null ? null : VideoRequestSelection.For(open, workId);
        var now = clock.GetUtcNow().UtcDateTime;
        var units = new List<SeriesUnit>(rows.Count);

        var episodes = rows.Select(row =>
            {
                var episodeFiles = filesByEpisode[row.Id].ToArray();
                var episodeTracks = tracksByEpisode[row.Id].ToArray();
                var audio = Languages(episodeTracks, MediaTrackKind.Audio);
                var subtitles = Languages(episodeTracks, MediaTrackKind.Subtitle);
                var facts = new AnimeEpisodeMediaFacts(episodeFiles.Length > 0, AnimeDetailView.RuntimeMinutes(episodeFiles.Max(x => x.DurationSeconds)), audio, subtitles);

                // A request covers the title as a whole; only the episodes its scope includes show its state, and while
                // it is downloading only the episode the executor is on is "Downloading" (the rest wait as "Requested").
                var requestStatus = selection is not null && selection.Includes(row.Id, row.SeasonId, row.AiredAt) ? open!.Status : (AcquisitionRequestStatus?)null;
                if (requestStatus is AcquisitionRequestStatus.Downloading or AcquisitionRequestStatus.Importing && selection!.Payload.ActiveWorkEpisodeId is { } active && active != row.Id)
                {
                    requestStatus = AcquisitionRequestStatus.Approved;
                }

                units.Add(new SeriesUnit(row.Id, row.SeasonNumber, row.Number, facts.HasFile, row.AiredAt is null || row.AiredAt <= now));
                progressByEpisode.TryGetValue(row.Id, out var episodeProgress);
                return new VideoDetailEpisode(
                    row.Id,
                    row.SeasonNumber,
                    row.Number,
                    row.Title,
                    facts.HasFile,
                    facts.RuntimeMinutes,
                    audio,
                    subtitles,
                    AnimeDetailView.Availability(facts, requestStatus, preference.Audio, preference.Subtitle),
                    episodeProgress is { IsCompleted: true },
                    episodeProgress is { IsCompleted: false, PositionMs: >= VideoProgressService.MinimumResumeMs } ? episodeProgress.Percent : null);
            })
            .ToList();

        var progress = snapshots
            .Where(x => x.WorkEpisodeId is not null && x.UpdatedAt is not null)
            .ToDictionary(x => x.WorkEpisodeId!.Value, x => new EpisodeProgressState(x.WorkEpisodeId!.Value, x.PositionMs, x.IsCompleted, x.UpdatedAt!.Value));
        var openFacts = open is null ? null : OpenRequestFacts.ForSeries(open, selection!, rows.Select(x => (x.Id, x.SeasonId, x.AiredAt)));
        return (episodes, new SeriesPlaybackFacts(workId, hasRequestIdentity, openFacts, units, progress));
    }

    private sealed record FileRow(Guid? WorkEpisodeId, Guid FileId, double? DurationSeconds, int? Width, int? Height, string? DynamicRange);

    private sealed record TrackRow(Guid? WorkEpisodeId, Guid FileId, MediaTrackKind Kind, string Language);

    private sealed record EpisodeRow(Guid Id, Guid? SeasonId, int SeasonNumber, int Number, string? Title, DateTime? AiredAt);

    private sealed record RelatedRow(Guid WorkId, WorkMediaType MediaType, string Title, int? Year, WorkRelationType Relation, bool Outgoing, Guid? AnimeId);
}
