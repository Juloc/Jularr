using System.IO.Compression;
using Jularr.Web.Data;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.Import;
using Jularr.Web.Features.Manga;
using Jularr.Web.Features.Operations;
using Jularr.Web.Features.ReadingAcquisition;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jularr.Tests;

/// <summary>
/// Completed Manga downloads go into the configured Manga library with the owner's import mode,
/// and a new volume joins the series already matched to the same AniList entry (#485 item 7).
/// </summary>
[TestClass]
public sealed class MangaCompletedDownloadImportTests
{
    [TestMethod]
    public async Task DownloadIsCopiedIntoTheMangaLibraryAndKeptInTheDownloadFolder()
    {
        await using var host = await Host.CreateAsync(ImportMode.Copy);
        var download = host.Download("Frieren.Vol.01.CBZ", "Frieren Vol 01.cbz");

        var result = await host.ImportAsync(download, provider: "manual", externalId: "frieren");

        Assert.AreEqual(CompletedDownloadImportDisposition.Completed, result.Disposition, result.Message);
        var placed = Path.Combine(host.Library, "Frieren", "Frieren.Vol.01.CBZ", "Frieren Vol 01.cbz");
        Assert.IsTrue(File.Exists(placed));
        Assert.IsTrue(File.Exists(Path.Combine(download, "Frieren Vol 01.cbz")));
        var series = await host.SingleSeriesAsync();
        Assert.AreEqual(Path.Combine(host.Library, "Frieren"), series.SourcePath);
        Assert.AreEqual($"/Manga/Series/{series.Id}", result.ResultUrl);
    }

    [TestMethod]
    public async Task ADefaultRootChosenInStorageReplacesTheLegacyLibraryFolderAndItsPlacementPolicy()
    {
        await using var host = await Host.CreateAsync(ImportMode.Move);
        var routedRoot = Path.Combine(host.Root, "routed");
        Directory.CreateDirectory(routedRoot);
        var root = new Jularr.Web.Features.Library.LibraryRoot { Name = "Manga", Path = routedRoot };
        host.Db.LibraryRoots.Add(root);
        await host.Db.SaveChangesAsync();
        await new Jularr.Web.Features.Storage.LibraryRootRoutingService(host.Db).AssignDefaultAsync(Jularr.Web.Features.Library.LibraryContentType.Manga, root.Id, Jularr.Web.Features.Library.LibraryPlacementPolicy.Copy);
        var download = host.Download("Frieren.Vol.01.CBZ", "Frieren Vol 01.cbz");

        var result = await host.ImportAsync(download, provider: "manual", externalId: "frieren", routed: true);

        Assert.AreEqual(CompletedDownloadImportDisposition.Completed, result.Disposition, result.Message);
        Assert.IsTrue(File.Exists(Path.Combine(routedRoot, "Frieren", "Frieren.Vol.01.CBZ", "Frieren Vol 01.cbz")), "The Storage root receives the release.");
        Assert.IsFalse(Directory.Exists(host.Library), "The legacy folder is not used once Storage has a default root.");
        Assert.IsTrue(File.Exists(Path.Combine(download, "Frieren Vol 01.cbz")), "The root's Copy policy beats the legacy Move mode.");
        Assert.AreEqual(ImportMode.Copy, result.Placement!.Mode);
    }

    [TestMethod]
    public async Task MoveModeEmptiesTheDownloadFolder()
    {
        await using var host = await Host.CreateAsync(ImportMode.Move);
        var download = host.Download("Frieren.Vol.01.CBZ", "Frieren Vol 01.cbz");

        var result = await host.ImportAsync(download, provider: "manual", externalId: "frieren");

        Assert.AreEqual(CompletedDownloadImportDisposition.Completed, result.Disposition, result.Message);
        Assert.IsFalse(File.Exists(Path.Combine(download, "Frieren Vol 01.cbz")));
        Assert.IsTrue(File.Exists(Path.Combine(host.Library, "Frieren", "Frieren.Vol.01.CBZ", "Frieren Vol 01.cbz")));
    }

