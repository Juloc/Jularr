using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.Import;
using Jularr.Web.Features.Manga;
using Jularr.Web.Features.Novels;

namespace Jularr.Tests;

// Issue #581: the per-work NAS media folder of a Light Novel or Manga series, resolved on its
// library root from what the library recorded when the media was placed there.
[TestClass]
public sealed class ReadingArtworkFoldersTests
{
    [TestMethod]
    public async Task LightNovelResolvesToTheSeriesFolderOfItsFirstVolumeOnTheLibraryRoot()
    {
        await using var host = await ReadingArtworkTestHost.CreateAsync();
        var workId = await host.AddLightNovelAsync(
            (2, Path.Combine(host.NovelRoot, "Frieren", "Frieren v02.epub")),
            (1, Path.Combine(host.NovelRoot, "Frieren", "Vol 01", "Frieren v01.epub")));

        var folder = await NovelArtworkFolders.ResolveAsync(host.Db, await host.Settings.LoadAsync(), workId, CancellationToken.None);

        Assert.AreEqual(Path.Combine(host.NovelRoot, "Frieren"), folder);
    }

    [TestMethod]
    public async Task LightNovelSkipsVolumesThatAreNotOnTheLibraryRoot()
    {
        await using var host = await ReadingArtworkTestHost.CreateAsync();
        var elsewhere = Path.Combine(host.Root, "elsewhere", "Frieren v01.epub");
        var loose = Path.Combine(host.NovelRoot, "loose.epub");
        var workId = await host.AddLightNovelAsync(
            (1, null),
            (2, elsewhere),
            (3, loose),
            (4, Path.Combine(host.NovelRoot, "Frieren", "Frieren v04.epub")));

        var folder = await NovelArtworkFolders.ResolveAsync(host.Db, await host.Settings.LoadAsync(), workId, CancellationToken.None);

        Assert.AreEqual(Path.Combine(host.NovelRoot, "Frieren"), folder);
    }

    [TestMethod]
    public async Task LightNovelWithoutALibraryRootOrOnTheRootHasNoFolder()
    {
        await using var host = await ReadingArtworkTestHost.CreateAsync(withLibraryRoots: false);
        var onDisk = await host.AddLightNovelAsync((1, Path.Combine(host.NovelRoot, "Frieren", "Frieren v01.epub")));
        var unimported = await host.AddLightNovelAsync((1, null));

        var noRoot = await host.Settings.LoadAsync();
        Assert.IsNull(await NovelArtworkFolders.ResolveAsync(host.Db, noRoot, onDisk, CancellationToken.None));

        await host.ConfigureLibraryRootsAsync(host.NovelRoot, host.MangaRoot);
        var withRoot = await host.Settings.LoadAsync();
        Assert.IsNull(await NovelArtworkFolders.ResolveAsync(host.Db, withRoot, unimported, CancellationToken.None));
        Assert.IsNull(await NovelArtworkFolders.ResolveAsync(host.Db, withRoot, Guid.NewGuid(), CancellationToken.None));
        Assert.IsNotNull(await NovelArtworkFolders.ResolveAsync(host.Db, withRoot, onDisk, CancellationToken.None));
    }

    [TestMethod]
    public async Task MangaResolvesToItsSeriesFolderWhetherImportedAsAFolderOrAsOneArchive()
    {
        await using var host = await ReadingArtworkTestHost.CreateAsync();
        var folderSeries = await host.AddMangaAsync(Path.Combine(host.MangaRoot, "Frieren"));
        var archiveSeries = await host.AddMangaAsync(Path.Combine(host.MangaRoot, "Dungeon Meshi", "Vol 01.cbz"));
        var settings = await host.Settings.LoadAsync();

        Assert.AreEqual(
            Path.Combine(host.MangaRoot, "Frieren"),
            await MangaArtworkFolders.ResolveAsync(host.Db, settings, folderSeries, CancellationToken.None));
        Assert.AreEqual(
            Path.Combine(host.MangaRoot, "Dungeon Meshi"),
            await MangaArtworkFolders.ResolveAsync(host.Db, settings, archiveSeries, CancellationToken.None));
    }

