using Jularr.Web.Data;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.Monitoring;
using Jularr.Web.Features.Acquisition.Quality;
using Jularr.Web.Features.Instance;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Web.Features.Library;

/// <summary>One local file of a Movie or an episode, with the facts the Admin media page shows. <paramref name="Location"/> is the library root and the folder below it.</summary>
public sealed record AdminVideoFile(Guid Id, string Name, long SizeBytes, string? Quality, IReadOnlyList<string> Audio, IReadOnlyList<string> Subtitles, string Location, string? Container, string? VideoCodec);

/// <summary>One version of a Movie (a release such as "1080p WEB-DL" or "4K HDR Remux") with the local files that make it up.</summary>
public sealed record AdminVideoVersion(Guid Id, string? Label, string? ReleaseGroup, IReadOnlyList<AdminVideoFile> Files)
{
    public long SizeBytes => Files.Sum(file => file.SizeBytes);

    public string? Quality => Files.Select(file => file.Quality).Aggregate((string?)null, AdminMediaDetailView.BestQuality);

    public IReadOnlyList<string> Audio => [.. Files.SelectMany(file => file.Audio).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)];

    public IReadOnlyList<string> Subtitles => [.. Files.SelectMany(file => file.Subtitles).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)];
}

public sealed record AdminVideoEpisode(
    Guid Id,
    Guid? SeasonId,
    int Season,
    int Number,
    string? Title,
    DateTime? AiredAt,
    bool IsUpcoming,
    bool Monitored,
    AdminMediaState State,
    IReadOnlyList<AdminVideoFile> Files)
{
    public long SizeBytes => Files.Sum(file => file.SizeBytes);
}

public sealed record AdminVideoSeason(Guid? Id, int Number, IReadOnlyList<AdminVideoEpisode> Episodes)
{
    public int Available => Episodes.Count(episode => episode.State == AdminMediaState.Available);

    public long SizeBytes => Episodes.Sum(episode => episode.SizeBytes);

    /// <summary>The audio languages of the season with the number of episodes that have each, most covered first.</summary>
    public IReadOnlyList<(string Language, int Episodes)> AudioCoverage =>
    [
        .. Episodes
            .SelectMany(episode => episode.Files.SelectMany(file => file.Audio).Distinct(StringComparer.OrdinalIgnoreCase))
            .GroupBy(code => code, StringComparer.OrdinalIgnoreCase)
            .Select(group => (group.Key, group.Count()))
            .OrderByDescending(item => item.Item2)
            .ThenBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
    ];

    public AdminMediaMonitoring Monitoring => Episodes.Count(episode => episode.Monitored) switch
    {
        0 => AdminMediaMonitoring.Off,
        var count when count == Episodes.Count => AdminMediaMonitoring.On,
        _ => AdminMediaMonitoring.Partial
    };
}

/// <summary>What Admin can do about acquiring the Work, from the shared request path (no Movie/TV-specific rules).</summary>
public sealed record AdminVideoAcquisition(
    bool CanAcquire,
    bool CanSearchNow,
    AcquisitionRequest? OpenRequest,
    string ProfileId,
    string ProfileName,
    string? AssignedProfileId,
    IReadOnlyList<(string Id, string Name)> Profiles)
{
    /// <summary>Manual Search opens for an approved request that is monitored: the states the shared Manual Search can search.</summary>
    public bool CanManualSearch => CanSearchNow && OpenRequest is not null;
}

/// <summary>The scope of the TV monitoring as the Request dialog names it; <see cref="VideoMonitoringService.OffScope"/> when nothing is wanted.</summary>
public sealed record AdminVideoMonitoring(bool Monitored, string Scope, bool MonitorFuture);