    [TestMethod]
    public async Task NextVolumeJoinsTheSeriesMatchedToTheSameAniListEntry()
    {
        await using var host = await Host.CreateAsync(ImportMode.Copy);
        var first = await host.ImportAsync(
            host.Download("Frieren.Vol.01.CBZ", "Frieren Vol 01.cbz"),
            provider: "manual",
            externalId: "frieren");
        var series = await host.SingleSeriesAsync();
        await host.Repository.UpdateMetadataAsync(
            series.Id,
            new MangaAniListCandidate("118586", "Frieren", null, null, null, null, "RELEASING"),
            CancellationToken.None);

        var second = await host.ImportAsync(
            host.Download("Sousou.no.Frieren.v02.Digital", "Sousou no Frieren v02.cbz"),
            provider: "anilist",
            externalId: "118586",
            title: "Sousou no Frieren");

        Assert.AreEqual(CompletedDownloadImportDisposition.Completed, first.Disposition, first.Message);
        Assert.AreEqual(CompletedDownloadImportDisposition.Completed, second.Disposition, second.Message);
        var only = await host.SingleSeriesAsync();
        Assert.AreEqual(series.Id, only.Id);
        Assert.AreEqual(2, await host.Db.Database.SqlQueryRaw<int>("""SELECT COUNT(*) AS "Value" FROM "MangaChapters" """).SingleAsync());
        // The matched series already lives in the library, so the new release goes into its folder.
        Assert.IsTrue(File.Exists(Path.Combine(host.Library, "Frieren", "Sousou.no.Frieren.v02.Digital", "Sousou no Frieren v02.cbz")));
        Assert.AreEqual($"/Manga/Series/{series.Id}", second.ResultUrl);
    }

    [TestMethod]
    public async Task WithoutALibraryTheDownloadIsReadInPlaceButStillJoinsTheMatchedSeries()
    {
        await using var host = await Host.CreateAsync(mode: null);
        await host.ImportAsync(host.Download("Frieren.Vol.01", "v01.cbz"), provider: "manual", externalId: "frieren");
        var series = await host.SingleSeriesAsync();
        await host.Repository.UpdateMetadataAsync(
            series.Id,
            new MangaAniListCandidate("118586", "Frieren", null, null, null, null, "RELEASING"),
            CancellationToken.None);

        var second = await host.ImportAsync(host.Download("Frieren.Vol.02", "v02.cbz"), provider: "anilist", externalId: "118586");

        Assert.AreEqual(CompletedDownloadImportDisposition.Completed, second.Disposition, second.Message);
        Assert.AreEqual(series.Id, (await host.SingleSeriesAsync()).Id);
        Assert.IsFalse(Directory.Exists(host.Library));
    }

    [TestMethod]
    public async Task PackageWithoutMangaIsRejectedWithoutCreatingASeries()
    {
        await using var host = await Host.CreateAsync(ImportMode.Copy);
        var download = Path.Combine(host.Root, "downloads", "Not.Manga");
        Directory.CreateDirectory(download);
        await File.WriteAllTextAsync(Path.Combine(download, "readme.nfo"), "nothing here");

        var result = await host.ImportAsync(download, provider: "manual", externalId: "x");

        Assert.AreEqual(CompletedDownloadImportDisposition.RejectedRelease, result.Disposition, result.Message);
        Assert.AreEqual(0, await host.Db.Database.SqlQueryRaw<int>("""SELECT COUNT(*) AS "Value" FROM "MangaSeries" """).SingleAsync());
    }

    [TestMethod]
    public void HardlinkOrCopyFallsBackToCopyAcrossFilesystemsButHardlinkDoesNot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"jularr-transfer-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var source = Path.Combine(root, "a.cbz");
            File.WriteAllBytes(source, [1, 2, 3]);
            var transfer = new ImportFileTransfer(new CrossDeviceHardLinks());

            transfer.Transfer(source, Path.Combine(root, "copy.cbz"), ImportMode.HardlinkOrCopy);
            Assert.ThrowsExactly<CrossDeviceLinkException>(
                () => transfer.Transfer(source, Path.Combine(root, "link.cbz"), ImportMode.Hardlink));

