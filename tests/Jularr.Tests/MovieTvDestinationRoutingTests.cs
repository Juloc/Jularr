using Jularr.Web.Data;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.Import;
using Jularr.Web.Features.Acquisition.Release;
using Jularr.Web.Features.Library;
using Jularr.Web.Features.MediaCore;
using Jularr.Web.Features.Movies;
using Jularr.Web.Features.Storage;
using Jularr.Web.Features.Tv;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jularr.Tests;

/// <summary>
/// The Movie and TV importers resolve their destination and placement policy through the canonical LibraryRoot routing of Storage (#815):
/// the default root of the content type is used, a missing or unavailable one makes the import wait with an actionable message, and no
/// legacy per-media folder or in-place fallback exists. The one-time move of an old per-media folder into Storage never moves media.
/// </summary>
[TestClass]
public sealed class MovieTvDestinationRoutingTests
{
    private const string Inception = "Inception.2010.1080p.BluRay.x264-GROUP.mkv";
    private const string BreakingBad = "Breaking.Bad.S01E02.1080p.BluRay.x264-GROUP.mkv";

    private static MediaAcquisitionRegistry Registry() => new([new MovieAcquisitionRegistration(), new TvAcquisitionRegistration()]);

    private static LegacyWorkBridge Bridge(AppDbContext db) => new(db, new WorkService(db), new WorkStructureService(db));

    private static MovieCompletedDownloadImportAdapter MovieAdapter(AppDbContext db) =>
        new(new MovieLibraryService(db, Bridge(db)), Registry(), new LibraryRootRoutingService(db), new FileSystemHardLinkCreator(), NullLogger<MovieCompletedDownloadImportAdapter>.Instance, new CanonicalMediaStorageService(db));

    private static TvCompletedDownloadImportAdapter TvAdapter(AppDbContext db)
    {
        var series = new TvLibraryService(db, Bridge(db), new WorkStructureService(db));
        return new(series, Registry(), new LibraryRootRoutingService(db), new FileSystemHardLinkCreator(), NullLogger<TvCompletedDownloadImportAdapter>.Instance, new CanonicalMediaStorageService(db));
    }

    private static CompletedDownloadImportRequest Download(string path, MediaAcquisitionKind kind) => new(null, null, path, kind);

    [TestMethod]
    public async Task MovieImportUsesTheDefaultRootAndItsPlacementPolicy()
    {
        await using var db = await MediaCoreTestSupport.CreateDbAsync();
        using var temp = new TempFolders();
        var download = temp.Dir("download");
        var source = Path.Combine(download, Inception);
        File.WriteAllText(source, "video");
        var library = temp.Dir("library");
        await MovieTvImportTests.RoutingWithDefaultAsync(db, LibraryContentType.Movie, library, LibraryPlacementPolicy.Move);

        var result = await MovieAdapter(db).ImportAsync(Download(download, MediaAcquisitionKind.Movie), CancellationToken.None);

        Assert.AreEqual(CompletedDownloadImportDisposition.Completed, result.Disposition);
        Assert.AreEqual(ImportMode.Move, result.Placement!.Mode, "The root's policy, not a per-media setting, decides how the file is placed.");
        Assert.IsTrue(File.Exists(Path.Combine(library, "Inception (2010)", "Inception (2010).mkv")));
        Assert.IsFalse(File.Exists(source), "Move removes the source after placement.");
        var stored = await db.StoredFiles.SingleAsync();
        var root = await db.LibraryRoots.SingleAsync();
        Assert.AreEqual(root.Id, stored.LibraryRootId, "The canonical file belongs to the routed root.");
    }