public sealed record AdminVideoMediaDetail(
    MediaAcquisitionKind Kind,
    Guid WorkId,
    string Title,
    int? Year,
    AdminVideoMonitoring Monitoring,
    AdminVideoAcquisition Acquisition,
    IReadOnlyList<AdminVideoVersion> Versions,
    IReadOnlyList<AdminVideoSeason> Seasons)
{
    public IEnumerable<AdminVideoEpisode> Episodes => Seasons.SelectMany(season => season.Episodes);

    /// <summary>
    /// The one monitoring state of the medium for its header control: off when nothing is monitored, partial when a Series monitors only
    /// some of its episodes (a selection, or future episodes only) and on otherwise.
    /// </summary>
    public AdminMediaMonitoring MediumMonitoring =>
        !Monitoring.Monitored ? AdminMediaMonitoring.Off
        : Kind == MediaAcquisitionKind.Tv && Episodes.Any(episode => !episode.Monitored) ? AdminMediaMonitoring.Partial
        : AdminMediaMonitoring.On;

    public IEnumerable<AdminVideoFile> Files => Kind == MediaAcquisitionKind.Movie ? Versions.SelectMany(version => version.Files) : Episodes.SelectMany(episode => episode.Files);

    public long SizeBytes => Files.Sum(file => file.SizeBytes);

    public int Available => Kind == MediaAcquisitionKind.Movie ? (Versions.Count > 0 ? 1 : 0) : Episodes.Count(episode => episode.State == AdminMediaState.Available);

    public int Total => Kind == MediaAcquisitionKind.Movie ? 1 : Episodes.Count();

    public IReadOnlyList<string> AudioLanguages =>
        [.. Files.SelectMany(file => file.Audio).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

    public IReadOnlyList<string> SubtitleLanguages =>
        [.. Files.SelectMany(file => file.Subtitles).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
}

/// <summary>
/// Reads the Admin media page of one Movie or Series Work from the canonical Work, season, episode, asset and file rows, the
/// monitoring state and the open request. A fixed number of set-based queries whatever the episode count; no file is probed.
/// </summary>
public sealed class AdminVideoMediaService(
    AppDbContext db,
    VideoMonitoringService monitoring,
    QualityProfileStore profiles,
    IEnumerable<IAcquisitionRequestExecutor> executors,
    TimeProvider clock,
    IInstanceModuleService? instanceModules = null)
{
    /// <summary>The detail of one Work, or null when it does not exist or is not a Work of <paramref name="kind"/>.</summary>
    public async Task<AdminVideoMediaDetail?> LoadAsync(MediaAcquisitionKind kind, Guid workId, CancellationToken cancellationToken)
    {
        var type = VideoWorkLinks.WorkType(kind);
        var work = await db.Works.AsNoTracking().Where(x => x.Id == workId && x.MediaType == type).Select(x => new { x.CanonicalTitle, x.Year }).SingleOrDefaultAsync(cancellationToken);
        if (work is null)
        {
            return null;
        }

        var files = await (
                from asset in db.MediaAssets.AsNoTracking()
                join file in db.StoredFiles.AsNoTracking() on (Guid?)asset.Id equals file.MediaAssetId
                join analysis in db.MediaTechnicalAnalyses.AsNoTracking().Where(x => x.Status == MediaAnalysisStatus.Succeeded) on file.Id equals analysis.MediaFileId into analyses
                from analysis in analyses.DefaultIfEmpty()
                join version in db.WorkVersions.AsNoTracking() on asset.WorkVersionId equals version.Id
                join root in db.LibraryRoots.AsNoTracking() on file.LibraryRootId equals root.Id
                where asset.WorkId == workId && asset.Kind == MediaAssetKind.Video
                orderby file.Path
                select new
                {
                    asset.WorkEpisodeId,
                    asset.WorkVersionId,
                    VersionQuality = version.Quality,
                    version.ReleaseGroup,
                    file.Id,
                    file.Path,
                    file.SizeBytes,
                    RootName = root.Name,
                    RootPath = root.Path,
                    analysis.Width,
                    analysis.Height,
                    analysis.DynamicRange,
                    analysis.Container,
                    analysis.VideoCodec
                })
            .ToListAsync(cancellationToken);
        var tracks = await (
                from track in db.MediaTracks.AsNoTracking()
                join file in db.StoredFiles.AsNoTracking() on track.MediaFileId equals file.Id
                join asset in db.MediaAssets.AsNoTracking() on file.MediaAssetId equals (Guid?)asset.Id
                where asset.WorkId == workId && asset.Kind == MediaAssetKind.Video && track.Language != null && track.Kind != MediaTrackKind.Video
                select new TrackRow(file.Id, track.Kind, track.Language!))
            .ToListAsync(cancellationToken);
        var languages = tracks.ToLookup(track => track.FileId);
        var fileRows = files.Select(row => (row.WorkEpisodeId, row.WorkVersionId, row.VersionQuality, row.ReleaseGroup, File: new AdminVideoFile(
                row.Id,
                AdminMediaDetailView.FileName(row.Path),
                row.SizeBytes,
                AdminMediaDetailView.QualityLabel(row.Width, row.Height, row.DynamicRange),
                Languages(languages[row.Id], MediaTrackKind.Audio),
                Languages(languages[row.Id], MediaTrackKind.Subtitle),
                string.Join('/', new[] { row.RootName, AdminMediaDetailView.Folder(row.RootPath, row.Path) }.Where(part => part.Length > 0)),
                row.Container,
                row.VideoCodec)))
            .ToList();

        var open = await monitoring.FindOpenRequestAsync(kind, workId, cancellationToken);
        var payload = open is null ? null : VideoRequestPayload.Of(open, workId, work.CanonicalTitle, work.Year);
        var profile = await profiles.ResolveAsync(kind, workId, cancellationToken);
        var profileState = await profiles.LoadAsync(cancellationToken);
        var acquisition = new AdminVideoAcquisition(
            await CanAcquireAsync(kind, workId, cancellationToken),
            open?.Status == AcquisitionRequestStatus.Approved && payload!.Monitored,
            open,
            profile.Id,
            profile.Name,
            profileState.WorkAssignments.GetValueOrDefault(workId.ToString("D")),
            [.. profileState.Profiles.Select(item => (item.Id, item.Name))]);

        if (kind == MediaAcquisitionKind.Movie)
        {
            return new AdminVideoMediaDetail(
                kind,
                workId,
                work.CanonicalTitle,
                work.Year,
                new AdminVideoMonitoring(payload?.Monitored == true, "", false),
                acquisition,
                [
                    .. fileRows
                        .GroupBy(row => row.WorkVersionId)
                        .Select(group => new AdminVideoVersion(group.Key, group.First().VersionQuality, group.First().ReleaseGroup, [.. group.Select(row => row.File)]))
                        .OrderByDescending(version => version.SizeBytes)
                ],
                []);
        }

        return new AdminVideoMediaDetail(
            kind,
            workId,
            work.CanonicalTitle,
            work.Year,
            TvMonitoring(payload),
            acquisition,
            [],
            await LoadSeasonsAsync(workId, open, fileRows.Where(row => row.WorkEpisodeId is not null).ToLookup(row => row.WorkEpisodeId!.Value, row => row.File), cancellationToken));
    }

    private async Task<IReadOnlyList<AdminVideoSeason>> LoadSeasonsAsync(
        Guid workId,
        AcquisitionRequest? open,
        ILookup<Guid, AdminVideoFile> files,
        CancellationToken cancellationToken)
    {
        var seasonRows = await db.WorkSeasons.AsNoTracking().Where(x => x.WorkId == workId).Select(x => new { x.Id, x.SeasonNumber }).ToListAsync(cancellationToken);
        var episodeRows = await db.WorkEpisodes.AsNoTracking()
            .Where(x => x.WorkId == workId)
            .OrderBy(x => x.SeasonNumber)
            .ThenBy(x => x.EpisodeNumber)
            .Select(x => new { x.Id, x.SeasonId, x.SeasonNumber, x.EpisodeNumber, x.Title, x.AiredAt })
            .ToListAsync(cancellationToken);

        var selection = open is null ? null : VideoRequestSelection.For(open, workId);
        var payload = selection?.Payload;
        var now = clock.GetUtcNow().UtcDateTime;
        var episodes = episodeRows.Select(row =>
        {
            var local = files[row.Id].ToList();
            var upcoming = row.AiredAt is { } airs && airs > now;
            return new AdminVideoEpisode(
                row.Id,
                row.SeasonId,
                row.SeasonNumber,
                row.EpisodeNumber,
                row.Title,
                row.AiredAt,
                upcoming,
                selection?.Includes(row.Id, row.SeasonId, row.AiredAt) == true,
                local.Count > 0 ? AdminMediaState.Available : ActiveState(open, payload, row.Id),
                local);
        }).ToList();

        return
        [
            .. episodes
                .GroupBy(episode => episode.Season)
                .OrderBy(group => group.Key)
                .Select(group => new AdminVideoSeason(seasonRows.FirstOrDefault(season => season.SeasonNumber == group.Key)?.Id, group.Key, [.. group]))
        ];
    }

    /// <summary>What the open request is doing about an episode that has no file: only the episode it is working on is in flight.</summary>
    private static AdminMediaState ActiveState(AcquisitionRequest? open, VideoRequestPayload? payload, Guid episodeId) =>
        open is not null && payload?.ActiveWorkEpisodeId == episodeId
            ? open.Status switch
            {
                AcquisitionRequestStatus.Searching => AdminMediaState.Searching,
                AcquisitionRequestStatus.Downloading => AdminMediaState.Downloading,
                AcquisitionRequestStatus.Importing => AdminMediaState.Importing,
                _ => AdminMediaState.Missing
            }
            : AdminMediaState.Missing;

    private static AdminVideoMonitoring TvMonitoring(VideoRequestPayload? payload)
    {
        if (payload is not { Monitored: true })
        {
            return new AdminVideoMonitoring(false, VideoMonitoringService.OffScope, false);
        }

        var scope = payload.Scope switch
        {
            VideoRequestScope.AllCurrentAndFuture => "all",
            VideoRequestScope.FutureOnly => "future",
            _ => "custom"
        };
        return new AdminVideoMonitoring(true, scope, payload.MonitorFuture);
    }

    private static List<string> Languages(IEnumerable<TrackRow> tracks, MediaTrackKind kind) =>
        [.. tracks.Where(track => track.Kind == kind).Select(track => track.Language).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)];

    private sealed record TrackRow(Guid FileId, MediaTrackKind Kind, string Language);

    /// <summary>Whether <paramref name="fileId"/> is a local video file of the Work, so an action on it cannot reach another title's file.</summary>
    public Task<bool> OwnsFileAsync(Guid workId, Guid fileId, CancellationToken cancellationToken) =>
        (
            from asset in db.MediaAssets.AsNoTracking()
            join file in db.StoredFiles.AsNoTracking() on (Guid?)asset.Id equals file.MediaAssetId
            where asset.WorkId == workId && asset.Kind == MediaAssetKind.Video && file.Id == fileId
            select file.Id
        ).AnyAsync(cancellationToken);

    /// <summary>Whether the Work can be acquired at all: its modules are on, an executor exists for the kind and a provider identity names it.</summary>
    public async Task<bool> CanAcquireAsync(MediaAcquisitionKind kind, Guid workId, CancellationToken cancellationToken)
    {
        var type = VideoWorkLinks.WorkType(kind);
        if (instanceModules is not null)
        {
            var instance = await instanceModules.GetAsync(cancellationToken);
            if (!instance.IsEnabled(InstanceModule.Acquisition) || !instance.IsEnabled(AcquisitionInstanceModules.For(kind)))
            {
                return false;
            }
        }

        return executors.Any(executor => executor.Kind == kind)
            && await db.WorkExternalIdentities.AsNoTracking().AnyAsync(identity => identity.WorkId == workId && identity.MediaType == type, cancellationToken);
    }
}
