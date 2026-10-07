using Jularr.Web.Data;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.Import;
using Jularr.Web.Features.Acquisition.Quality;
using Jularr.Web.Features.Acquisition.Release;
using Jularr.Web.Features.Acquisition.Selection;
using Jularr.Web.Features.Library;
using Jularr.Web.Features.MediaCore;
using Jularr.Web.Features.Naming;
using Jularr.Web.Features.Storage;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Web.Features.Music;

/// <summary>The library layout of music: <c>Artist/Album (Year)/NN - Title.ext</c>, with a disc prefix when the album has several discs.</summary>
public static class MusicNaming
{
    public static string ArtistFolder(string artist) => Clean(artist, "Unknown Artist");

    public static string AlbumFolder(string album, int? year)
    {
        var title = Clean(album, "Unknown Album");
        return year is > 0 ? $"{title} ({year})" : title;
    }

    public static string TrackFileName(int disc, int number, bool multiDisc, string title, string extension) =>
        $"{(multiDisc ? $"{disc}-" : string.Empty)}{number:00} - {Clean(title, $"Track {number}")}{extension.ToLowerInvariant()}";

    private static string Clean(string value, string fallback)
    {
        var clean = NamingTemplateEngine.CleanFileName(value ?? string.Empty);
        return clean.Length == 0 ? fallback : clean.Length > 120 ? clean[..120].TrimEnd() : clean;
    }
}