    [TestMethod]
    public async Task TvImportUsesTheDefaultRootAndKeepsTheSourceWithCopyPolicy()
    {
        await using var db = await MediaCoreTestSupport.CreateDbAsync();
        using var temp = new TempFolders();
        var download = temp.Dir("download");
        var source = Path.Combine(download, BreakingBad);
        File.WriteAllText(source, "video");
        var library = temp.Dir("library");
        await MovieTvImportTests.RoutingWithDefaultAsync(db, LibraryContentType.Tv, library, LibraryPlacementPolicy.Copy);

        var result = await TvAdapter(db).ImportAsync(Download(download, MediaAcquisitionKind.Tv), CancellationToken.None);

        Assert.AreEqual(CompletedDownloadImportDisposition.Completed, result.Disposition);
        Assert.AreEqual(ImportMode.Copy, result.Placement!.Mode);
        Assert.AreEqual(1, Directory.GetFiles(library, "*.mkv", SearchOption.AllDirectories).Length);
        Assert.IsTrue(File.Exists(source), "Copy keeps the source.");
    }

    [TestMethod]
    public async Task WithoutADefaultRootNothingIsImportedAndTheOwnerGetsAnActionableReason()
    {
        await using var db = await MediaCoreTestSupport.CreateDbAsync();
        using var temp = new TempFolders();
        var download = temp.Dir("download");
        File.WriteAllText(Path.Combine(download, Inception), "video");
        File.WriteAllText(Path.Combine(download, BreakingBad), "video");

        var movie = await MovieAdapter(db).ImportAsync(Download(download, MediaAcquisitionKind.Movie), CancellationToken.None);
        var tv = await TvAdapter(db).ImportAsync(Download(download, MediaAcquisitionKind.Tv), CancellationToken.None);

        foreach (var result in new[] { movie, tv })
        {
            Assert.AreEqual(CompletedDownloadImportDisposition.RetryLater, result.Disposition, "The import waits instead of failing the release or guessing a folder.");
            StringAssert.Contains(result.Message, "Admin → Storage");
        }

        Assert.AreEqual(0, await db.Movies.CountAsync(), "Nothing is recorded or read in place.");
        Assert.AreEqual(0, await db.TvSeries.CountAsync());
        Assert.AreEqual(0, await db.StoredFiles.CountAsync());
        Assert.AreEqual(0, await db.LibraryRoots.CountAsync(), "No implicit root is invented for the download folder.");
    }

    [TestMethod]
    public async Task ADefaultOfAnotherContentTypeOrADisabledRootIsNotUsed()
    {
        await using var db = await MediaCoreTestSupport.CreateDbAsync();
        using var temp = new TempFolders();
        var download = temp.Dir("download");
        File.WriteAllText(Path.Combine(download, Inception), "video");
        var library = temp.Dir("library");
        await MovieTvImportTests.RoutingWithDefaultAsync(db, LibraryContentType.Tv, library);

        var otherType = await MovieAdapter(db).ImportAsync(Download(download, MediaAcquisitionKind.Movie), CancellationToken.None);
        Assert.AreEqual(CompletedDownloadImportDisposition.RetryLater, otherType.Disposition, "A TV default never receives Movies.");

        var movieRoot = new LibraryRoot { Name = "Movies", Path = temp.Dir("movies") };
        db.LibraryRoots.Add(movieRoot);
        await db.SaveChangesAsync();
        var routing = new LibraryRootRoutingService(db);
        await routing.SetSupportedAsync(movieRoot.Id, LibraryContentType.Movie, true);
        await routing.SetDefaultAsync(LibraryContentType.Movie, movieRoot.Id);
        movieRoot.IsEnabled = false;
        await db.SaveChangesAsync();

        var disabled = await MovieAdapter(db).ImportAsync(Download(download, MediaAcquisitionKind.Movie), CancellationToken.None);
        Assert.AreEqual(CompletedDownloadImportDisposition.RetryLater, disabled.Disposition);
        Assert.AreEqual(0, Directory.GetFiles(movieRoot.Path, "*", SearchOption.AllDirectories).Length);
    }

    [TestMethod]
    public async Task AnUnavailableRootWaitsWithoutCreatingFoldersOnAnUnmountedPath()
    {
        await using var db = await MediaCoreTestSupport.CreateDbAsync();
        using var temp = new TempFolders();
        var download = temp.Dir("download");
        File.WriteAllText(Path.Combine(download, Inception), "video");
        var unmounted = Path.Combine(temp.Root, "not-mounted");
        await MovieTvImportTests.RoutingWithDefaultAsync(db, LibraryContentType.Movie, unmounted);

        var result = await MovieAdapter(db).ImportAsync(Download(download, MediaAcquisitionKind.Movie), CancellationToken.None);

        Assert.AreEqual(CompletedDownloadImportDisposition.RetryLater, result.Disposition);
        StringAssert.Contains(result.Message, "not available");
        Assert.IsFalse(Directory.Exists(unmounted));
    }

