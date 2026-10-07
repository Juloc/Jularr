using System.Text.RegularExpressions;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.Import;
using Jularr.Web.Features.Acquisition.Release;
using Jularr.Web.Features.Acquisition.Selection;
using Jularr.Web.Features.Library;
using Jularr.Web.Features.Storage;

namespace Jularr.Web.Features.Movies;

/// <summary>
/// The Movie importer behind the shared completed-download dispatcher and the Movie inbox scan (#593). A completed download's
/// largest video file is placed into the default Movie LibraryRoot (Storage owns the destination and the placement policy,
/// #815) as <c>Title (Year)/Title (Year).ext</c> with its subtitle/nfo sidecars and recorded as one <see cref="Movie"/> bridged
/// to the universal media core. Without an enabled default root nothing is placed and nothing is read in place: the import
/// waits with an explanation until the owner chooses a root. Metadata comes from the acquisition request when there is one,
/// otherwise from the release name via the shared scene parser (never a filename-only provider match).
/// </summary>
public sealed partial class MovieCompletedDownloadImportAdapter(
    MovieLibraryService movies,
    MediaAcquisitionRegistry registry,
    LibraryRootRoutingService routing,
    LibraryRootAvailabilityService availability,
    IHardLinkCreator hardLinks,
    ILogger<MovieCompletedDownloadImportAdapter> logger,
    CanonicalMediaStorageService? canonicalStorage = null,
    InstalledVideoVersions? upgrades = null)
    : ICompletedDownloadImportAdapter, IMediaInboxImportAdapter
{
    /// <summary>Why a finished download did not become a movie; the next release is tried.</summary>
    public const string NoVideoFileReason = "The download contained no video file.";

    public MediaAcquisitionKind Kind => MediaAcquisitionKind.Movie;

    [GeneratedRegex(@"(?<year>(?:19|20)\d{2})", RegexOptions.CultureInvariant)]
    private static partial Regex YearRegex();

    public async Task<CompletedDownloadImportResult> ImportAsync(CompletedDownloadImportRequest request, CancellationToken cancellationToken)
    {
        var files = CompletedDownloadFiles.Enumerate(request.SourcePath, out var error);
        if (error is not null)
        {
            // Not the release's fault: an unmapped path or an offline share. Wait rather than burn releases.
            return CompletedDownloadImportResult.RetryLater(error);
        }

        var video = files.Where(file => CompletedDownloadFiles.IsVideo(file.Path)).OrderByDescending(file => file.SizeBytes).FirstOrDefault();
        if (video is null)
        {
            return CompletedDownloadImportResult.RejectRelease(NoVideoFileReason);
        }

        var route = await routing.ResolveDefaultAsync(LibraryContentType.Movie, cancellationToken);
        if (route is null)
        {
            return CompletedDownloadImportResult.RetryLater(LibraryRootRoutingService.MissingDefaultMessage(LibraryContentType.Movie));
        }

        if (!await availability.IsReadyForImportAsync(route.LibraryRootId, cancellationToken))
        {
            return CompletedDownloadImportResult.RetryLater($"The Movie library root '{route.Name}' is not available.");
        }

        try
        {
            await request.ReportProgressAsync(CompletedDownloadImportPhase.Importing, "Placing the movie in the library.");
            var placed = await PlaceAsync(route, files, video.Path, ResolveMetadata(request, video.Path), cancellationToken);
            var mode = ImportFileTransfer.ModeFor(route.PlacementPolicy);
            return CompletedDownloadImportResult.Completed($"Imported movie \"{placed.Movie.Title}\".", resultUrl: null, new CompletedDownloadPlacement(placed.Folder, mode));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (DestinationMismatchException exception)
        {
            return CompletedDownloadImportResult.NeedsReview(exception.Message);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or CrossDeviceLinkException)
        {
            logger.LogWarning(exception, "Movie import is waiting for storage for '{SourcePath}'.", request.SourcePath);
            return CompletedDownloadImportResult.RetryLater("The movie import is waiting for storage.");
        }
    }

    public async Task<MediaInboxImportResult> ImportInboxAsync(string inboxRoot, IReadOnlyCollection<string> excludedFolders, CancellationToken cancellationToken)
    {
        var files = CompletedDownloadFiles.Enumerate(inboxRoot, out var error);
        if (error is not null)
        {
            return new MediaInboxImportResult(0, error);
        }

        var route = await routing.ResolveDefaultAsync(LibraryContentType.Movie, cancellationToken);
        if (route is null)
        {
            return new MediaInboxImportResult(0, LibraryRootRoutingService.MissingDefaultMessage(LibraryContentType.Movie));
        }

        if (!await availability.IsReadyForImportAsync(route.LibraryRootId, cancellationToken))
        {
            return new MediaInboxImportResult(0, $"The Movie library root '{route.Name}' is not available.");
        }

        var imported = 0;
        var skipped = new List<string>();
        foreach (var file in files)
        {
            if (!CompletedDownloadFiles.IsVideo(file.Path) || IsExcluded(file.Path, excludedFolders))
            {
                continue;
            }

            try
            {
                await PlaceAsync(route, files, file.Path, ResolveMetadata(request: null, file.Path), cancellationToken);
                imported++;
            }
            catch (DestinationMismatchException exception)
            {
                skipped.Add(exception.Message);
            }
        }

        var message = imported == 0 ? "No new movies in the inbox." : $"Imported {imported} movie(s) from the inbox.";
        return new MediaInboxImportResult(imported, skipped.Count == 0 ? message : $"{message} Skipped: {string.Join(" ", skipped)}");
    }

    /// <summary>
    /// Places one video into the Movie root with the root's placement policy and records it. Idempotent: a movie already placed at the
    /// destination is left as is and only its record is refreshed, so re-running an inbox that keeps its sources never duplicates. A file that
    /// is a meaningful upgrade of the installed movie (the upgrade policy of its profile) replaces it: the old file stays until the new one is
    /// recorded, then goes, and the Version records the new quality.
    /// </summary>
    private async Task<(Movie Movie, string Folder)> PlaceAsync(LibraryRootRoute route, IReadOnlyList<CompletedDownloadFile> files, string videoPath, MovieMetadata metadata, CancellationToken cancellationToken)
    {
        var (action, allowFallback) = ImportFileTransfer.Resolve(ImportFileTransfer.ModeFor(route.PlacementPolicy));
        var folder = Path.Combine(route.Path, MovieNaming.FolderName(metadata.Title, metadata.Year));
        var destination = Path.Combine(folder, MovieNaming.FileName(metadata.Title, metadata.Year, Path.GetExtension(videoPath)));
        if (!StoragePaths.IsBelow(destination, route.Path))
        {
            throw new InvalidOperationException("The movie destination would leave its library root.");
        }

        var alreadyPlaced = LibraryFilePlacer.FindDestinationConflict(destination, []) is not null;
        var incomingQuality = upgrades?.QualityOfDownload(MediaAcquisitionKind.Movie, videoPath);

        // The records come first so a database failure cannot happen after the file already left the source.
        var entry = await movies.EnsureAsync(metadata.Title, metadata.Year, metadata.TmdbId, metadata.ImdbId, folder, cancellationToken);
        var judgement = upgrades is null || canonicalStorage is null
            ? new IncomingVideoJudgement(IncomingVideoVerdict.Undecidable, [])
            : await upgrades.JudgeIncomingAsync(MediaAcquisitionKind.Movie, entry.WorkId, workEpisodeId: null, incomingQuality, cancellationToken);
        var isUpgrade = judgement.Verdict == IncomingVideoVerdict.Upgrade;
        if (alreadyPlaced && !isUpgrade && !LibraryFilePlacer.IsCompletePlacement(videoPath, destination))
        {
            throw new DestinationMismatchException(destination);
        }

        ReplacedLibraryFile? replaced = null;
        if (isUpgrade && alreadyPlaced && !LibraryFilePlacer.IsCompletePlacement(videoPath, destination))
        {
            replaced = ReplacedLibraryFile.SetAside(destination);
            alreadyPlaced = false;
        }

        var moved = false;
        try
        {
            if (!alreadyPlaced)
            {
                var sidecars = BuildSidecars(files, videoPath, Path.GetFileNameWithoutExtension(destination));
                new LibraryFilePlacer(new ImportFileTransfer(hardLinks)).Place(new LibraryFilePlacement(videoPath, destination, action, allowFallback, sidecars, []));
                moved = action == ImportFileAction.Move;
            }

            if (canonicalStorage is not null)
            {
                try
                {
                    await canonicalStorage.AttachVideosAsync([new CanonicalVideoAttachment(entry.WorkId, null, Path.GetFullPath(destination), Path.GetFullPath(route.Path), incomingQuality)], cancellationToken);
                }
                catch when (moved)
                {
                    // The import is retried from the source, so the moved file must be there again.
                    LibraryFilePlacer.RestoreMovedSource(videoPath, destination);
                    throw;
                }
            }
        }
        catch
        {
            replaced?.Rollback();
            throw;
        }

        if (replaced is not null)
        {
            replaced.Commit();
        }

        if (isUpgrade && canonicalStorage is not null)
        {
            var kept = Path.GetFullPath(destination);
            await canonicalStorage.RemoveFilesAsync([.. judgement.Superseded.Where(file => !LibraryFilePlacer.SamePath(file.Path, kept)).Select(file => file.StoredFileId)], cancellationToken);
        }

        return (entry.Movie, folder);
    }

    private MovieMetadata ResolveMetadata(CompletedDownloadImportRequest? request, string videoPath)
    {
        var name = Path.GetFileNameWithoutExtension(videoPath);
        var release = registry.ParserFor(MediaAcquisitionKind.Movie).TryParse(name, out var parsed) ? parsed : null;
        var imdb = release?.ImdbId is { Length: > 0 } imdbId ? imdbId : null;

        if (request?.Request is { } acquisition)
        {
            var requestedYear = VideoRequestPayload.Parse(acquisition.PayloadJson)?.Year
                ?? TryParseYear(acquisition.Title)
                ?? release?.AirDate?.Year
                ?? TryParseYear(name);
            var tmdb = string.Equals(acquisition.Provider, "tmdb", StringComparison.OrdinalIgnoreCase)
                ? acquisition.ExternalId
                : null;
            if (string.Equals(acquisition.Provider, "imdb", StringComparison.OrdinalIgnoreCase))
            {
                imdb ??= acquisition.ExternalId;
            }

            return new MovieMetadata(acquisition.Title, requestedYear, tmdb, imdb);
        }

        var title = release?.SeriesTitle is { Length: > 0 } seriesTitle ? seriesTitle : name;
        var year = release?.AirDate?.Year ?? TryParseYear(name);

        // Without season numbering the scene parser keeps the release year in the title ("Inception 2010"); the naming adds it again.
        if (year is int releaseYear && title.EndsWith($" {releaseYear}", StringComparison.Ordinal) && title.Length > 5)
        {
            title = title[..^5].TrimEnd();
        }

        return new MovieMetadata(title, year, null, imdb);
    }

    private static int? TryParseYear(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var match = YearRegex().Match(text);
        return match.Success ? int.Parse(match.Groups["year"].Value, System.Globalization.CultureInfo.InvariantCulture) : null;
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

    private readonly record struct MovieMetadata(string Title, int? Year, string? TmdbId, string? ImdbId);
}
