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

        if (!Directory.Exists(route.Path))
        {
            return CompletedDownloadImportResult.RetryLater($"The TV library root '{route.Name}' is not available.");
        }

        try
        {
            var placed = new List<PlacedEpisode>();
            foreach (var video in videos)
            {
                placed.Add(await PlaceAsync(route, files, video.Path, ResolveMetadata(request, video.Path), cancellationToken));
            }

            if (canonicalStorage is not null)
            {
                await canonicalStorage.AttachVideosAsync(placed.Select(episode => episode.Attachment).ToList(), cancellationToken);
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

        if (!Directory.Exists(route.Path))
        {
            return new MediaInboxImportResult(0, $"The TV library root '{route.Name}' is not available.");
        }

        var placed = new List<PlacedEpisode>();
        foreach (var file in files)
        {
            if (!CompletedDownloadFiles.IsVideo(file.Path) || IsExcluded(file.Path, excludedFolders))
            {
                continue;
            }

            placed.Add(await PlaceAsync(route, files, file.Path, ResolveMetadata(request: null, file.Path), cancellationToken));
        }

        if (canonicalStorage is not null)
        {
            await canonicalStorage.AttachVideosAsync(placed.Select(episode => episode.Attachment).ToList(), cancellationToken);
        }

        return new MediaInboxImportResult(placed.Count, placed.Count == 0 ? "No new episodes in the inbox." : $"Imported {placed.Count} episode(s) from the inbox.");
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
        if (!MediaInboxImportService.IsBelow(destination, route.Path))
        {
            throw new InvalidOperationException("The episode destination would leave its library root.");
        }

        var entry = await series.EnsureSeriesAsync(meta.Series, meta.Year, meta.TmdbId, meta.TvdbId, seriesFolder, cancellationToken);
        var workEpisode = await series.EnsureEpisodeAsync(entry.WorkId, meta.Season, meta.Episode, meta.EpisodeTitle, cancellationToken);
        if (LibraryFilePlacer.FindDestinationConflict(destination, []) is null)
        {
            var sidecars = BuildSidecars(files, videoPath, Path.GetFileNameWithoutExtension(destination));
            new LibraryFilePlacer(new ImportFileTransfer(hardLinks)).Place(new LibraryFilePlacement(videoPath, destination, action, allowFallback, sidecars, []));
        }

        return new PlacedEpisode(new CanonicalVideoAttachment(entry.WorkId, workEpisode.Id, Path.GetFullPath(destination), Path.GetFullPath(route.Path)), seriesFolder);
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

    private readonly record struct PlacedEpisode(CanonicalVideoAttachment Attachment, string SeriesFolder);
}
