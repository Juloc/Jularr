using Jularr.Web.Data;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.Import;
using Jularr.Web.Features.Acquisition.Release;
using Jularr.Web.Features.MediaCore;
using Jularr.Web.Features.Library;
using Jularr.Web.Features.Movies;
using Jularr.Web.Features.Storage;
using Jularr.Web.Features.Tv;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jularr.Tests;

/// <summary>
/// The Movie (#593) and TV (#594) completed-download and inbox importers place video files into the
/// per-kind library with the right naming and record a first-class entity bridged to the media core.
/// </summary>
[TestClass]
public sealed class MovieTvImportTests
{
    private static MediaAcquisitionRegistry Registry() =>
        new([new MovieAcquisitionRegistration(), new TvAcquisitionRegistration()]);

    internal static async Task<LibraryRootRoutingService> RoutingWithDefaultAsync(
        AppDbContext db, LibraryContentType contentType, string libraryRoot, LibraryPlacementPolicy policy = LibraryPlacementPolicy.Copy)
    {
        var root = new LibraryRoot { Name = contentType.ToString(), Path = libraryRoot, PlacementPolicy = policy };
        db.LibraryRoots.Add(root);
        await db.SaveChangesAsync();
        var routing = new LibraryRootRoutingService(db);
        await routing.SetSupportedAsync(root.Id, contentType, true);
        await routing.SetDefaultAsync(contentType, root.Id);
        return routing;
    }

    [TestMethod]
    public async Task MovieImportPlacesVideoWithSidecarAndBridgesToCore()
    {
        await using var db = await MediaCoreTestSupport.CreateDbAsync();
        using var temp = new TempWorkspace();
        var download = temp.Dir("download");
        File.WriteAllText(Path.Combine(download, "Inception.2010.1080p.BluRay.x264-GROUP.mkv"), "video");
        File.WriteAllText(Path.Combine(download, "Inception.2010.1080p.BluRay.x264-GROUP.en.srt"), "subs");
        var library = temp.Dir("library");
        var routing = await RoutingWithDefaultAsync(db, LibraryContentType.Movie, library);

        var adapter = new MovieCompletedDownloadImportAdapter(
            new MovieLibraryService(db, Bridge(db)),
            Registry(),
            routing,
            new FileSystemHardLinkCreator(),
            NullLogger<MovieCompletedDownloadImportAdapter>.Instance,
            new CanonicalMediaStorageService(db));

        var result = await adapter.ImportAsync(
            new CompletedDownloadImportRequest(null, null, download, MediaAcquisitionKind.Movie), CancellationToken.None);

        Assert.AreEqual(CompletedDownloadImportDisposition.Completed, result.Disposition);
        var movie = await db.Movies.SingleAsync();
        Assert.AreEqual(2010, movie.Year);

        var videos = Directory.GetFiles(library, "*.mkv", SearchOption.AllDirectories);
        Assert.AreEqual(1, videos.Length);
        Assert.AreEqual(1, Directory.GetFiles(library, "*.srt", SearchOption.AllDirectories).Length, "The subtitle sidecar follows the video.");

        var work = await db.Works.SingleAsync();
        Assert.AreEqual(WorkMediaType.Movie, work.MediaType);
        Assert.AreEqual(1, await db.WorkSourceLinks.CountAsync(x => x.SourceKind == WorkSourceKind.Movie && x.SourceId == movie.Id));

        var movieAsset = await db.MediaAssets.SingleAsync();
        Assert.AreEqual(work.Id, movieAsset.WorkId);
        Assert.IsNull(movieAsset.WorkEpisodeId);
        var movieFile = await db.StoredFiles.SingleAsync();
        Assert.AreEqual(movieAsset.Id, movieFile.MediaAssetId);
    }

    [TestMethod]
    public async Task TvImportPlacesEpisodeInSeasonFolderAndRecordsStructure()
    {
        await using var db = await MediaCoreTestSupport.CreateDbAsync();
        using var temp = new TempWorkspace();
        var download = temp.Dir("download");
        File.WriteAllText(Path.Combine(download, "Breaking.Bad.S01E02.1080p.BluRay.x264-GROUP.mkv"), "video");
        var library = temp.Dir("library");
        var routing = await RoutingWithDefaultAsync(db, LibraryContentType.Tv, library);

        var adapter = new TvCompletedDownloadImportAdapter(
            new TvLibraryService(db, Bridge(db), new WorkStructureService(db)),
            Registry(),
            routing,
            new FileSystemHardLinkCreator(),
            NullLogger<TvCompletedDownloadImportAdapter>.Instance,
            new CanonicalMediaStorageService(db));

        var result = await adapter.ImportAsync(
            new CompletedDownloadImportRequest(null, null, download, MediaAcquisitionKind.Tv), CancellationToken.None);

        Assert.AreEqual(CompletedDownloadImportDisposition.Completed, result.Disposition);
        Assert.AreEqual(1, await db.TvSeries.CountAsync());

        var videos = Directory.GetFiles(library, "*.mkv", SearchOption.AllDirectories);
        Assert.AreEqual(1, videos.Length);
        Assert.AreEqual("Season 01", Path.GetFileName(Path.GetDirectoryName(videos[0])));

        var work = await db.Works.SingleAsync();
        Assert.AreEqual(WorkMediaType.Series, work.MediaType);
        var episode = await db.WorkEpisodes.SingleAsync(x => x.WorkId == work.Id);
        Assert.AreEqual(1, episode.SeasonNumber);
        Assert.AreEqual(2, episode.EpisodeNumber);

        var tvAsset = await db.MediaAssets.SingleAsync();
        Assert.AreEqual(work.Id, tvAsset.WorkId);
        Assert.AreEqual(episode.Id, tvAsset.WorkEpisodeId);
        var tvFile = await db.StoredFiles.SingleAsync();
        Assert.AreEqual(tvAsset.Id, tvFile.MediaAssetId);
    }