    [TestMethod]
    public async Task MangaReadInPlaceOutsideTheLibraryRootHasNoFolder()
    {
        await using var host = await ReadingArtworkTestHost.CreateAsync();
        var settings = await host.Settings.LoadAsync();
        var inPlace = await host.AddMangaAsync(Path.Combine(host.Root, "downloads", "Frieren"));
        var looseArchive = await host.AddMangaAsync(Path.Combine(host.MangaRoot, "Loose.cbz"));

        Assert.IsNull(await MangaArtworkFolders.ResolveAsync(host.Db, settings, inPlace, CancellationToken.None));
        Assert.IsNull(await MangaArtworkFolders.ResolveAsync(host.Db, settings, looseArchive, CancellationToken.None));
        Assert.IsNull(await MangaArtworkFolders.ResolveAsync(host.Db, settings, Guid.NewGuid(), CancellationToken.None));
    }

    [TestMethod]
    public async Task MangaWithoutALibraryRootHasNoFolder()
    {
        await using var host = await ReadingArtworkTestHost.CreateAsync(withLibraryRoots: false);
        var series = await host.AddMangaAsync(Path.Combine(host.MangaRoot, "Frieren"));

        Assert.IsNull(await MangaArtworkFolders.ResolveAsync(host.Db, await host.Settings.LoadAsync(), series, CancellationToken.None));
    }

    [TestMethod]
    public async Task MangaSeriesRecordedInTheReportedFormResolvesThroughItsRemotePathMapping()
    {
        await using var host = await ReadingArtworkTestHost.CreateAsync();
        Directory.CreateDirectory(Path.Combine(host.MangaRoot, "Frieren"));
        await host.Settings.UpdateAsync(state => state.WithRemotePathMappings(MediaAcquisitionKind.Manga, [new RemotePathMapping("/nas/manga", host.MangaRoot)]));
        var series = await host.AddMangaAsync("/nas/manga/Frieren", createSource: false);

        Assert.AreEqual(
            Path.Combine(host.MangaRoot, "Frieren"),
            await MangaArtworkFolders.ResolveAsync(host.Db, await host.Settings.LoadAsync(), series, CancellationToken.None));
    }

    [TestMethod]
    public async Task MangaSeriesThatIsAFolderOfLoosePageImagesKeepsNoCoverInIt()
    {
        await using var host = await ReadingArtworkTestHost.CreateAsync();
        // The importer reads every image directly in such a folder as a page, so a cover.* written
        // there would become page zero of the only chapter.
        var loose = Path.Combine(host.MangaRoot, "Loose Pages");
        Directory.CreateDirectory(loose);
        await File.WriteAllBytesAsync(Path.Combine(loose, "001.jpg"), [1, 2, 3]);
        await File.WriteAllBytesAsync(Path.Combine(loose, "002.jpg"), [4, 5, 6]);
        var archives = Path.Combine(host.MangaRoot, "Frieren");
        await ArchiveOfOnePageAsync(Path.Combine(archives, "Vol 01.cbz"));

        var repository = new MangaRepository(host.Db);
        var importer = new MangaImportService(repository, Path.Combine(host.Root, "manga-cache"));
        var pages = (await importer.ImportAsync(loose, CancellationToken.None)).SeriesId;
        var volumes = (await importer.ImportAsync(archives, CancellationToken.None)).SeriesId;
        var settings = await host.Settings.LoadAsync();

        Assert.IsNull(await MangaArtworkFolders.ResolveAsync(host.Db, settings, pages, CancellationToken.None));
        Assert.AreEqual(archives, await MangaArtworkFolders.ResolveAsync(host.Db, settings, volumes, CancellationToken.None));
    }

    private static async Task ArchiveOfOnePageAsync(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var file = File.Create(path);
        using var archive = new System.IO.Compression.ZipArchive(file, System.IO.Compression.ZipArchiveMode.Create);
        var entry = archive.CreateEntry("001.jpg", System.IO.Compression.CompressionLevel.NoCompression);
        await using var stream = entry.Open();
        await stream.WriteAsync(new byte[] { 1, 2, 3 });
    }
}
