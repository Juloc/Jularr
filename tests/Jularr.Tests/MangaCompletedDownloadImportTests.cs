using System.IO.Compression;
using Jularr.Web.Data;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.Import;
using Jularr.Web.Features.Manga;
using Jularr.Web.Features.MediaCore;
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
    public async Task TheDefaultRootChosenInStorageReceivesTheReleaseWithItsOwnPlacementPolicy()
    {
        await using var host = await Host.CreateAsync(ImportMode.Move);
        var routedRoot = Path.Combine(host.Root, "routed");
        Directory.CreateDirectory(routedRoot);
        var root = new Jularr.Web.Features.Library.LibraryRoot { Name = "Manga", Path = routedRoot };
        host.Db.LibraryRoots.Add(root);
        await host.Db.SaveChangesAsync();
        await new Jularr.Web.Features.Storage.LibraryRootRoutingService(host.Db).AssignDefaultAsync(Jularr.Web.Features.Library.LibraryContentType.Manga, root.Id, Jularr.Web.Features.Library.LibraryPlacementPolicy.Copy);
        var download = host.Download("Frieren.Vol.01.CBZ", "Frieren Vol 01.cbz");

        var result = await host.ImportAsync(download, provider: "manual", externalId: "frieren");

        Assert.AreEqual(CompletedDownloadImportDisposition.Completed, result.Disposition, result.Message);
        Assert.IsTrue(File.Exists(Path.Combine(routedRoot, "Frieren", "Frieren.Vol.01.CBZ", "Frieren Vol 01.cbz")), "The Storage root receives the release.");
        Assert.IsFalse(Directory.Exists(host.Library), "Only the default root receives imports.");
        Assert.IsTrue(File.Exists(Path.Combine(download, "Frieren Vol 01.cbz")), "The placement policy of the default root decides, here Copy.");
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
    public async Task ARequestsFilesGoIntoTheSeriesOfItsWorkAndASecondVolumeJoinsItWithoutAnotherSeriesOrWork()
    {
        await using var host = await Host.CreateAsync(ImportMode.Copy);
        var request = await RequestWorkTestSupport.CreateRequestAsync(host.Db, MediaAcquisitionKind.Manga, bound: true, provider: "anilist", externalId: "118586", title: "Frieren");

        var first = await host.ImportAsync(host.Download("Frieren.Vol.01.CBZ", "Frieren Vol 01.cbz"), request);
        var series = await host.SingleSeriesAsync();
        var volumeTwo = host.Download("Frieren.Vol.02.CBZ", "Frieren Vol 02.cbz");
        var second = await host.ImportAsync(volumeTwo, request);
        var again = await host.ImportAsync(volumeTwo, request);

        Assert.AreEqual(CompletedDownloadImportDisposition.Completed, first.Disposition, first.Message);
        Assert.AreEqual(CompletedDownloadImportDisposition.Completed, second.Disposition, second.Message);
        Assert.AreEqual(CompletedDownloadImportDisposition.Completed, again.Disposition, again.Message);
        Assert.AreEqual(request.WorkId, await RequestWorkTestSupport.WorkOfLegacyAsync(host.Db, WorkSourceKind.MangaSeries, series.Id), "The series that received the files is the request's Work.");
        Assert.AreEqual(series.Id, (await host.SingleSeriesAsync()).Id, "A next volume never creates another series.");
        Assert.AreEqual(1, await host.Db.Set<Work>().CountAsync(), "Request and import share one Work.");
        Assert.AreEqual($"/Manga/Series/{series.Id}", second.ResultUrl);
    }

    [TestMethod]
    public async Task FilesNeverSilentlySwitchTheRequestsWorkWhenTheSeriesTheyLandInBelongsToAnotherOne()
    {
        await using var host = await Host.CreateAsync(ImportMode.Copy);
        await host.ImportAsync(host.Download("Frieren.Vol.01.CBZ", "Frieren Vol 01.cbz"), provider: "manual", externalId: "frieren");
        var series = await host.SingleSeriesAsync();
        var works = new WorkService(host.Db);
        var other = await works.CreateWorkAsync(WorkMediaType.Manga, "Another series", null, CancellationToken.None);
        await works.LinkSourceAsync(other.Id, WorkSourceKind.MangaSeries, series.Id, CancellationToken.None);
        var request = await RequestWorkTestSupport.CreateRequestAsync(host.Db, MediaAcquisitionKind.Manga, bound: true, provider: "anilist", externalId: "118586", title: "Frieren");

        var result = await host.ImportAsync(host.Download("Frieren.Vol.02.CBZ", "Frieren Vol 02.cbz"), request);

        Assert.AreEqual(CompletedDownloadImportDisposition.NeedsReview, result.Disposition, result.Message);
        Assert.AreEqual(other.Id, await RequestWorkTestSupport.WorkOfLegacyAsync(host.Db, WorkSourceKind.MangaSeries, series.Id), "The series keeps the Work it belonged to.");
        Assert.AreNotEqual(request.WorkId, other.Id);
    }

    [TestMethod]
    public async Task ASeriesMatchedToADifferentAniListEntryIsNotBoundToTheRequestsWork()
    {
        await using var host = await Host.CreateAsync(ImportMode.Copy);
        await host.ImportAsync(host.Download("Frieren.Vol.01.CBZ", "Frieren Vol 01.cbz"), provider: "manual", externalId: "frieren");
        var series = await host.SingleSeriesAsync();
        await host.Repository.UpdateMetadataAsync(series.Id, new MangaAniListCandidate("999", "Frieren", null, null, null, null, "RELEASING"), CancellationToken.None);
        var request = await RequestWorkTestSupport.CreateRequestAsync(host.Db, MediaAcquisitionKind.Manga, bound: true, provider: "anilist", externalId: "118586", title: "Frieren");

        var result = await host.ImportAsync(host.Download("Frieren.Vol.02.CBZ", "Frieren Vol 02.cbz"), request);

        Assert.AreEqual(CompletedDownloadImportDisposition.NeedsReview, result.Disposition, result.Message);
        Assert.IsNull(await RequestWorkTestSupport.WorkOfLegacyAsync(host.Db, WorkSourceKind.MangaSeries, series.Id), "Matching folder names is not identity: nothing was linked.");
    }

    [TestMethod]
    public async Task WithoutADefaultMangaRootTheImportWaitsInsteadOfReadingTheDownloadInPlace()
    {
        await using var host = await Host.CreateAsync(mode: null);

        var result = await host.ImportAsync(host.Download("Frieren.Vol.01", "v01.cbz"), provider: "manual", externalId: "frieren");

        Assert.AreEqual(CompletedDownloadImportDisposition.RetryLater, result.Disposition);
        StringAssert.Contains(result.Message, "Admin → Storage");
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
    public async Task OnlyTheInboxAndTheMappingsSurviveTheSettingsStoreNeverALibraryFolder()
    {
        var root = Path.Combine(Path.GetTempPath(), $"jularr-library-settings-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            await new AnimeImportSettingsStore(root).UpdateAsync(state => state with
            {
                MediaLibraries = new() { [MediaAcquisitionKind.Manga] = new MediaLibraryTarget("/data/media/manga", ImportMode.Hardlink, "/data/inbox/manga") }
            });

            var loaded = await new AnimeImportSettingsStore(root).LoadAsync();

            Assert.AreEqual("/data/inbox/manga", loaded.InboxFor(MediaAcquisitionKind.Manga));
            Assert.IsNull(loaded.LibraryFor(MediaAcquisitionKind.Manga), "The destination is Storage's, so the settings keep none.");
            Assert.AreEqual(ImportMode.Move, loaded.ModeFor(MediaAcquisitionKind.Manga));
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
                await ReadingTestRoots.AssignAsync(db, MediaAcquisitionKind.Manga, host.Library, importMode);
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

        public Task<CompletedDownloadImportResult> ImportAsync(string download, AcquisitionRequest request) =>
            ImportAsync(download, request.Provider, request.ExternalId, request.Title, request);

        public Task<CompletedDownloadImportResult> ImportAsync(
            string download,
            string provider,
            string externalId,
            string title = "Frieren",
            AcquisitionRequest? bound = null)
        {
            var adapter = new MangaCompletedDownloadImportAdapter(
                Db,
                new UnreachableHttpClients(),
                null!,
                null!,
                settings,
                new FileSystemHardLinkCreator(),
                NullLogger<MangaCompletedDownloadImportAdapter>.Instance,
                Path.Combine(Root, "cache"),
                routing: new Jularr.Web.Features.Storage.LibraryRootRoutingService(Db),
                binder: bound is null ? null : RequestWorkTestSupport.Binder(Db));
            var request = bound ?? new AcquisitionRequest(
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

    /// <summary>AniList is not reachable from a test: the metadata match after an import fails the way a network problem does, and the import itself is unaffected.</summary>
    private sealed class UnreachableHttpClients : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new Unreachable());

        private sealed class Unreachable : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
                throw new HttpRequestException("AniList is not reachable in tests.");
        }
    }
}