    [TestMethod]
    public async Task MovieLibraryReusesProviderIdentityWhenRequestYearIsMissing()
    {
        await using var db = await MediaCoreTestSupport.CreateDbAsync();
        var existing = new Movie
        {
            Key = MovieLibraryService.MovieKey("Dune", 2021),
            Title = "Dune",
            Year = 2021,
            TmdbId = "438631"
        };
        db.Movies.Add(existing);
        await db.SaveChangesAsync();

        var resolved = await new MovieLibraryService(db, Bridge(db)).EnsureAsync(
            "Dune",
            year: null,
            tmdbId: "438631",
            imdbId: null,
            libraryPath: null,
            CancellationToken.None);

        Assert.AreEqual(existing.Id, resolved.Movie.Id);
        Assert.AreEqual(1, await db.Movies.CountAsync());
        Assert.AreEqual(2021, resolved.Movie.Year);
    }

    [TestMethod]
    public async Task TvLibraryReusesProviderIdentityWhenRequestYearIsMissing()
    {
        await using var db = await MediaCoreTestSupport.CreateDbAsync();
        var existing = new TvSeries
        {
            Key = TvLibraryService.SeriesKey("Severance", 2022),
            Title = "Severance",
            Year = 2022,
            TmdbId = "95396"
        };
        db.TvSeries.Add(existing);
        await db.SaveChangesAsync();

        var resolved = await new TvLibraryService(db, Bridge(db), new WorkStructureService(db)).EnsureSeriesAsync(
            "Severance",
            year: null,
            tmdbId: "95396",
            tvdbId: null,
            libraryPath: null,
            CancellationToken.None);

        Assert.AreEqual(existing.Id, resolved.Series.Id);
        Assert.AreEqual(1, await db.TvSeries.CountAsync());
        Assert.AreEqual(2022, resolved.Series.Year);
    }

    [TestMethod]
    public async Task MovieInboxPlacesEachVideoIntoTheDefaultRootAndIsIdempotent()
    {
        await using var db = await MediaCoreTestSupport.CreateDbAsync();
        using var temp = new TempWorkspace();
        var inbox = temp.Dir("inbox");
        var library = temp.Dir("library");
        File.WriteAllText(Path.Combine(inbox, "Inception.2010.1080p.BluRay.x264-GROUP.mkv"), "video");
        var routing = await RoutingWithDefaultAsync(db, LibraryContentType.Movie, library);

        var adapter = new MovieCompletedDownloadImportAdapter(
            new MovieLibraryService(db, Bridge(db)),
            Registry(),
            routing,
            new FileSystemHardLinkCreator(),
            NullLogger<MovieCompletedDownloadImportAdapter>.Instance,
            new CanonicalMediaStorageService(db));

        var result = await adapter.ImportInboxAsync(inbox, [], CancellationToken.None);

        Assert.AreEqual(1, result.Imported);
        Assert.AreEqual(1, await db.Movies.CountAsync());
        Assert.AreEqual(WorkMediaType.Movie, (await db.Works.SingleAsync()).MediaType);
        var placed = Path.Combine(library, "Inception (2010)", "Inception (2010).mkv");
        Assert.IsTrue(File.Exists(placed), "The inbox video is placed into the default Movie root. Found: " + string.Join(", ", Directory.GetFiles(library, "*", SearchOption.AllDirectories)));
        Assert.IsTrue(File.Exists(Path.Combine(inbox, "Inception.2010.1080p.BluRay.x264-GROUP.mkv")), "The Copy policy of the root keeps the inbox source.");

        await adapter.ImportInboxAsync(inbox, [], CancellationToken.None);
        Assert.AreEqual(1, await db.Movies.CountAsync(), "A second scan recognizes the file instead of importing it again.");
        Assert.AreEqual(1, await db.StoredFiles.CountAsync());
    }

    private static LegacyWorkBridge Bridge(AppDbContext db) =>
        new(db, new WorkService(db), new WorkStructureService(db));

    private sealed class TempWorkspace : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), $"jularr-movietv-{Guid.NewGuid():N}");

        public TempWorkspace() => Directory.CreateDirectory(Root);

        public string Dir(string name)
        {
            var path = Path.Combine(Root, name);
            Directory.CreateDirectory(path);
            return path;
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup of the temp workspace.
            }
        }
    }
}