    [TestMethod]
    public async Task TvInboxPlacesEpisodesIntoTheDefaultRootAndRecognizesThemOnTheNextScan()
    {
        await using var db = await MediaCoreTestSupport.CreateDbAsync();
        using var temp = new TempFolders();
        var inbox = temp.Dir("inbox");
        File.WriteAllText(Path.Combine(inbox, BreakingBad), "video");
        var library = temp.Dir("library");
        await MovieTvImportTests.RoutingWithDefaultAsync(db, LibraryContentType.Tv, library);

        var first = await TvAdapter(db).ImportInboxAsync(inbox, [], CancellationToken.None);
        var second = await TvAdapter(db).ImportInboxAsync(inbox, [], CancellationToken.None);

        Assert.AreEqual(1, first.Imported);
        Assert.AreEqual(1, second.Imported);
        Assert.AreEqual(1, await db.TvSeries.CountAsync());
        Assert.AreEqual(1, await db.WorkEpisodes.CountAsync());
        Assert.AreEqual(1, await db.StoredFiles.CountAsync(), "A second scan refreshes the record instead of importing again.");
        Assert.AreEqual("Season 01", Path.GetFileName(Path.GetDirectoryName(Directory.GetFiles(library, "*.mkv", SearchOption.AllDirectories).Single())));
    }

    [TestMethod]
    public async Task InboxScansWithoutADefaultRootImportNothingAndSayWhy()
    {
        await using var db = await MediaCoreTestSupport.CreateDbAsync();
        using var temp = new TempFolders();
        var inbox = temp.Dir("inbox");
        File.WriteAllText(Path.Combine(inbox, Inception), "video");
        File.WriteAllText(Path.Combine(inbox, BreakingBad), "video");

        var movie = await MovieAdapter(db).ImportInboxAsync(inbox, [], CancellationToken.None);
        var tv = await TvAdapter(db).ImportInboxAsync(inbox, [], CancellationToken.None);

        Assert.AreEqual(0, movie.Imported);
        Assert.AreEqual(0, tv.Imported);
        StringAssert.Contains(movie.Message, "Admin → Storage");
        StringAssert.Contains(tv.Message, "Admin → Storage");
        Assert.AreEqual(0, await db.StoredFiles.CountAsync(), "An inbox is never registered in place.");
    }

    [TestMethod]
    public async Task RoutingNeverLetsAMovieRootBeScannedAsAnAnimeLibrary()
    {
        await using var db = await MediaCoreTestSupport.CreateDbAsync();
        using var temp = new TempFolders();
        var legacyAnime = new LibraryRoot { Name = "Anime", Path = temp.Dir("anime") };
        var movieOnly = new LibraryRoot { Name = "Movies", Path = temp.Dir("movies") };
        var shared = new LibraryRoot { Name = "Shared", Path = temp.Dir("shared") };
        db.LibraryRoots.AddRange(legacyAnime, movieOnly, shared);
        await db.SaveChangesAsync();
        var routing = new LibraryRootRoutingService(db);
        await routing.SetSupportedAsync(movieOnly.Id, LibraryContentType.Movie, true);
        await routing.SetSupportedAsync(shared.Id, LibraryContentType.Movie, true);
        await routing.SetSupportedAsync(shared.Id, LibraryContentType.Anime, true);

        var scannable = await db.LibraryRoots.ServingAnime(db).Select(root => root.Name).OrderBy(name => name).ToListAsync();

        CollectionAssert.AreEqual(new[] { "Anime", "Shared" }, scannable, "Roots without assignments stay Anime libraries; a Movie-only root is not one.");
    }

