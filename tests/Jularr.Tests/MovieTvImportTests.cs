using Jularr.Web.Data;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.Import;
using Jularr.Web.Features.Acquisition.Release;
using Jularr.Web.Features.MediaCore;
using Jularr.Web.Features.Library;
using Jularr.Web.Features.Movies;
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

    private static async Task<AnimeImportSettingsStore> SettingsWithLibraryAsync(
        string dataRoot, MediaAcquisitionKind kind, string libraryRoot)
    {
        var store = new AnimeImportSettingsStore(dataRoot);
        await store.UpdateAsync(state => state with
        {
            DefaultImportMode = ImportMode.Copy,
            MediaLibraries = new Dictionary<MediaAcquisitionKind, MediaLibraryTarget>
            {
                [kind] = new MediaLibraryTarget(LibraryRoot: libraryRoot)
            }
        });
        return store;
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
        var settings = await SettingsWithLibraryAsync(temp.Root, MediaAcquisitionKind.Movie, library);

        var adapter = new MovieCompletedDownloadImportAdapter(
            new MovieLibraryService(db, Bridge(db)),
            Registry(),
            settings,
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
        var settings = await SettingsWithLibraryAsync(temp.Root, MediaAcquisitionKind.Tv, library);

        var adapter = new TvCompletedDownloadImportAdapter(
            new TvLibraryService(db, Bridge(db), new WorkStructureService(db)),
            Registry(),
            settings,
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
    public async Task MovieInboxImportsEachVideo()
    {
        await using var db = await MediaCoreTestSupport.CreateDbAsync();
        using var temp = new TempWorkspace();
        var inbox = temp.Dir("inbox");
        File.WriteAllText(Path.Combine(inbox, "The Matrix (1999).mkv"), "video");

        var adapter = new MovieCompletedDownloadImportAdapter(
            new MovieLibraryService(db, Bridge(db)),
            Registry(),
            new AnimeImportSettingsStore(temp.Root),
            new FileSystemHardLinkCreator(),
            NullLogger<MovieCompletedDownloadImportAdapter>.Instance);

        var result = await adapter.ImportInboxAsync(inbox, [], CancellationToken.None);

        Assert.AreEqual(1, result.Imported);
        Assert.AreEqual(1, await db.Movies.CountAsync());
        Assert.AreEqual(WorkMediaType.Movie, (await db.Works.SingleAsync()).MediaType);
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