/// <summary>
/// The Music importer behind the shared completed-download dispatcher and the Music inbox scan. The audio files of a download are matched to
/// the tracks of the requested album and placed into the default Music LibraryRoot with that root's placement policy (Storage owns both);
/// every placed file becomes one canonical Audio asset of its track. A download that carries too few of the album's tracks is the wrong
/// release and the next one is tried; an offline root or unreachable path only waits. Importing the same download again never duplicates
/// a file: a destination that already holds the complete file is recorded again and left as it is. An album that is already in the library is
/// only replaced by a complete release that is a meaningful upgrade of its weakest track (the shared upgrade policy); anything else only fills
/// the tracks that have no file, so a partial or lesser download never degrades what is installed.
/// </summary>
public sealed class MusicCompletedDownloadImportAdapter(
    AppDbContext db,
    MusicLibraryService library,
    LibraryRootRoutingService routing,
    LibraryRootAvailabilityService availability,
    IHardLinkCreator hardLinks,
    CanonicalMediaStorageService canonicalStorage,
    ILogger<MusicCompletedDownloadImportAdapter> logger,
    QualityProfileStore? profiles = null)
    : ICompletedDownloadImportAdapter, IMediaInboxImportAdapter
{
    public const string NoAudioFileReason = "The download contained no audio file.";

    /// <summary>The share of an album's tracks a download has to carry; fewer means another album or a partial release.</summary>
    public const double RequiredTrackShare = 0.6;

    public MediaAcquisitionKind Kind => MediaAcquisitionKind.Music;

    public async Task<CompletedDownloadImportResult> ImportAsync(CompletedDownloadImportRequest request, CancellationToken cancellationToken)
    {
        var files = CompletedDownloadFiles.Enumerate(request.SourcePath, out var error);
        if (error is not null)
        {
            return CompletedDownloadImportResult.RetryLater(error);
        }

        var audio = files.Where(file => MusicTrackMatcher.IsAudio(file.Path)).ToArray();
        if (audio.Length == 0)
        {
            return CompletedDownloadImportResult.RejectRelease(NoAudioFileReason);
        }

        if (request.Request is not { } acquisition)
        {
            return CompletedDownloadImportResult.NeedsReview("A music download without a request cannot be matched to an album. Add the artist and request the album.");
        }

        var workId = MusicRequestPayload.Of(acquisition).WorkId;
        var album = await LoadAlbumAsync(workId == Guid.Empty ? null : workId, acquisition.ExternalId, cancellationToken);
        if (album is null)
        {
            return CompletedDownloadImportResult.Failed("The requested album no longer exists.");
        }

        var route = await routing.ResolveDefaultAsync(LibraryContentType.Music, cancellationToken);
        if (route is null)
        {
            return CompletedDownloadImportResult.RetryLater(LibraryRootRoutingService.MissingDefaultMessage(LibraryContentType.Music));
        }

        if (!await availability.IsReadyForImportAsync(route.LibraryRootId, cancellationToken))
        {
            return CompletedDownloadImportResult.RetryLater($"The Music library root '{route.Name}' is not available.");
        }

        try
        {
            await request.ReportProgressAsync(CompletedDownloadImportPhase.Importing, "Placing the album in the library.");
            var outcome = await PlaceAlbumAsync(album, audio, Path.GetFileName(request.SourcePath.TrimEnd('/', '\\')), route, cancellationToken);
            var placement = new CompletedDownloadPlacement(outcome.Folder, ImportFileTransfer.ModeFor(route.PlacementPolicy));
            return outcome.Rejection is { } reason
                ? CompletedDownloadImportResult.RejectRelease(reason)
                : CompletedDownloadImportResult.Completed($"Imported {outcome.Placed} track(s) of \"{album.Title}\".", MusicLinks.AlbumPath(album.WorkId), placement);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (MusicMetadataException exception)
        {
            logger.LogInformation(exception, "The track list of album {WorkId} is not available yet.", album.WorkId);
            return CompletedDownloadImportResult.RetryLater("The track list of the album could not be read yet; the import waits.");
        }
        catch (DestinationMismatchException exception)
        {
            return CompletedDownloadImportResult.NeedsReview(exception.Message);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or CrossDeviceLinkException)
        {
            logger.LogWarning(exception, "Music import is waiting for storage for '{SourcePath}'.", request.SourcePath);
            return CompletedDownloadImportResult.RetryLater("The music import is waiting for storage.");
        }
    }

    /// <summary>Imports every folder of the inbox whose name identifies exactly one known album (<c>Artist - Album (Year)</c>); anything else is left untouched.</summary>
    public async Task<MediaInboxImportResult> ImportInboxAsync(string inboxRoot, IReadOnlyCollection<string> excludedFolders, CancellationToken cancellationToken)
    {
        var route = await routing.ResolveDefaultAsync(LibraryContentType.Music, cancellationToken);
        if (route is null)
        {
            return new MediaInboxImportResult(0, LibraryRootRoutingService.MissingDefaultMessage(LibraryContentType.Music));
        }

        if (!await availability.IsReadyForImportAsync(route.LibraryRootId, cancellationToken))
        {
            return new MediaInboxImportResult(0, $"The Music library root '{route.Name}' is not available.");
        }

        if (!Directory.Exists(inboxRoot))
        {
            return new MediaInboxImportResult(0, $"The inbox folder does not exist or is not mounted in Jularr: {inboxRoot}");
        }

        var known = await db.MusicAlbums.AsNoTracking()
            .Join(db.MusicArtists.AsNoTracking(), album => album.ArtistId, artist => artist.Id, (album, artist) => new { album.WorkId, ArtistName = artist.Name })
            .Join(db.Works.AsNoTracking(), item => item.WorkId, work => work.Id, (item, work) => new KnownAlbum(item.WorkId, work.CanonicalTitle, item.ArtistName, work.Year))
            .ToListAsync(cancellationToken);
        var imported = 0;
        var skipped = new List<string>();
        foreach (var folder in Directory.EnumerateDirectories(inboxRoot).Order(StringComparer.Ordinal))
        {
            if (excludedFolders.Any(excluded => LibraryFilePlacer.SamePath(excluded, folder)))
            {
                continue;
            }

            var name = MusicReleaseParser.ReadableName(Path.GetFileName(folder));
            var matches = known.Where(album => NameIdentifies(name, album)).ToArray();
            var files = CompletedDownloadFiles.Enumerate(folder, out var error).Where(file => MusicTrackMatcher.IsAudio(file.Path)).ToArray();
            if (error is not null || files.Length == 0)
            {
                continue;
            }

            if (matches.Length != 1)
            {
                skipped.Add($"'{Path.GetFileName(folder)}' does not identify exactly one known album.");
                continue;
            }

            try
            {
                var outcome = await PlaceAlbumAsync(matches[0], files, Path.GetFileName(folder), route, cancellationToken);
                if (outcome.Rejection is { } reason)
                {
                    skipped.Add($"'{Path.GetFileName(folder)}': {reason}");
                    continue;
                }

                imported++;
            }
            catch (Exception exception) when (exception is DestinationMismatchException or MusicMetadataException)
            {
                skipped.Add($"'{Path.GetFileName(folder)}': {exception.Message}");
            }
        }

        var message = imported == 0 ? "No new albums in the inbox." : $"Imported {imported} album(s) from the inbox.";
        return new MediaInboxImportResult(imported, skipped.Count == 0 ? message : $"{message} Skipped: {string.Join(" ", skipped)}");
    }

    private static bool NameIdentifies(string folderName, KnownAlbum album)
    {
        var words = folderName.Split([' ', '-', '(', ')', '[', ']', '.', '_'], StringSplitOptions.RemoveEmptyEntries).Select(word => word.ToLowerInvariant()).ToHashSet();
        bool Covers(string text) => text.Split([' ', '-', '(', ')', '[', ']', '.', '_', ':', ',', '\''], StringSplitOptions.RemoveEmptyEntries).Select(word => word.ToLowerInvariant()).Where(word => word.Length > 1).All(words.Contains);
        return Covers(album.Title) && Covers(album.Artist);
    }

    /// <summary>
    /// Places the album's tracks from <paramref name="files"/>. A rejection reason means the release is not this album and nothing was
    /// placed. The records of the placed files are written after the files; a failure while recording puts moved sources back, so a retry
    /// finds them in the download folder.
    /// </summary>
    private async Task<AlbumPlacement> PlaceAlbumAsync(KnownAlbum album, IReadOnlyList<CompletedDownloadFile> files, string releaseName, LibraryRootRoute route, CancellationToken cancellationToken)
    {
        await library.EnsureTracksAsync(album.WorkId, cancellationToken);
        var tracks = await db.WorkTracks.Where(track => track.WorkId == album.WorkId).OrderBy(track => track.Disc).ThenBy(track => track.Number).ToListAsync(cancellationToken);
        if (tracks.Count == 0)
        {
            tracks = [.. MusicTrackMatcher.Synthesize(album.WorkId, files)];
            db.WorkTracks.AddRange(tracks);
            await db.SaveChangesAsync(cancellationToken);
        }

        var matches = MusicTrackMatcher.Match(files, tracks).Where(match => match.Track is not null).ToArray();
        var folder = Path.Combine(route.Path, MusicNaming.ArtistFolder(album.Artist), MusicNaming.AlbumFolder(album.Title, album.Year));
        if (matches.Length < Math.Max(1, (int)Math.Ceiling(tracks.Count * RequiredTrackShare)))
        {
            return new AlbumPlacement(0, folder, $"Only {matches.Length} of {tracks.Count} tracks of the album were found in the download.");
        }

        // What is installed decides what this release may do: replace it (a complete, meaningfully better album), fill its gaps, or nothing.
        var incomingQuality = MusicReleaseParser.DetectQuality(releaseName) ?? (matches.All(match => MusicInstalledQuality.OfExtension(match.File.Path) == "FLAC") ? "FLAC" : null);
        var installed = await canonicalStorage.ListAudioFilesAsync(album.WorkId, cancellationToken);
        var profile = profiles is null || installed.Count == 0 ? null : await profiles.ResolveAsync(MediaAcquisitionKind.Music, album.WorkId, cancellationToken);
        var improves = profile is not null && UpgradePolicy.IsUpgrade(profile, MusicInstalledQuality.OfAlbum(profile, installed), incomingQuality);
        var isUpgrade = improves && matches.Length == tracks.Count;
        if (improves && !isUpgrade)
        {
            return new AlbumPlacement(0, folder, $"Only {matches.Length} of {tracks.Count} tracks are in the download, so it cannot replace the complete album in the library.");
        }

        var (action, allowFallback) = ImportFileTransfer.Resolve(ImportFileTransfer.ModeFor(route.PlacementPolicy));
        var multiDisc = tracks.Select(track => track.Disc).Distinct().Count() > 1;
        var placer = new LibraryFilePlacer(new ImportFileTransfer(hardLinks));
        var plan = matches
            .Select(match => (match.File, Track: match.Track!, Destination: Path.Combine(folder, MusicNaming.TrackFileName(match.Track!.Disc, match.Track.Number, multiDisc, match.Track.Title, Path.GetExtension(match.File.Path)))))
            .Where(item => isUpgrade || installed.All(file => file.WorkTrackId != item.Track.Id))
            .ToArray();
        foreach (var item in plan)
        {
            if (!StoragePaths.IsBelow(item.Destination, route.Path))
            {
                throw new InvalidOperationException("The album destination would leave its library root.");
            }

            if (!isUpgrade && File.Exists(item.Destination) && !LibraryFilePlacer.IsCompletePlacement(item.File.Path, item.Destination))
            {
                throw new DestinationMismatchException(item.Destination);
            }
        }

        var moved = new List<(string Source, string Destination)>();
        var replaced = new List<ReplacedLibraryFile>();
        try
        {
            foreach (var item in plan)
            {
                if (File.Exists(item.Destination))
                {
                    // A better release takes the destination of the file it replaces; the old file stays until the new one is recorded.
                    if (!isUpgrade || LibraryFilePlacer.IsCompletePlacement(item.File.Path, item.Destination))
                    {
                        continue;
                    }

                    replaced.Add(ReplacedLibraryFile.SetAside(item.Destination));
                }

                placer.Place(new LibraryFilePlacement(item.File.Path, item.Destination, action, allowFallback, [], []));
                if (action == ImportFileAction.Move)
                {
                    moved.Add((item.File.Path, item.Destination));
                }
            }

            await canonicalStorage.AttachAudiosAsync([.. plan.Select(item => new CanonicalAudioAttachment(album.WorkId, item.Track.Id, Path.GetFullPath(item.Destination), incomingQuality))], cancellationToken);
        }
        catch
        {
            foreach (var (source, destination) in moved)
            {
                LibraryFilePlacer.RestoreMovedSource(source, destination);
            }

            foreach (var file in replaced)
            {
                file.Rollback();
            }

            throw;
        }

        foreach (var file in replaced)
        {
            file.Commit();
        }

        if (isUpgrade)
        {
            var kept = plan.Select(item => Path.GetFullPath(item.Destination)).ToArray();
            await canonicalStorage.RemoveFilesAsync([.. installed.Where(file => !kept.Any(path => LibraryFilePlacer.SamePath(path, file.Path))).Select(file => file.StoredFileId)], cancellationToken);
        }

        return new AlbumPlacement(plan.Length, folder, null);
    }

    private async Task<KnownAlbum?> LoadAlbumAsync(Guid? workId, string releaseGroupId, CancellationToken cancellationToken)
    {
        var query = from album in db.MusicAlbums.AsNoTracking()
                    join artist in db.MusicArtists.AsNoTracking() on album.ArtistId equals artist.Id
                    join work in db.Works.AsNoTracking() on album.WorkId equals work.Id
                    where workId != null ? album.WorkId == workId : album.MusicBrainzReleaseGroupId == releaseGroupId
                    select new KnownAlbum(album.WorkId, work.CanonicalTitle, artist.Name, work.Year);
        return await query.FirstOrDefaultAsync(cancellationToken);
    }

    private sealed record KnownAlbum(Guid WorkId, string Title, string Artist, int? Year);

    private sealed record AlbumPlacement(int Placed, string Folder, string? Rejection);
}
