using System.Globalization;
using System.Text.RegularExpressions;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.Import;
using Jularr.Web.Features.Acquisition.Release;
using Jularr.Web.Features.Library;

namespace Jularr.Web.Features.Tv;

/// <summary>
/// The TV importer behind the shared completed-download dispatcher and the TV inbox scan (#594). Each video
/// file of a completed download (one episode, or many for a season pack) is placed into the series library as
/// <c>Series (Year)/Season 01/Series - S01E02.ext</c> (with its subtitle/nfo sidecars) and recorded as one
/// episode of a <see cref="TvSeries"/> bridged to the universal media core, reusing the universal
/// season/episode structure. When no TV library folder is configured the files are imported in place. Series
/// and season/episode numbers come from the acquisition request when there is one, otherwise from the release
/// name via the shared scene parser (never a filename-only provider match).
/// </summary>
public sealed partial class TvCompletedDownloadImportAdapter(
    TvLibraryService series,
    MediaAcquisitionRegistry registry,
    AnimeImportSettingsStore importSettings,
    IHardLinkCreator hardLinks,
    ILogger<TvCompletedDownloadImportAdapter> logger,
    CanonicalMediaStorageService? canonicalStorage = null)
    : ICompletedDownloadImportAdapter, IMediaInboxImportAdapter
{
    /// <summary>Why a finished download did not become an episode; the next release is tried.</summary>
    public const string NoVideoFileReason = "The download contained no video file.";

    public MediaAcquisitionKind Kind => MediaAcquisitionKind.Tv;

    [GeneratedRegex(@"(?<year>(?:19|20)\d{2})", RegexOptions.CultureInvariant)]
    private static partial Regex YearRegex();

    public async Task<CompletedDownloadImportResult> ImportAsync(
        CompletedDownloadImportRequest request,
        CancellationToken cancellationToken)
    {
        var files = CompletedDownloadFiles.Enumerate(request.SourcePath, out var error);
        if (error is not null)
        {
            return CompletedDownloadImportResult.RetryLater(error);
        }

        var videos = files
            .Where(file => CompletedDownloadFiles.IsVideo(file.Path))
            .OrderBy(file => file.Path, StringComparer.Ordinal)
            .ToList();
        if (videos.Count == 0)
        {
            return CompletedDownloadImportResult.RejectRelease(NoVideoFileReason);
        }

        try
        {
            var settings = await importSettings.LoadAsync(cancellationToken);
            var library = settings.LibraryFor(MediaAcquisitionKind.Tv);
            var mode = settings.ModeFor(MediaAcquisitionKind.Tv);
            var (action, allowFallback) = ImportFileTransfer.Resolve(mode);
            var placer = new LibraryFilePlacer(new ImportFileTransfer(hardLinks));

            var imported = 0;
            var canonicalAttachments = new List<CanonicalVideoAttachment>();
            CompletedDownloadPlacement? placement = null;
            foreach (var video in videos)
            {
                var meta = ResolveMetadata(request, video.Path);
                string? seriesFolder = library is not null
                    ? Path.Combine(library.LibraryRoot!, TvNaming.SeriesFolderName(meta.Series, meta.Year))
                    : Path.GetDirectoryName(video.Path);

                var entry = await series.EnsureSeriesAsync(
                    meta.Series, meta.Year, meta.TmdbId, meta.TvdbId, seriesFolder, cancellationToken);
                var workEpisode = await series.EnsureEpisodeAsync(
                    entry.WorkId, meta.Season, meta.Episode, meta.EpisodeTitle, cancellationToken);
                var storedVideoPath = Path.GetFullPath(video.Path);
                var storageRootPath = Path.GetDirectoryName(storedVideoPath);

                if (library is not null)
                {
                    var seasonFolder = Path.Combine(seriesFolder!, TvNaming.SeasonFolderName(meta.Season));
                    var destination = Path.Combine(
                        seasonFolder,
                        TvNaming.EpisodeFileName(
                            meta.Series, meta.Season, meta.Episode, meta.EpisodeTitle, Path.GetExtension(video.Path)));

                    if (LibraryFilePlacer.FindDestinationConflict(destination, []) is null)
                    {
                        var sidecars = BuildSidecars(files, video.Path, Path.GetFileNameWithoutExtension(destination));
                        placer.Place(new LibraryFilePlacement(video.Path, destination, action, allowFallback, sidecars, []));
                    }

                    storedVideoPath = Path.GetFullPath(destination);
                    storageRootPath = Path.GetFullPath(library.LibraryRoot!);
                    placement = new CompletedDownloadPlacement(seriesFolder!, mode);
                }

                canonicalAttachments.Add(new CanonicalVideoAttachment(
                    entry.WorkId,
                    workEpisode.Id,
                    storedVideoPath,
                    storageRootPath));
                imported++;
            }

            if (canonicalStorage is not null)
            {
                await canonicalStorage.AttachVideosAsync(canonicalAttachments, cancellationToken);
            }

            await request.ReportProgressAsync(CompletedDownloadImportPhase.Importing, $"Imported {imported} episode(s).");
            return CompletedDownloadImportResult.Completed(
                $"Imported {imported} episode(s).", resultUrl: null, placement);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or CrossDeviceLinkException)
        {
            logger.LogWarning(exception, "TV import is waiting for storage for '{SourcePath}'.", request.SourcePath);
            return CompletedDownloadImportResult.RetryLater("The TV import is waiting for storage.");
        }
    }

    public async Task<MediaInboxImportResult> ImportInboxAsync(
        string inboxRoot,
        IReadOnlyCollection<string> excludedFolders,
        CancellationToken cancellationToken)
    {
        var files = CompletedDownloadFiles.Enumerate(inboxRoot, out var error);
        if (error is not null)
        {
            return new MediaInboxImportResult(0, error);
        }

        var imported = 0;
        var canonicalAttachments = new List<CanonicalVideoAttachment>();
        foreach (var file in files)
        {
            if (!CompletedDownloadFiles.IsVideo(file.Path) || IsExcluded(file.Path, excludedFolders))
            {
                continue;
            }

            var meta = ResolveMetadata(request: null, file.Path);
            var entry = await series.EnsureSeriesAsync(
                meta.Series, meta.Year, meta.TmdbId, meta.TvdbId, Path.GetDirectoryName(file.Path), cancellationToken);
            var workEpisode = await series.EnsureEpisodeAsync(
                entry.WorkId, meta.Season, meta.Episode, meta.EpisodeTitle, cancellationToken);
            canonicalAttachments.Add(new CanonicalVideoAttachment(
                entry.WorkId,
                workEpisode.Id,
                file.Path,
                inboxRoot));
            imported++;
        }

        if (canonicalStorage is not null)
        {
            await canonicalStorage.AttachVideosAsync(canonicalAttachments, cancellationToken);
        }

        return new MediaInboxImportResult(
            imported,
            imported == 0 ? "No new episodes in the inbox." : $"Imported {imported} episode(s) from the inbox.");
    }

    private EpisodeMetadata ResolveMetadata(CompletedDownloadImportRequest? request, string videoPath)
    {
        var name = Path.GetFileNameWithoutExtension(videoPath);
        var release = registry.ParserFor(MediaAcquisitionKind.Tv).TryParse(name, out var parsed) ? parsed : null;

        var seriesTitle = request?.Request?.Title;
        if (string.IsNullOrWhiteSpace(seriesTitle))
        {
            seriesTitle = release?.SeriesTitle is { Length: > 0 } parsedTitle ? parsedTitle : name;
        }

        var season = release?.SeasonNumber ?? 1;
        var episode = release?.EpisodeStart ?? 1;
        var year = TryParseYear(request?.Request?.Title) ?? release?.AirDate?.Year;

        string? tmdb = null;
        string? tvdb = null;
        if (request?.Request is { } acquisition)
        {
            if (string.Equals(acquisition.Provider, "tmdb", StringComparison.OrdinalIgnoreCase))
            {
                tmdb = acquisition.ExternalId;
            }
            else if (string.Equals(acquisition.Provider, "tvdb", StringComparison.OrdinalIgnoreCase))
            {
                tvdb = acquisition.ExternalId;
            }
        }

        return new EpisodeMetadata(seriesTitle!, year, season, episode, EpisodeTitle: null, tmdb, tvdb);
    }

    private static int? TryParseYear(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var match = YearRegex().Match(text);
        return match.Success ? int.Parse(match.Groups["year"].Value, CultureInfo.InvariantCulture) : null;
    }

    private static bool IsExcluded(string path, IReadOnlyCollection<string> excludedFolders) =>
        excludedFolders.Any(folder =>
            !string.IsNullOrWhiteSpace(folder) &&
            path.Replace('\\', '/').StartsWith(folder.Replace('\\', '/').TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase));

    private static IReadOnlyList<PlacedSidecar> BuildSidecars(
        IReadOnlyList<CompletedDownloadFile> files,
        string videoPath,
        string destinationBaseName)
    {
        var directory = Path.GetDirectoryName(videoPath) ?? "";
        var videoBaseName = Path.GetFileNameWithoutExtension(videoPath);
        var sidecars = new List<PlacedSidecar>();
        foreach (var file in files)
        {
            if (!CompletedDownloadFiles.IsSidecar(file.Path))
            {
                continue;
            }

            if (!string.Equals(Path.GetDirectoryName(file.Path) ?? "", directory, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var fileName = Path.GetFileName(file.Path);
            if (!fileName.StartsWith(videoBaseName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var suffix = fileName[videoBaseName.Length..];
            sidecars.Add(new PlacedSidecar(file.Path, destinationBaseName + suffix));
        }

        return sidecars;
    }

    private readonly record struct EpisodeMetadata(
        string Series,
        int? Year,
        int Season,
        int Episode,
        string? EpisodeTitle,
        string? TmdbId,
        string? TvdbId);
}