    [TestMethod]
    public async Task TheAnimeScannerSkipsARootStorageRoutesOnlyToMovies()
    {
        await using var host = await LibraryScanTestHost.CreateAsync();
        var animeRoot = await host.AddRootAsync("Anime");
        var movieFolder = Path.Combine(host.TempRoot, "movies");
        Directory.CreateDirectory(Path.Combine(movieFolder, "Inception (2010)"));
        File.WriteAllBytes(Path.Combine(movieFolder, "Inception (2010)", "Inception (2010).mkv"), [0x00]);
        var movieRoot = await host.AddRootAsync("Movies", movieFolder);
        host.WriteMedia(Path.Combine("Frieren", "Season 01", "Frieren - S01E01.mkv"));
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await new LibraryRootRoutingService(db).SetSupportedAsync(movieRoot.Id, LibraryContentType.Movie, true);
        var scanner = scope.ServiceProvider.GetRequiredService<LibraryScanner>();

        var movieScan = await scanner.ScanAsync(movieRoot.Id, CancellationToken.None);
        Assert.AreEqual(0, movieScan.Discovered);
        Assert.AreEqual(0, await db.Anime.CountAsync(), "Movie folders do not become anime.");

        var animeScan = await scanner.ScanAsync(animeRoot.Id, CancellationToken.None);
        Assert.AreEqual(1, animeScan.Discovered, "A root without assignments is still an Anime library.");
    }

    [TestMethod]
    public async Task OneTimeMigrationMovesTheLegacyFolderAndImportModeIntoStorageWithoutMovingMedia()
    {
        await using var db = await MediaCoreTestSupport.CreateDbAsync();
        using var temp = new TempFolders();
        var movies = temp.Dir("movies");
        var series = temp.Dir("series");
        File.WriteAllText(Path.Combine(movies, "existing.mkv"), "video");
        var store = new AnimeImportSettingsStore(temp.Root);
        await store.UpdateAsync(state => state with
        {
            Version = MediaFolderSettingsMigration.MediaFoldersVersion,
            DefaultImportMode = ImportMode.Copy,
            MediaLibraries = new Dictionary<MediaAcquisitionKind, MediaLibraryTarget>
            {
                [MediaAcquisitionKind.Movie] = new(LibraryRoot: movies, ImportMode: ImportMode.Hardlink, InboxRoot: "/inbox/movies"),
                [MediaAcquisitionKind.Tv] = new(LibraryRoot: series)
            }
        });
        var routing = new LibraryRootRoutingService(db);

        var migrated = await VideoLibraryRootMigration.MigrateAsync(store, db, routing);

        Assert.AreEqual(2, migrated);
        var movieRoute = await routing.ResolveDefaultAsync(LibraryContentType.Movie);
        Assert.AreEqual(Path.GetFullPath(movies), movieRoute!.Path);
        Assert.AreEqual(LibraryPlacementPolicy.Hardlink, movieRoute.PlacementPolicy, "The per-media import mode becomes the root's placement policy.");
        var tvRoute = await routing.ResolveDefaultAsync(LibraryContentType.Tv);
        Assert.AreEqual(LibraryPlacementPolicy.Copy, tvRoute!.PlacementPolicy, "Without an override the global default mode applies.");
        Assert.IsTrue(File.Exists(Path.Combine(movies, "existing.mkv")), "No media is moved.");

        var state = await store.LoadAsync();
        Assert.AreEqual(VideoLibraryRootMigration.CanonicalVideoRootsVersion, state.Version);
        Assert.IsNull(state.LibraryFor(MediaAcquisitionKind.Movie), "Nothing reads the legacy folder afterwards.");
        Assert.AreEqual("/inbox/movies", state.InboxFor(MediaAcquisitionKind.Movie), "The inbox is not part of the move.");
        Assert.IsFalse(state.MediaLibraries.ContainsKey(MediaAcquisitionKind.Tv), "An entry left without any setting is dropped.");

        Assert.AreEqual(0, await VideoLibraryRootMigration.MigrateAsync(store, db, routing), "The migration runs once.");
        Assert.AreEqual(2, await db.LibraryRoots.CountAsync());
    }