            Assert.IsTrue(File.Exists(Path.Combine(root, "copy.cbz")));
            Assert.IsFalse(File.Exists(Path.Combine(root, "link.cbz")));
            Assert.IsTrue(File.Exists(source));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task MangaLibrarySettingSurvivesTheSettingsStore()
    {
        var root = Path.Combine(Path.GetTempPath(), $"jularr-library-settings-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            await new AnimeImportSettingsStore(root).UpdateAsync(state => state with
            {
                MediaLibraries = new() { [MediaAcquisitionKind.Manga] = new MediaLibraryTarget("/data/media/manga", ImportMode.Hardlink) }
            });

            var loaded = await new AnimeImportSettingsStore(root).LoadAsync();

            Assert.AreEqual("/data/media/manga", loaded.LibraryFor(MediaAcquisitionKind.Manga)!.LibraryRoot);
            Assert.AreEqual(ImportMode.Hardlink, loaded.ModeFor(MediaAcquisitionKind.Manga));
            Assert.IsNull(loaded.LibraryFor(MediaAcquisitionKind.LightNovel));
            Assert.AreEqual(ImportMode.Move, loaded.ModeFor(MediaAcquisitionKind.LightNovel));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class CrossDeviceHardLinks : IHardLinkCreator
    {
        public void CreateHardLink(string sourcePath, string destinationPath) =>
            throw new CrossDeviceLinkException(sourcePath);
    }

    private sealed class Host : IAsyncDisposable
    {
        private readonly AnimeImportSettingsStore settings;

        private Host(string root, AppDbContext db, AnimeImportSettingsStore settings)
        {
            Root = root;
            Db = db;
            this.settings = settings;
        }

        public string Root { get; }
        public string Library => Path.Combine(Root, "library", "manga");
        public AppDbContext Db { get; }
        public MangaRepository Repository => new(Db);

        public static async Task<Host> CreateAsync(ImportMode? mode)
        {
            var root = Path.Combine(Path.GetTempPath(), $"jularr-manga-download-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            var db = new AppDbContext(
                new DbContextOptionsBuilder<AppDbContext>()
                    .UseSqlite($"Data Source={Path.Combine(root, "jularr.db")};Foreign Keys=True")
                    .Options);
            await DatabaseMigrationBridge.UpgradeAsync(db);
            var settings = new AnimeImportSettingsStore(root);
            var host = new Host(root, db, settings);
            if (mode is { } importMode)
            {
                await settings.UpdateAsync(state => state with
                {
                    MediaLibraries = new() { [MediaAcquisitionKind.Manga] = new MediaLibraryTarget(host.Library, importMode) }
                });
            }

            return host;
        }

        public string Download(string jobName, string archiveName)
        {
            var folder = Path.Combine(Root, "downloads", jobName);
            Directory.CreateDirectory(folder);
            using var archive = ZipFile.Open(Path.Combine(folder, archiveName), ZipArchiveMode.Create);
            for (var index = 0; index < 2; index++)
            {
                var entry = archive.CreateEntry($"{index + 1:D3}.jpg", CompressionLevel.NoCompression);
                using var stream = entry.Open();
                stream.Write([(byte)(index + 1), 2, 3, (byte)jobName.Length]);
            }

            return folder;
        }

        public Task<CompletedDownloadImportResult> ImportAsync(
            string download,
            string provider,
            string externalId,
            string title = "Frieren",
            bool routed = false)
        {
            var adapter = new MangaCompletedDownloadImportAdapter(
                Db,
                null!,
                null!,
                null!,
                settings,
                new FileSystemHardLinkCreator(),
                NullLogger<MangaCompletedDownloadImportAdapter>.Instance,
                Path.Combine(Root, "cache"),
                routing: routed ? new Jularr.Web.Features.Storage.LibraryRootRoutingService(Db) : null);
            var request = new AcquisitionRequest(
                Guid.NewGuid(),
                MediaAcquisitionKind.Manga,
                provider,
                externalId,
                title,
                null,
                null,
                null,
                "owner",
                AcquisitionRequestStatus.Importing,
                null,
                null,
                null,
                DateTime.UtcNow,
                DateTime.UtcNow,
                "owner",
                DateTime.UtcNow);
            var operation = new OperationSnapshot(
                Guid.NewGuid(), "reading-usenet-download", "External downloads", OperationLane.Normal,
                OperationStatus.Succeeded, "owner", "Download Manga", title, 100, null, null, true,
                null, null, null, null, 1, true, "sabnzbd", "nzo", DateTime.UtcNow, DateTime.UtcNow,
                DateTime.UtcNow, DateTime.UtcNow);
            return adapter.ImportAsync(
                new CompletedDownloadImportRequest(request, operation, download),
                CancellationToken.None);
        }

        public async Task<MangaSeriesLocation> SingleSeriesAsync()
        {
            var rows = await Db.Database
                .SqlQueryRaw<SeriesRow>("""SELECT "Id", "Title", "SourcePath" FROM "MangaSeries" """)
                .ToListAsync();
            Assert.AreEqual(1, rows.Count, "exactly one Manga series");
            return new MangaSeriesLocation(Guid.Parse(rows[0].Id), rows[0].Title, rows[0].SourcePath);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            Directory.Delete(Root, recursive: true);
        }
    }

    private sealed record SeriesRow(string Id, string Title, string SourcePath);
}
