using System.Text.RegularExpressions;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.Import;
using Jularr.Web.Features.Acquisition.Release;
using Jularr.Web.Features.Library;

namespace Jularr.Web.Features.Movies;

/// <summary>
/// The Movie importer behind the shared completed-download dispatcher and the Movie inbox scan (#593).
/// A completed download's largest video file is placed into the movie library as
/// <c>Title (Year)/Title (Year).ext</c> (with its subtitle/nfo sidecars) and recorded as one
/// <see cref="Movie"/> bridged to the universal media core; when no movie library folder is configured the
/// file is imported in place. Metadata comes from the acquisition request when there is one, otherwise from
/// the release name via the shared scene parser (never a filename-only provider match).
/// </summary>
public sealed partial class MovieCompletedDownloadImportAdapter(
    MovieLibraryService movies,
    MediaAcquisitionRegistry registry,
    AnimeImportSettingsStore importSettings,
    IHardLinkCreator hardLinks,
    ILogger<MovieCompletedDownloadImportAdapter> logger,
    CanonicalMediaStorageService? canonicalStorage = null)
    : ICompletedDownloadImportAdapter, IMediaInboxImportAdapter
{
    /// <summary>Why a finished download did not become a movie; the next release is tried.</summary>
    public const string NoVideoFileReason = "The download contained no video file.";

    public MediaAcquisitionKind Kind => MediaAcquisitionKind.Movie;

    [GeneratedRegex(@"(?<year>(?:19|20)\d{2})", RegexOptions.CultureInvariant)]
    private static partial Regex YearRegex();

    public async Task<CompletedDownloadImportResult> ImportAsync(
        CompletedDownloadImportRequest request,
        CancellationToken cancellationToken)
    {
        var files = CompletedDownloadFiles.Enumerate(request.SourcePath, out var error);
        if (error is not null)
        {
            // Not the release's fault: an unmapped path or an offline share. Wait rather than burn releases.
            return CompletedDownloadImportResult.RetryLater(error);
        }

        var video = files
            .Where(file => CompletedDownloadFiles.IsVideo(file.Path))
            .OrderByDescending(file => file.SizeBytes)
            .FirstOrDefault();
        if (video is null)
        {
            return CompletedDownloadImportResult.RejectRelease(NoVideoFileReason);
        }

        var metadata = ResolveMetadata(request, video.Path);

        try
        {
            var settings = await importSettings.LoadAsync(cancellationToken);
            var library = settings.LibraryFor(MediaAcquisitionKind.Movie);
            string? libraryPath;
            var storedVideoPath = Path.GetFullPath(video.Path);
            var storageRootPath = Path.GetDirectoryName(storedVideoPath);
            CompletedDownloadPlacement? placement = null;

            if (library is not null)
            {
                var mode = settings.ModeFor(MediaAcquisitionKind.Movie);
                var (action, allowFallback) = ImportFileTransfer.Resolve(mode);
                var folder = Path.Combine(library.LibraryRoot!, MovieNaming.FolderName(metadata.Title, metadata.Year));
                var destination = Path.Combine(
                    folder, MovieNaming.FileName(metadata.Title, metadata.Year, Path.GetExtension(video.Path)));

                // Idempotent: a movie already placed here is left as is (re-import only refreshes the record).
                if (LibraryFilePlacer.FindDestinationConflict(destination, []) is null)
                {
                    var sidecars = BuildSidecars(files, video.Path, Path.GetFileNameWithoutExtension(destination));
                    await request.ReportProgressAsync(CompletedDownloadImportPhase.Importing, "Placing the movie in the library.");
                    new LibraryFilePlacer(new ImportFileTransfer(hardLinks)).Place(
                        new LibraryFilePlacement(video.Path, destination, action, allowFallback, sidecars, []));
                }

                libraryPath = folder;
                storedVideoPath = Path.GetFullPath(destination);
                storageRootPath = Path.GetFullPath(library.LibraryRoot!);
                placement = new CompletedDownloadPlacement(folder, mode);
            }
            else
            {
                libraryPath = Path.GetDirectoryName(video.Path);
            }

            var entry = await movies.EnsureAsync(
                metadata.Title, metadata.Year, metadata.TmdbId, metadata.ImdbId, libraryPath, cancellationToken);
            if (canonicalStorage is not null)
            {
                await canonicalStorage.AttachVideoAsync(
                    entry.WorkId,
                    workEpisodeId: null,
                    storedVideoPath,
                    storageRootPath,
                    cancellationToken);
            }

            return CompletedDownloadImportResult.Completed(
                $"Imported movie \"{entry.Movie.Title}\".", resultUrl: "/Library", placement);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or CrossDeviceLinkException)
        {
            logger.LogWarning(exception, "Movie import is waiting for storage for '{SourcePath}'.", request.SourcePath);
            return CompletedDownloadImportResult.RetryLater("The movie import is waiting for storage.");
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
        foreach (var file in files)
        {
            if (!CompletedDownloadFiles.IsVideo(file.Path) || IsExcluded(file.Path, excludedFolders))
            {
                continue;
            }

            var metadata = ResolveMetadata(request: null, file.Path);
            var entry = await movies.EnsureAsync(
                metadata.Title, metadata.Year, metadata.TmdbId, metadata.ImdbId,
                Path.GetDirectoryName(file.Path), cancellationToken);
            if (canonicalStorage is not null)
            {
                await canonicalStorage.AttachVideoAsync(
                    entry.WorkId,
                    workEpisodeId: null,
                    file.Path,
                    inboxRoot,
                    cancellationToken);
            }

            imported++;
        }

        return new MediaInboxImportResult(
            imported,
            imported == 0 ? "No new movies in the inbox." : $"Imported {imported} movie(s) from the inbox.");
    }

    private MovieMetadata ResolveMetadata(CompletedDownloadImportRequest? request, string videoPath)
    {
        var name = Path.GetFileNameWithoutExtension(videoPath);
        var release = registry.ParserFor(MediaAcquisitionKind.Movie).TryParse(name, out var parsed) ? parsed : null;
        var imdb = release?.ImdbId is { Length: > 0 } imdbId ? imdbId : null;

        if (request?.Request is { } acquisition)
        {
            var requestedYear = TryParseYear(acquisition.Title) ?? release?.AirDate?.Year ?? TryParseYear(name);
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
        return new MovieMetadata(title, release?.AirDate?.Year ?? TryParseYear(name), null, imdb);
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