    [TestMethod]
    public async Task MigrationReusesAnExistingRootAdoptsAnImplicitOneAndKeepsAnOwnerChosenDefault()
    {
        await using var db = await MediaCoreTestSupport.CreateDbAsync();
        using var temp = new TempFolders();
        var movies = temp.Dir("movies");
        var series = temp.Dir("series");
        var chosen = new LibraryRoot { Name = "Chosen", Path = temp.Dir("chosen"), PlacementPolicy = LibraryPlacementPolicy.Copy };
        var implicitRoot = new LibraryRoot { Name = "Imported media: series", Path = series, IsEnabled = false };
        db.LibraryRoots.AddRange(chosen, implicitRoot);
        await db.SaveChangesAsync();
        var routing = new LibraryRootRoutingService(db);
        await routing.SetSupportedAsync(chosen.Id, LibraryContentType.Movie, true);
        await routing.SetDefaultAsync(LibraryContentType.Movie, chosen.Id);
        var store = new AnimeImportSettingsStore(temp.Root);
        await store.UpdateAsync(state => state with
        {
            Version = MediaFolderSettingsMigration.MediaFoldersVersion,
            DefaultImportMode = ImportMode.Move,
            MediaLibraries = new Dictionary<MediaAcquisitionKind, MediaLibraryTarget>
            {
                [MediaAcquisitionKind.Movie] = new(LibraryRoot: movies),
                [MediaAcquisitionKind.Tv] = new(LibraryRoot: series)
            }
        });

        await VideoLibraryRootMigration.MigrateAsync(store, db, routing);

        Assert.AreEqual(chosen.Id, (await routing.ResolveDefaultAsync(LibraryContentType.Movie))!.LibraryRootId, "A default the owner chose in Storage is never replaced.");
        Assert.AreEqual(2, (await routing.ListAsync(LibraryContentType.Movie)).Count, "The old folder is still offered as a supporting root.");
        var tv = await routing.ResolveDefaultAsync(LibraryContentType.Tv);
        Assert.AreEqual(implicitRoot.Id, tv!.LibraryRootId, "A disabled root created by an older in-place import is adopted instead of duplicated.");
        Assert.AreEqual(LibraryPlacementPolicy.Move, tv.PlacementPolicy);
        Assert.AreEqual(3, await db.LibraryRoots.CountAsync());
    }

    [TestMethod]
    public void NoRuntimeCodeReadsAPerMediaLibraryFolderOrImportModeForMovieOrTv()
    {
        var sourceRoot = Path.Combine(FindRepositoryRoot(), "src", "Jularr.Web");
        var legacyRead = new System.Text.RegularExpressions.Regex(@"\.(LibraryFor|ModeFor)\(\s*MediaAcquisitionKind\.(Movie|Tv)\b");
        var offenders = Directory
            .EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(sourceRoot, path).Replace('\\', '/'))
            .Where(relative => !relative.StartsWith("obj/", StringComparison.Ordinal) && !relative.StartsWith("bin/", StringComparison.Ordinal))
            .Where(relative => legacyRead.IsMatch(File.ReadAllText(Path.Combine(sourceRoot, relative))))
            .ToArray();

        CollectionAssert.AreEqual(Array.Empty<string>(), offenders, "The Movie and TV destination and placement policy are owned by LibraryRoot routing, not by the per-media import settings.");
        foreach (var adapter in new[] { "Features/Movies/MovieCompletedDownloadImportAdapter.cs", "Features/Tv/TvCompletedDownloadImportAdapter.cs" })
        {
            var source = File.ReadAllText(Path.Combine(sourceRoot, adapter));
            Assert.IsFalse(source.Contains("AnimeImportSettingsStore", StringComparison.Ordinal), adapter);
            StringAssert.Contains(source, "ResolveDefaultAsync", adapter);
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Jularr.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate Jularr repository root.");
    }

    private sealed class TempFolders : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), $"jularr-moviety-routing-{Guid.NewGuid():N}");

        public TempFolders() => Directory.CreateDirectory(Root);

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
