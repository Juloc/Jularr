using Jularr.Web.Data;
using Jularr.Web.Features.Acquisition.Sabnzbd;
using Jularr.Web.Features.Operations;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Tests;

[TestClass]
public sealed class OperationCoverageTests
{
    [TestMethod]
    public async Task ExternalRunningOperationSurvivesWorkerRestartReconciliation()
    {
        var path = TempDatabasePath();

        try
        {
            await using var db = await CreateDatabaseAsync(path);
            var store = new OperationStore(db);

            var localId = await store.CreateAsync(
                new OperationDescriptor(
                    "local-test",
                    "Test",
                    "Local operation",
                    Lane: OperationLane.Normal));

            var externalId = await store.CreateAsync(
                new OperationDescriptor(
                    "sabnzbd-download",
                    "External downloads",
                    "SAB download",
                    Lane: OperationLane.Normal,
                    IsDownload: true,
                    Retryable: false,
                    ExternalProvider: SabnzbdClient.ProviderId,
                    ExternalId: "SABnzbd_nzo_test"));

            await store.MarkRunningAsync(localId);
            await store.MarkRunningAsync(externalId);

            var recovered = await store.RecoverInterruptedAsync(
                OperationLane.Normal);

            Assert.AreEqual(1, recovered);

            var local = await store.GetAsync(localId);
            var external = await store.GetAsync(externalId);

            Assert.IsNotNull(local);
            Assert.IsNotNull(external);
            Assert.AreEqual(OperationStatus.Interrupted, local.Status);
            Assert.AreEqual(OperationStatus.Running, external.Status);
            Assert.AreEqual("sabnzbd", external.ExternalProvider);
            Assert.AreEqual("SABnzbd_nzo_test", external.ExternalId);
            Assert.IsFalse(external.CanCancel);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public async Task ExternalReferenceCanBeAttachedAfterSubmission()
    {
        var path = TempDatabasePath();

        try
        {
            await using var db = await CreateDatabaseAsync(path);
            var store = new OperationStore(db);

            var id = await store.CreateAsync(
                new OperationDescriptor(
                    "sabnzbd-download",
                    "External downloads",
                    "SAB download",
                    IsDownload: true,
                    Retryable: false,
                    ExternalProvider: SabnzbdClient.ProviderId));

            await store.MarkRunningAsync(id);
            await store.SetExternalReferenceAsync(
                id,
                SabnzbdClient.ProviderId,
                "SABnzbd_nzo_late");

            var active = await store.ListActiveExternalAsync(
                SabnzbdClient.ProviderId);

            Assert.AreEqual(1, active.Count);
            Assert.AreEqual(id, active[0].Id);
            Assert.AreEqual("SABnzbd_nzo_late", active[0].ExternalId);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public void LongRunningPageActionsUseCanonicalOperations()
    {
        var root = FindRepositoryRoot();

        AssertKinds(
            root,
            "src/Jularr.Web/Pages/Manga/Index.cshtml.cs",
            "manga-upload-import",
            "manga-path-import");

        AssertKinds(
            root,
            "src/Jularr.Web/Pages/Manga/Series.cshtml.cs",
            "manga-anilist-match",
            "manga-refresh");

        AssertKinds(
            root,
            "src/Jularr.Web/Pages/Index.Discover.cs",
            "discover-novel-import");

        AssertKinds(
            root,
            "src/Jularr.Web/Pages/Discover/MangaImport.cshtml.cs",
            "discover-manga-upload-import",
            "discover-manga-path-import");

        AssertKinds(
            root,
            "src/Jularr.Web/Pages/Novels/Work.cshtml.cs",
            "anilist-novel-progress-sync",
            "novel-refresh",
            "novel-anilist-match");

        AssertKinds(
            root,
            "src/Jularr.Web/Pages/Novels/Read.cshtml.cs",
            "novel-chapter-refresh");

        // Queued Novel jobs (chapter download, translation, episode mapping)
        // are defined once and used by the Work and Read pages.
        AssertKinds(
            root,
            "src/Jularr.Web/Features/Novels/NovelJobs.cs",
            "novel-chapter-download",
            "novel-chapter-translation",
            "novel-episode-mapping");

        AssertKinds(
            root,
            "src/Jularr.Web/Pages/Library/Anime.cshtml.cs",
            "anime-metadata-match",
            "anime-episode-range-match",
            "anime-metadata-refresh");

        AssertKinds(
            root,
            "src/Jularr.Web/Pages/Library/Episode.cshtml.cs",
            "episode-subtitle-import",
            "anilist-episode-progress-sync",
            "episode-learning-preparation");

        AssertKinds(
            root,
            "src/Jularr.Web/Pages/Books/Index.cshtml.cs",
            "book-epub-upload-import",
            "remote-epub-import");

        // Inbox scans of every media type and manual downloads share the
        // completed-download import, so their kinds live with it.
        AssertKinds(
            root,
            "src/Jularr.Web/Features/Acquisition/Import/MediaInboxImportService.cs",
            "media-inbox-import");
        AssertKinds(
            root,
            "src/Jularr.Web/Features/Acquisition/Import/CompletedDownloadImportService.cs",
            "sabnzbd-download");
    }

    private static void AssertKinds(
        string root,
        string relativePath,
        params string[] kinds)
    {
        var source = File.ReadAllText(Path.Combine(
            root,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));

        foreach (var kind in kinds)
        {
            StringAssert.Contains(
                source,
                $"\"{kind}\"",
                $"Expected {relativePath} to register operation kind {kind}.");
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

        throw new DirectoryNotFoundException(
            "Could not locate Jularr repository root.");
    }

    private static string TempDatabasePath() =>
        Path.Combine(
            Path.GetTempPath(),
            $"jularr-operation-coverage-{Guid.NewGuid():N}.db");

    private static async Task<AppDbContext> CreateDatabaseAsync(string path)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={path};Foreign Keys=True")
            .Options;

        var db = new AppDbContext(options);
        await DatabaseMigrationBridge.UpgradeAsync(db);
        return db;
    }
}
