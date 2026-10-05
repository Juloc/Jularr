using System.Globalization;
using System.Text.RegularExpressions;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.Import;
using Jularr.Web.Features.Acquisition.Release;
using Jularr.Web.Features.Library;
using Jularr.Web.Features.Storage;

namespace Jularr.Web.Features.Tv;

/// <summary>
/// The TV importer behind the shared completed-download dispatcher and the TV inbox scan (#594). Each video file of a completed
/// download (one episode, or many for a season pack) is placed into the default TV LibraryRoot (Storage owns the destination and
/// the placement policy, #815) as <c>Series (Year)/Season 01/Series - S01E02.ext</c> with its subtitle/nfo sidecars and recorded
/// as one episode of a <see cref="TvSeries"/> bridged to the universal media core, reusing the universal season/episode
/// structure. Without an enabled default root nothing is placed and nothing is read in place: the import waits with an
/// explanation until the owner chooses a root. Series and season/episode numbers come from the acquisition request when there is
/// one, otherwise from the release name via the shared scene parser (never a filename-only provider match).
/// </summary>
public sealed partial class TvCompletedDownloadImportAdapter(
    TvLibraryService series,
    MediaAcquisitionRegistry registry,
    LibraryRootRoutingService routing,
    LibraryRootAvailabilityService availability,
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

    public async Task<CompletedDownloadImportResult> ImportAsync(CompletedDownloadImportRequest request, CancellationToken cancellationToken)
    {
        var files = CompletedDownloadFiles.Enumerate(request.SourcePath, out var error);
        if (error is not null)
        {
            return CompletedDownloadImportResult.RetryLater(error);
        }

        var videos = files.Where(file => CompletedDownloadFiles.IsVideo(file.Path)).OrderBy(file => file.Path, StringComparer.Ordinal).ToList();
        if (videos.Count == 0)
        {
            return CompletedDownloadImportResult.RejectRelease(NoVideoFileReason);
        }

        var route = await routing.ResolveDefaultAsync(LibraryContentType.Tv, cancellationToken);
        if (route is null)
        {
            return CompletedDownloadImportResult.RetryLater(LibraryRootRoutingService.MissingDefaultMessage(LibraryContentType.Tv));
        }

        if (!await availability.IsReadyForImportAsync(route.LibraryRootId, cancellationToken))
        {
            return CompletedDownloadImportResult.RetryLater($"The TV library root '{route.Name}' is not available.");
        }

        var placed = new List<PlacedEpisode>();
        try
        {
            // Episodes moved out of the source before a later one failed must still be attached: a retry only sees what is left in the source.
            var failed = true;
            try
            {
                foreach (var video in videos)
                {
                    placed.Add(await PlaceAsync(route, files, video.Path, ResolveMetadata(request, video.Path), cancellationToken));
                }

                failed = false;
            }
            finally
            {
                await AttachPlacedAsync(placed, afterFailure: failed);
            }

            await request.ReportProgressAsync(CompletedDownloadImportPhase.Importing, $"Imported {placed.Count} episode(s).");
            return CompletedDownloadImportResult.Completed(
                $"Imported {placed.Count} episode(s).",
                resultUrl: null,
                new CompletedDownloadPlacement(placed[^1].SeriesFolder, ImportFileTransfer.ModeFor(route.PlacementPolicy)));
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
            logger.LogWarning(exception, "TV import is waiting for storage for '{SourcePath}'.", request.SourcePath);
            return CompletedDownloadImportResult.RetryLater("The TV import is waiting for storage.");
        }
    }

    public async Task<MediaInboxImportResult> ImportInboxAsync(string inboxRoot, IReadOnlyCollection<string> excludedFolders, CancellationToken cancellationToken)
    {
        var files = CompletedDownloadFiles.Enumerate(inboxRoot, out var error);
        if (error is not null)
        {
            return new MediaInboxImportResult(0, error);
        }

        var route = await routing.ResolveDefaultAsync(LibraryContentType.Tv, cancellationToken);
        if (route is null)
        {
            return new MediaInboxImportResult(0, LibraryRootRoutingService.MissingDefaultMessage(LibraryContentType.Tv));
        }

        if (!await availability.IsReadyForImportAsync(route.LibraryRootId, cancellationToken))
        {
            return new MediaInboxImportResult(0, $"The TV library root '{route.Name}' is not available.");
        }

        var placed = new List<PlacedEpisode>();
        var skipped = new List<string>();
        var failed = true;
        try
        {
            foreach (var file in files)
            {
                if (!CompletedDownloadFiles.IsVideo(file.Path) || IsExcluded(file.Path, excludedFolders))
                {
                    continue;
                }

                try
                {
                    placed.Add(await PlaceAsync(route, files, file.Path, ResolveMetadata(request: null, file.Path), cancellationToken));
                }
                catch (DestinationMismatchException exception)
                {
                    skipped.Add(exception.Message);
                }
            }

            failed = false;
        }
        finally
        {
            await AttachPlacedAsync(placed, afterFailure: failed);
        }

        var message = placed.Count == 0 ? "No new episodes in the inbox." : $"Imported {placed.Count} episode(s) from the inbox.";
        return new MediaInboxImportResult(placed.Count, skipped.Count == 0 ? message : $"{message} Skipped: {string.Join(" ", skipped)}");
    }

    /// <summary>
    /// Places one episode into the TV root with the root's placement policy and records the series and episode. Idempotent: an episode
    /// already placed at the destination is left as is and only its records are refreshed.
    /// </summary>
    private async Task<PlacedEpisode> PlaceAsync(LibraryRootRoute route, IReadOnlyList<CompletedDownloadFile> files, string videoPath, EpisodeMetadata meta, CancellationToken cancellationToken)
    {
        var (action, allowFallback) = ImportFileTransfer.Resolve(ImportFileTransfer.ModeFor(route.PlacementPolicy));
        var seriesFolder = Path.Combine(route.Path, TvNaming.SeriesFolderName(meta.Series, meta.Year));
        var destination = Path.Combine(seriesFolder, TvNaming.SeasonFolderName(meta.Season), TvNaming.EpisodeFileName(meta.Series, meta.Season, meta.Episode, meta.EpisodeTitle, Path.GetExtension(videoPath)));
        if (!StoragePaths.IsBelow(destination, route.Path))
        {
            throw new InvalidOperationException("The episode destination would leave its library root.");
        }

        var alreadyPlaced = LibraryFilePlacer.FindDestinationConflict(destination, []) is not null;
        if (alreadyPlaced && !LibraryFilePlacer.IsCompletePlacement(videoPath, destination))
        {
            throw new DestinationMismatchException(destination);
        }

        // The records come first so a database failure cannot happen after the file already left the source.
        var entry = await series.EnsureSeriesAsync(meta.Series, meta.Year, meta.TmdbId, meta.TvdbId, seriesFolder, cancellationToken);
        var workEpisode = await series.EnsureEpisodeAsync(entry.WorkId, meta.Season, meta.Episode, meta.EpisodeTitle, cancellationToken);
        var moved = false;
        if (!alreadyPlaced)
        {
            var sidecars = BuildSidecars(files, videoPath, Path.GetFileNameWithoutExtension(destination));
            new LibraryFilePlacer(new ImportFileTransfer(hardLinks)).Place(new LibraryFilePlacement(videoPath, destination, action, allowFallback, sidecars, []));
            moved = action == ImportFileAction.Move;
        }

        var attachment = new CanonicalVideoAttachment(entry.WorkId, workEpisode.Id, Path.GetFullPath(destination), Path.GetFullPath(route.Path));
        return new PlacedEpisode(attachment, seriesFolder, moved ? videoPath : null);
    }

    // Attaching is data safety, not part of the request: it runs to completion even when the import is being cancelled. When it fails, the
    // episodes that were moved out of the source go back so a retry still finds them. A failure here never hides the failure that made the
    // import stop (<paramref name="afterFailure"/>): it is logged and the first one propagates.
    private async Task AttachPlacedAsync(List<PlacedEpisode> placed, bool afterFailure)
    {
        if (canonicalStorage is null || placed.Count == 0)
        {
            return;
        }

        try
        {
            await canonicalStorage.AttachVideosAsync(placed.Select(episode => episode.Attachment).ToList(), CancellationToken.None);
        }
        catch (Exception exception)
        {
            foreach (var episode in placed.Where(episode => episode.MovedFrom is not null))
            {
                LibraryFilePlacer.RestoreMovedSource(episode.MovedFrom!, episode.Attachment.Path);
            }

            if (!afterFailure)
            {
                throw;
            }

            logger.LogError(exception, "Attaching the episodes placed before the failure also failed.");
        }
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
        var canonicalRequest = request?.Request is { } storedRequest
            ? VideoAcquisitionEngine.ReadPayload(storedRequest)
            : null;
        var year = canonicalRequest?.Year
            ?? TryParseYear(request?.Request?.Title)
            ?? release?.AirDate?.Year;

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

    /// <summary><paramref name="MovedFrom"/> is the source path when the file was moved (not copied or linked) into the library.</summary>
    private readonly record struct PlacedEpisode(CanonicalVideoAttachment Attachment, string SeriesFolder, string? MovedFrom);
}
