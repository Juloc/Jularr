using Jint;

namespace Jularr.Tests;

[TestClass]
public sealed class OfflineMediaEngineTests
{
    [TestMethod]
    public void PackageIdentityAndChunkingAreStableAcrossMediaTypes()
    {
        var engine = CreateEngine();
        engine.Execute("var media = JularrOfflineMedia;");

        Assert.AreEqual("episode:abc", engine.Evaluate("media.packageId('episode', 'ABC')").AsString());
        Assert.AreEqual("manga:chapter-1", engine.Evaluate("media.packageId('manga', 'chapter-1')").AsString());
        Assert.AreEqual(0, engine.Evaluate("media.chunkCount(0)").AsNumber());
        Assert.AreEqual(2, engine.Evaluate("media.chunkCount(media.CHUNK_BYTES + 1)").AsNumber());
        Assert.AreEqual(10d * 1024 * 1024 * 1024, engine.Evaluate("media.DEFAULT_LIMIT_BYTES").AsNumber());
    }

    [TestMethod]
    public void RangeParserHandlesOpenClosedAndSuffixRanges()
    {
        var engine = CreateEngine();
        engine.Execute("var media = JularrOfflineMedia;");

        Assert.AreEqual(2, engine.Evaluate("media.parseRange('bytes=2-5', 10).start").AsNumber());
        Assert.AreEqual(5, engine.Evaluate("media.parseRange('bytes=2-5', 10).end").AsNumber());
        Assert.AreEqual(9, engine.Evaluate("media.parseRange('bytes=8-', 10).end").AsNumber());
        Assert.AreEqual(7, engine.Evaluate("media.parseRange('bytes=-3', 10).start").AsNumber());
        Assert.IsTrue(engine.Evaluate("media.parseRange('bytes=10-11', 10) === null").AsBoolean());
    }

    [TestMethod]
    public void PackageOnlyCountsReadyWhenEveryChunkIsStored()
    {
        var engine = CreateEngine();
        engine.Execute("var media = JularrOfflineMedia;");
        engine.Execute("var full = { state: 'ready', sizeBytes: media.CHUNK_BYTES + 1, completedChunks: 2 };");
        engine.Execute("var partial = { state: 'ready', sizeBytes: media.CHUNK_BYTES + 1, completedChunks: 1 };");

        Assert.IsTrue(engine.Evaluate("media.isComplete(full)").AsBoolean());
        Assert.IsFalse(engine.Evaluate("media.isComplete(partial)").AsBoolean());
    }

    [TestMethod]
    public void ServiceWorkerKeepsPrivateMediaOutOfCacheStorage()
    {
        var source = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Jularr.Web", "wwwroot", "service-worker.js"));
        StringAssert.Contains(source, "JularrOfflineMediaWorker.respond");
        Assert.IsFalse(source.Contains("/api/client/v1/offline-media", StringComparison.Ordinal));
        Assert.IsFalse(source.Contains("/api/client/v1/offline-library", StringComparison.Ordinal));
    }

    [TestMethod]
    public void OfflinePlaybackQueuesProgressForTheProfileScopedReconciler()
    {
        var root = RepositoryRoot();
        var manager = File.ReadAllText(Path.Combine(root, "src", "Jularr.Web", "wwwroot", "js", "offline-media-manager.js"));
        var storage = File.ReadAllText(Path.Combine(root, "src", "Jularr.Web", "wwwroot", "js", "offline-media-storage.js"));
        var player = File.ReadAllText(Path.Combine(root, "src", "Jularr.Web", "wwwroot", "js", "episode-player.js"));
        StringAssert.Contains(storage, "queueProgress");
        StringAssert.Contains(manager, "/api/client/v1/offline/progress");
        StringAssert.Contains(manager, "jularr:episode-progress");
        StringAssert.Contains(player, "jularr:episode-progress");
    }

    [TestMethod]
    public void ColdOfflineCatalogueUsesTheProfileScopedLightNovelStore()
    {
        var root = RepositoryRoot();
        var storage = File.ReadAllText(Path.Combine(root, "src", "Jularr.Web", "wwwroot", "js", "offline-library-storage.js"));
        var catalogue = File.ReadAllText(Path.Combine(root, "src", "Jularr.Web", "wwwroot", "js", "offline-media-catalog.js"));

        StringAssert.Contains(storage, "openStoreForProfile");
        StringAssert.Contains(storage, "[\"offline\", profileId, workId");
        StringAssert.Contains(storage, "clearProfile");
        StringAssert.Contains(catalogue, "openStoreForProfile(profileId)");
        StringAssert.Contains(catalogue, "loadChapterPayload(record.workId, chapterId)");
        StringAssert.Contains(catalogue, "Playback speed");
        StringAssert.Contains(catalogue, "Saved videos");
        Assert.IsFalse(catalogue.Contains("offline-review", StringComparison.Ordinal));

        var manager = File.ReadAllText(Path.Combine(root, "src", "Jularr.Web", "wwwroot", "js", "offline-media-manager.js"));
        StringAssert.Contains(manager, "textStore?.clearProfile()");
    }

    [TestMethod]
    public void DownloadUiShowsProgressAndOffersPackageControlsAtEveryMediaEntryPoint()
    {
        var root = RepositoryRoot();
        var ui = File.ReadAllText(Path.Combine(root, "src", "Jularr.Web", "wwwroot", "js", "offline-media-ui.js"));
        var settings = File.ReadAllText(Path.Combine(root, "src", "Jularr.Web", "Pages", "Settings", "Offline.cshtml"));
        var episode = EpisodePlayerSource.Read(root);
        var manga = File.ReadAllText(Path.Combine(root, "src", "Jularr.Web", "Pages", "Manga", "Read.cshtml"));
        var book = File.ReadAllText(Path.Combine(root, "src", "Jularr.Web", "Pages", "Books", "Read.cshtml"));

        StringAssert.Contains(ui, "jularr:offline-media-progress");
        StringAssert.Contains(ui, "data-offline-media-remove");
        StringAssert.Contains(ui, "completedChunks");
        StringAssert.Contains(settings, "data-offline-media-downloads");
        StringAssert.Contains(settings, "data-offline-media-progress");
        StringAssert.Contains(episode, "data-offline-media-progress");
        StringAssert.Contains(manga, "data-offline-media-progress");
        StringAssert.Contains(book, "data-offline-media-progress");
    }

    private static Engine CreateEngine()
    {
        var engine = new Engine(options => options.TimeoutInterval(TimeSpan.FromSeconds(10)));
        engine.Execute("var window = globalThis;");
        engine.Execute(File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Jularr.Web", "wwwroot", "js", "offline-media.js")));
        return engine;
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Jularr.sln"))) return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate Jularr repository root.");
    }
}
