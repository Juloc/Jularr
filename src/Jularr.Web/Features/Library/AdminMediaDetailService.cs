using Jularr.Web.Features.Monitoring;
using System.Data.Common;
using System.Text.Json;
using Jularr.Web.Data;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.History;
using Jularr.Web.Features.Acquisition.Import;
using Jularr.Web.Features.Acquisition.Monitoring;
using Jularr.Web.Features.Acquisition.Ownership;
using Jularr.Web.Features.Acquisition.Pipeline;
using Jularr.Web.Features.Acquisition.Quality;
using Jularr.Web.Features.Artwork;
using Jularr.Web.Features.Metadata;
using Jularr.Web.Features.Operations;
using Jularr.Web.Features.Playback;
using Jularr.Web.Features.Tracking;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Web.Features.Library;

/// <summary>
/// Reads everything Admin → Media detail shows for one anime from the stored rows and the monitoring,
/// quality and ownership state: episodes with their files and analysis, monitoring and acquisition state,
/// the provider match, imports, history and related operations. It never probes a file, so it stays cheap
/// when the storage is asleep or offline. Sections that can fail on their own (mapping ranges, imports,
/// history, operations) degrade instead of failing the page.
/// </summary>
public sealed class AdminMediaDetailService(
    AppDbContext db,
    AnimeMonitoringStore monitoring,
    AnimeMonitoring animeMonitoring,
    AnimeQualityProfileStore profiles,
    AcquisitionOwnershipStore ownership,
    AnimeImportStore imports,
    AniListAccountStore aniList,
    AcquisitionHistoryService history,
    AnimeEpisodeStates episodeStates)
{
    public const int HistoryLimit = 12;
    public const int OperationLimit = 6;

    /// <summary>The operations fetched before they are narrowed to this anime; the store can only search text.</summary>
    private const int OperationSearchLimit = 200;

    public async Task<AdminMediaDetail?> LoadAsync(Guid id, CancellationToken cancellationToken)
    {
        var anime = await db.Anime.AsNoTracking().SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (anime is null)
        {
            return null;
        }

        var metadata = await db.AnimeMetadata.AsNoTracking().SingleOrDefaultAsync(item => item.AnimeId == id, cancellationToken);
        var state = await monitoring.LoadAsync(cancellationToken);
        var settings = state.Anime.GetValueOrDefault(anime.Key);
        var view = await animeMonitoring.LoadAsync(anime.Key, cancellationToken);
        var states = await episodeStates.LoadAsync(anime.Key, cancellationToken);
        var episodes = await LoadEpisodesAsync(anime, states, view, cancellationToken);

        var profileState = await profiles.LoadAsync(cancellationToken);
        var profileId = profileState.ResolveProfileId(MediaAcquisitionKind.Anime, id)
            ?? AnimeQualityProfiles.DefaultAnime1080pId;
        var ownershipState = await ownership.LoadAsync(cancellationToken);
        var acquisition = new AdminMediaAcquisition(
            SonarrParallelSafety.GetMode(ownershipState, anime.Key),
            view.IsWorkMonitored,
            settings?.SearchOnAdd ?? true,
            profileId,
            profileState.Profiles.FirstOrDefault(profile => profile.Id.Equals(profileId, StringComparison.OrdinalIgnoreCase))?.Name ?? profileId,
            [.. profileState.Profiles.Select(profile => (profile.Id, profile.Name))],
            settings?.IndexerIds ?? [],
            settings?.TargetRootId,
            states.Wanted.Count);

        var (ranges, rangesFailed) = await LoadRangesAsync(id, cancellationToken);
        var mapping = new AdminMediaMapping(
            metadata?.Provider,
            metadata?.ExternalId,
            metadata?.PreferredTitle,
            ranges,
            rangesFailed);

        var activityFailed = false;
        IReadOnlyList<AdminMediaImport> importRows = [];
        IReadOnlyList<AcquisitionHistoryEntry> historyRows = [];
        IReadOnlyList<OperationSnapshot> operationRows = [];
        try
        {
            importRows = [.. (await imports.LoadAsync(cancellationToken)).Imports
                .Where(record => record.AnimeKey.Equals(anime.Key, StringComparison.OrdinalIgnoreCase)
                    && record.Status != AnimeImportStatus.Imported
                    && record.Status != AnimeImportStatus.Dismissed)
                .OrderByDescending(record => record.UpdatedAtUtc)
                .Select(record => new AdminMediaImport(record.Status, record.Message, record.UpdatedAtUtc, record.Files.Length))];
            historyRows = await history.ForAnimeAsync(id, HistoryLimit, cancellationToken);
            operationRows = await LoadOperationsAsync(anime, cancellationToken);
        }
        catch (Exception exception) when (IsReadFailure(exception))
        {
            activityFailed = true;
        }

        return new AdminMediaDetail(
            id,
            anime.Key,
            metadata?.PreferredTitle ?? anime.Title,
            metadata,
            AnimeArtworkStore.ResolvePosterUrl(id, metadata?.CoverImageUrl),
            episodes,
            acquisition,
            mapping,
            importRows,
            historyRows,
            operationRows,
            activityFailed);
    }

    private async Task<IReadOnlyList<AdminMediaEpisode>> LoadEpisodesAsync(
        Anime anime,
        AnimeEpisodeStateMap states,
        WorkMonitoringView view,
        CancellationToken cancellationToken)
    {
        var id = anime.Id;
        var episodeRows = await db.Episodes
            .AsNoTracking()
            .Where(item => item.AnimeId == id)
            .Select(item => new { item.Id, item.SeasonNumber, item.Number, item.Title })
            .ToListAsync(cancellationToken);

        var fileRows = await (
            from media in db.MediaFiles.AsNoTracking()
            join episode in db.Episodes.AsNoTracking() on media.EpisodeId equals episode.Id
            join root in db.LibraryRoots.AsNoTracking() on media.LibraryRootId equals root.Id into roots
            from root in roots.DefaultIfEmpty()
            join analysis in db.MediaAnalyses.AsNoTracking() on media.Id equals analysis.MediaFileId into analyses
            from analysis in analyses.DefaultIfEmpty()
            where episode.AnimeId == id
            orderby media.Path
            select new
            {
                media.Id,
                media.EpisodeId,
                media.Path,
                media.SizeBytes,
                media.DiscoveredAt,
                RootName = root == null ? null : root.Name,
                RootPath = root == null ? null : root.Path,
                Status = analysis == null ? (MediaAnalysisStatus?)null : analysis.Status,
                Diagnostic = analysis == null ? null : analysis.Diagnostic,
                Container = analysis == null ? null : analysis.Container,
                VideoCodec = analysis == null ? null : analysis.VideoCodec,
                Width = analysis == null ? null : analysis.Width,
                Height = analysis == null ? null : analysis.Height,
                DynamicRange = analysis == null ? null : analysis.DynamicRange
            })
            .ToListAsync(cancellationToken);

        var streamRows = await (
            from stream in db.MediaAnalysisStreams.AsNoTracking()
            join media in db.MediaFiles.AsNoTracking() on stream.MediaFileId equals media.Id
            join episode in db.Episodes.AsNoTracking() on media.EpisodeId equals episode.Id
            where episode.AnimeId == id
            orderby stream.StreamIndex
            select new { stream.MediaFileId, stream.Kind, stream.Language, stream.Codec, stream.Channels, stream.IsDefault, stream.IsForced })
            .ToListAsync(cancellationToken);
        var streamsByFile = streamRows.ToLookup(row => row.MediaFileId);

        var sidecarRows = await (
            from track in db.SubtitleTracks.AsNoTracking()
            join episode in db.Episodes.AsNoTracking() on track.EpisodeId equals episode.Id
            where episode.AnimeId == id
            orderby track.Language
            select new { track.EpisodeId, track.Language, track.Format, track.Forced, track.Sdh })
            .ToListAsync(cancellationToken);
        var sidecarsByEpisode = sidecarRows.ToLookup(row => row.EpisodeId);

        var filesByEpisode = fileRows.ToLookup(row => row.EpisodeId);

        AdminMediaEpisode Build(int season, int number, Guid? episodeId, string title)
        {
            var files = episodeId is { } key
                ? filesByEpisode[key].Select(row => new AdminMediaFile(
                    row.Id,
                    AdminMediaDetailView.FileName(row.Path),
                    Location(row.RootName, row.RootPath, row.Path),
                    row.SizeBytes,
                    DateTime.SpecifyKind(row.DiscoveredAt, DateTimeKind.Utc),
                    row.Status,
                    row.Diagnostic,
                    row.Container,
                    string.IsNullOrWhiteSpace(row.VideoCodec) ? null : row.VideoCodec,
                    AdminMediaDetailView.QualityLabel(row.Width, row.Height, row.DynamicRange),
                    [.. streamsByFile[row.Id]
                        .Where(stream => stream.Kind == MediaStreamKind.Audio)
                        .Select(stream => new AdminMediaTrack(Language(stream.Language), stream.Codec, stream.Channels, stream.IsDefault, stream.IsForced))],
                    [.. streamsByFile[row.Id]
                        .Where(stream => stream.Kind == MediaStreamKind.Subtitle)
                        .Select(stream => new AdminMediaTrack(Language(stream.Language), stream.Codec, stream.Channels, stream.IsDefault, stream.IsForced))])).ToArray()
                : [];
            var sidecars = episodeId is { } sidecarKey
                ? sidecarsByEpisode[sidecarKey].Select(row => new AdminMediaSidecar(row.Language, row.Format, row.Forced, row.Sdh)).ToArray()
                : [];

            var unit = MonitoredUnitKey.ForEpisode(anime.Key, season, number);

            string? quality = null;
            foreach (var file in files)
            {
                quality = AdminMediaDetailView.BestQuality(quality, file.Quality);
            }

            return new AdminMediaEpisode(
                season,
                number,
                episodeId,
                title,
                AnimeMonitoring.IsUnitMonitored(view, unit),
                AdminMediaDetailView.StateOf(files.Length > 0, states.AttemptOf(unit)),
                states.WantedOf(unit)?.Reason == WantedReason.CutoffUnmet,
                states.FailuresOf(unit),
                quality,
                LanguageSet(files.SelectMany(file => file.Audio).Select(track => track.Language)),
                LanguageSet(
                    files.SelectMany(file => file.Subtitles).Select(track => track.Language)
                        .Concat(sidecars.Select(sidecar => PlaybackLanguages.Normalize(sidecar.Language)))),
                files,
                sidecars);
        }

        var rows = episodeRows
            .Select(row => Build(row.SeasonNumber, row.Number, row.Id, row.Title))
            .ToList();

        // Episodes acquisition wants but the library has no row for yet still belong on the page.
        var known = rows.Select(row => (row.Season, row.Number)).ToHashSet();
        foreach (var episode in states.Wanted)
        {
            if (known.Add((episode.Key.SeasonNumber, episode.Key.EpisodeNumber)))
            {
                rows.Add(Build(episode.Key.SeasonNumber, episode.Key.EpisodeNumber, null, ""));
            }
        }

        return
        [
            .. rows
                .OrderBy(row => row.Season == 0 ? 1 : 0)
                .ThenBy(row => row.Season)
                .ThenBy(row => row.Number)
        ];
    }

    private static string Location(string? rootName, string? rootPath, string path)
    {
        var folder = rootPath is null ? "" : AdminMediaDetailView.Folder(rootPath, path);
        return string.Join('/', new[] { rootName, folder }.Where(part => !string.IsNullOrEmpty(part)));
    }

    private static string? Language(string? value) => PlaybackLanguages.Normalize(value);

    private static string[] LanguageSet(IEnumerable<string?> languages) =>
    [
        .. languages
            .Where(language => !string.IsNullOrEmpty(language) && language != PlaybackLanguages.SubtitlesOff)
            .Select(language => language!)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
    ];

    private async Task<(IReadOnlyList<AnimeEpisodeMetadataMapping> Ranges, bool Failed)> LoadRangesAsync(
        Guid id,
        CancellationToken cancellationToken)
    {
        try
        {
            return (await aniList.LoadEpisodeMappingsAsync(id, cancellationToken), false);
        }
        catch (Exception exception) when (IsReadFailure(exception))
        {
            return ([], true);
        }
    }

    // Operations carry the anime only as free text, so the store narrows by text and the exact subject decides.
    private async Task<IReadOnlyList<OperationSnapshot>> LoadOperationsAsync(Anime anime, CancellationToken cancellationToken)
    {
        var found = await new OperationStore(db).ListAsync(
            new OperationListFilter(Search: anime.Title, Limit: OperationSearchLimit),
            cancellationToken);
        return
        [
            .. found
                .Where(operation => string.Equals(operation.Subject, anime.Title, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(operation.Subject, anime.Key, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(operation => operation.UpdatedAtUtc)
                .Take(OperationLimit)
        ];
    }

    private static bool IsReadFailure(Exception exception) =>
        exception is DbException or InvalidOperationException or IOException or JsonException or AniListAccountException
            or InvalidDataException or UnauthorizedAccessException
            or System.Security.Cryptography.CryptographicException;
}
