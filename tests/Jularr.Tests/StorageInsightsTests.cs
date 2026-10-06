using Jularr.Web.Data;
using Jularr.Web.Features.Audiobooks;
using Jularr.Web.Features.Library;
using Jularr.Web.Features.MediaCore;
using Jularr.Web.Features.Playback.Transcoding;
using Jularr.Web.Features.Storage;
using Jularr.Web.Features.Storage.Insights;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jularr.Tests;

[TestClass]
public sealed class StorageInsightsTests
{
    private const long Gigabyte = 1_000_000_000;

    [TestMethod]
    public async Task UsageAggregatesPerRootMediaTypeAndLargestFiles()
    {
        using var scope = await Scope.CreateAsync();
        var online = scope.AddRoot("Media", scope.Dir("media"));
        var second = scope.AddRoot("Archive", scope.Dir("archive"));
        var showA = scope.AddAnime("Show A");
        var showB = scope.AddAnime("Show B");
        scope.AddMedia(online, showA, 1, 1, 3 * Gigabyte);
        scope.AddMedia(online, showA, 1, 2, 1 * Gigabyte);
        scope.AddMedia(second, showB, 2, 5, 5 * Gigabyte);
        scope.AddAudiobookFile(700_000);
        await scope.Db.SaveChangesAsync();

        var report = await scope.Usage.GetAsync(2, CancellationToken.None);

        var media = report.Roots.Single(x => x.RootId == online.Id);
        Assert.AreEqual(2, media.FileCount);
        Assert.AreEqual(4 * Gigabyte, media.Bytes);
        Assert.AreEqual(StorageHealthState.Online, media.Health);
        Assert.AreEqual(5 * Gigabyte, report.Roots.Single(x => x.RootId == second.Id).Bytes);

        var episodes = report.MediaTypes.Single(x => x.Kind == StorageMediaKind.Episodes);
        Assert.AreEqual(3, episodes.FileCount);
        Assert.AreEqual(9 * Gigabyte, episodes.Bytes);
        var audiobooks = report.MediaTypes.Single(x => x.Kind == StorageMediaKind.Audiobooks);
        Assert.AreEqual(1, audiobooks.FileCount);
        Assert.AreEqual(700_000, audiobooks.Bytes);
        Assert.AreEqual(0, report.MediaTypes.Single(x => x.Kind == StorageMediaKind.Books).Bytes);

        Assert.AreEqual(2, report.LargestItems.Count, "The list is capped at the requested size.");
        Assert.AreEqual("Show B", report.LargestItems[0].Title);
        Assert.AreEqual(5, report.LargestItems[0].EpisodeNumber);
        Assert.AreEqual("Show A", report.LargestItems[1].Title);
        Assert.AreEqual(3 * Gigabyte, report.LargestItems[1].Bytes);
    }

    [TestMethod]
    public async Task UsageCountsMoviesAsMoviesAndNeverAsEpisodesOrDuplicates()
    {
        using var scope = await Scope.CreateAsync();
        var root = scope.AddRoot("Media", scope.Dir("media"));
        scope.AddMovieFile(root, "Ember Road", 8 * Gigabyte);
        scope.AddMovieFile(root, "Salt Flats", 4 * Gigabyte);
        scope.AddMedia(root, scope.AddAnime("Show"), 1, 1, Gigabyte);
        await scope.Db.SaveChangesAsync();

        var report = await scope.Usage.GetAsync(5, CancellationToken.None);

        var movies = report.MediaTypes.Single(x => x.Kind == StorageMediaKind.Movies);
        Assert.AreEqual(2, movies.FileCount);
        Assert.AreEqual(12 * Gigabyte, movies.Bytes);
        var episodes = report.MediaTypes.Single(x => x.Kind == StorageMediaKind.Episodes);
        Assert.AreEqual(1, episodes.FileCount);
        Assert.IsFalse(report.MediaTypes.Any(x => x.Kind == StorageMediaKind.UnmatchedVideo), "A healthy library has no unmatched row.");
        Assert.AreEqual(0, report.Roots.Single().DuplicateEpisodes, "Two movies in one root are not a duplicate episode.");
        Assert.AreEqual("Ember Road", report.LargestItems[0].Title);
        Assert.IsNull(report.LargestItems[0].EpisodeNumber, "A movie has no episode number.");
        Assert.AreEqual("Show", report.LargestItems.Single(x => x.EpisodeNumber is not null).Title);
    }

    [TestMethod]
    public async Task UsageListsAVideoNoWorkOrEpisodeClaimsAsUnmatched()
    {
        using var scope = await Scope.CreateAsync();
        var root = scope.AddRoot("Media", scope.Dir("media"));
        scope.Db.MediaFiles.Add(new MediaFile
        {
            LibraryRootId = root.Id,
            Path = Path.Combine(root.Path, "loose.mkv"),
            SizeBytes = 3 * Gigabyte,
            LastWriteTimeUtc = new DateTime(2025, 1, 2, 3, 4, 5, DateTimeKind.Utc)
        });
        await scope.Db.SaveChangesAsync();

        var report = await scope.Usage.GetAsync(5, CancellationToken.None);

        Assert.AreEqual(3 * Gigabyte, report.MediaTypes.Single(x => x.Kind == StorageMediaKind.UnmatchedVideo).Bytes);
        Assert.AreEqual(0, report.MediaTypes.Single(x => x.Kind == StorageMediaKind.Movies).FileCount);
    }

    [TestMethod]
    public async Task UsageOfAnUnreachableRootComesFromTheInventoryWithoutFreeSpace()
    {
        using var scope = await Scope.CreateAsync();
        var missing = Path.Combine(scope.Base, "not-mounted");
        var root = scope.AddRoot("Offline", missing);
        scope.AddMedia(root, scope.AddAnime("Show"), 1, 1, 2 * Gigabyte);
        await scope.Db.SaveChangesAsync();

        var report = await scope.Usage.GetAsync(5, CancellationToken.None);

        var usage = report.Roots.Single();
        Assert.AreEqual(StorageHealthState.OfflineUnexpected, usage.Health);
        Assert.IsFalse(usage.IsOnline);
        Assert.AreEqual(2 * Gigabyte, usage.Bytes, "Sizes still come from the last scan.");
        Assert.IsNull(usage.FreeBytes);
        Assert.AreEqual(StorageHealthState.OfflineUnexpected, report.LargestItems.Single().RootHealth);
    }

    [TestMethod]
    public async Task UsageNeverProbesAWakeOnLanRootThatWasNotObserved()
    {
        using var scope = await Scope.CreateAsync();
        var sleeping = scope.AddRoot("NAS", Path.Combine(scope.Base, "nas"), wake: true);
        scope.AddMedia(sleeping, scope.AddAnime("Show"), 1, 1, Gigabyte);
        await scope.Db.SaveChangesAsync();

        var report = await scope.Usage.GetAsync(5, CancellationToken.None);

        var usage = report.Roots.Single();
        Assert.IsNull(usage.Health, "A sleeping NAS is reported as not checked, not probed.");
        Assert.AreEqual(Gigabyte, usage.Bytes);
        Assert.IsNull(
            scope.Coordinator.GetCached(sleeping.Id, wakeConfigured: true),
            "The analytics must not have touched the storage.");
    }

    [TestMethod]
    public async Task UsageKeepsAnAlreadyObservedSleepingRootAsExpectedOffline()
    {
        using var scope = await Scope.CreateAsync();
        var sleeping = scope.AddRoot("NAS", Path.Combine(scope.Base, "nas"), wake: true);
        scope.AddMedia(sleeping, scope.AddAnime("Show"), 1, 1, Gigabyte);
        await scope.Db.SaveChangesAsync();
        await scope.Coordinator.ProbeAsync(
            sleeping.Id, sleeping.Path, wakeConfigured: true, force: true, CancellationToken.None);

        var report = await scope.Usage.GetAsync(5, CancellationToken.None);

        Assert.AreEqual(StorageHealthState.OfflineExpected, report.Roots.Single().Health);
        Assert.IsNull(report.Roots.Single().FreeBytes);
    }

    [TestMethod]
    public async Task ScannerFindsOnlyCacheEntriesTheLibraryNoLongerNeeds()
    {
        using var scope = await Scope.CreateAsync();
        var root = scope.AddRoot("Media", scope.Dir("media"));
        var kept = scope.AddMedia(root, scope.AddAnime("Kept"), 1, 1, 1000);
        var changed = scope.AddMedia(root, scope.AddAnime("Changed"), 1, 1, 2000);
        await scope.Db.SaveChangesAsync();
        var gone = Guid.NewGuid();

        var current = scope.Prepared(kept.Id, kept.SizeBytes, kept.LastWriteTimeUtc.Ticks, "compatible", 10);
        var removed = scope.Prepared(gone, 1000, 1, "server-h264", 30);
        var stale = scope.Prepared(changed.Id, 1999, changed.LastWriteTimeUtc.Ticks, "compatible", 20);
        var oldWork = scope.Prepared(Guid.NewGuid(), 1, 1, "compatible", 40, suffix: ".tmp", age: TimeSpan.FromHours(12));
        var freshWork = scope.Prepared(Guid.NewGuid(), 1, 1, "compatible", 5, suffix: ".tmp", age: TimeSpan.FromMinutes(5));
        var stranger = scope.WriteFile(Path.Combine(scope.Layout.PlaybackCacheRoot, "notes.txt"), 7);

        var keptTrickplay = scope.Folder(scope.Layout.TrickplayRoot, $"{kept.Id:N}-abc-v1", 100);
        var goneTrickplay = scope.Folder(scope.Layout.TrickplayRoot, $"{gone:N}-abc-v1", 200);
        var abandonedWork = scope.Folder(Path.Combine(scope.Layout.TrickplayRoot, ".tmp"), "run1", 50, age: TimeSpan.FromHours(5));
        var freshWorkFolder = scope.Folder(Path.Combine(scope.Layout.TrickplayRoot, ".tmp"), "run2", 50);

        var idleSession = scope.Folder(scope.Layout.HlsRoot, Guid.NewGuid().ToString("N"), 300, age: TimeSpan.FromHours(3));
        var liveSession = scope.Folder(scope.Layout.HlsRoot, Guid.NewGuid().ToString("N"), 300);
        var artwork = scope.WriteFile(Path.Combine(scope.Layout.ArtworkRoot, "anime", "poster.webp"), 4000, TimeSpan.FromDays(400));

        var report = await scope.Scanner.ScanAsync(CancellationToken.None);

        var found = report.Plan.Candidates.ToDictionary(x => x.Path, x => x);
        CollectionAssert.AreEquivalent(
            new[] { removed, stale, oldWork, goneTrickplay, abandonedWork, idleSession },
            found.Keys.ToArray());
        Assert.AreEqual(ReclaimReason.SourceRemoved, found[removed].Reason);
        Assert.AreEqual(ReclaimReason.SourceChanged, found[stale].Reason);
        Assert.AreEqual(ReclaimReason.InterruptedWork, found[oldWork].Reason);
        Assert.AreEqual(ReclaimReason.SourceRemoved, found[goneTrickplay].Reason);
        Assert.AreEqual(ReclaimReason.InterruptedWork, found[abandonedWork].Reason);
        Assert.AreEqual(ReclaimReason.IdleSession, found[idleSession].Reason);
        Assert.AreEqual(200, found[goneTrickplay].Bytes);
        Assert.IsFalse(found.ContainsKey(current) || found.ContainsKey(freshWork) || found.ContainsKey(stranger));
        Assert.IsFalse(found.ContainsKey(keptTrickplay) || found.ContainsKey(freshWorkFolder) || found.ContainsKey(liveSession));
        Assert.IsFalse(found.ContainsKey(artwork), "Areas without a safe cleanup are measured only.");

        var prepared = report.Usage.Single(x => x.Area == StorageCacheAreaKind.PreparedPlayback);
        Assert.AreEqual(10 + 30 + 20 + 40 + 5 + 7, prepared.Bytes);
        Assert.AreEqual(30 + 20 + 40, prepared.ReclaimableBytes);
        Assert.AreEqual(4000, report.Usage.Single(x => x.Area == StorageCacheAreaKind.Artwork).Bytes);
        Assert.AreEqual(0, report.Usage.Single(x => x.Area == StorageCacheAreaKind.Artwork).ReclaimableBytes);
    }

    [TestMethod]
    public async Task PreviewOnlyReadsAndDeletesNothing()
    {
        using var scope = await Scope.CreateAsync();
        var orphan = scope.Prepared(Guid.NewGuid(), 1, 1, "compatible", 64);
        var session = scope.Folder(scope.Layout.HlsRoot, Guid.NewGuid().ToString("N"), 64, age: TimeSpan.FromHours(4));

        var preview = await scope.Cleanup.PreviewAsync(CancellationToken.None);

        Assert.AreEqual(2, preview.Plan.Candidates.Count);
        Assert.AreEqual(128, preview.Plan.TotalBytes);
        Assert.IsTrue(File.Exists(orphan));
        Assert.IsTrue(Directory.Exists(session));
    }

    [TestMethod]
    public async Task CleanRemovesOnlySelectedCleanableAreasAndKeepsLibraryMedia()
    {
        using var scope = await Scope.CreateAsync();
        var mediaDirectory = scope.Dir("media");
        var root = scope.AddRoot("Media", mediaDirectory);
        var file = scope.AddMedia(root, scope.AddAnime("Show"), 1, 1, 1000);
        await scope.Db.SaveChangesAsync();
        var episodeFile = scope.WriteFile(file.Path, 1000);

        var orphan = scope.Prepared(Guid.NewGuid(), 1, 1, "compatible", 100);
        var session = scope.Folder(scope.Layout.HlsRoot, Guid.NewGuid().ToString("N"), 100, age: TimeSpan.FromHours(4));
        var artwork = scope.WriteFile(Path.Combine(scope.Layout.ArtworkRoot, "poster.webp"), 100, TimeSpan.FromDays(400));

        var result = await scope.Cleanup.CleanAsync(
            [StorageCacheAreaKind.PreparedPlayback, StorageCacheAreaKind.Artwork],
            CancellationToken.None);

        Assert.AreEqual(1, result.Removed);
        Assert.AreEqual(100, result.BytesFreed);
        Assert.IsFalse(File.Exists(orphan));
        Assert.IsTrue(Directory.Exists(session), "An area that was not selected stays.");
        Assert.IsTrue(File.Exists(artwork), "Areas without a safe cleanup are never removed.");
        Assert.IsTrue(File.Exists(episodeFile), "Library media is never touched.");

        var again = await scope.Cleanup.CleanAsync(
            [StorageCacheAreaKind.PreparedPlayback],
            CancellationToken.None);
        Assert.AreEqual(0, again.Removed);
    }

    [TestMethod]
    public async Task CleanRefusesEntriesInsideALibraryRootEvenIfTheCacheIsMisconfigured()
    {
        using var scope = await Scope.CreateAsync();
        var libraryDirectory = scope.Dir("library");
        scope.AddRoot("Library", libraryDirectory);
        await scope.Db.SaveChangesAsync();

        var misplaced = new StorageCacheLayout(
            Path.Combine(libraryDirectory, "playback-cache"),
            scope.Layout.HlsRoot,
            scope.Layout.TrickplayRoot,
            scope.Layout.ArtworkRoot,
            scope.Layout.FingerprintRoot,
            scope.Layout.MangaCacheRoot);
        var orphan = scope.WriteFile(
            Path.Combine(misplaced.PlaybackCacheRoot, $"{Guid.NewGuid():N}-1-1-compatible.mp4"), 50);

        var cleanup = scope.CleanupFor(misplaced);
        var result = await cleanup.CleanAsync([StorageCacheAreaKind.PreparedPlayback], CancellationToken.None);

        Assert.AreEqual(0, result.Removed);
        Assert.AreEqual(1, result.Skipped);
        Assert.IsTrue(File.Exists(orphan));
    }

    [TestMethod]
    public async Task CleanRefusesAFileThatIsAKnownMediaPath()
    {
        using var scope = await Scope.CreateAsync();
        var root = scope.AddRoot("Media", scope.Dir("media"));
        var lookalike = Path.Combine(scope.Layout.PlaybackCacheRoot, $"{Guid.NewGuid():N}-1-1-compatible.mp4");
        var media = scope.AddMedia(root, scope.AddAnime("Show"), 1, 1, 50);
        media.Path = lookalike;
        await scope.Db.SaveChangesAsync();
        scope.WriteFile(lookalike, 50);

        var result = await scope.Cleanup.CleanAsync([StorageCacheAreaKind.PreparedPlayback], CancellationToken.None);

        Assert.AreEqual(0, result.Removed);
        Assert.IsTrue(File.Exists(lookalike));
    }

    private sealed class Scope : IDisposable
    {
        private Scope(string basePath, AppDbContext db)
        {
            Base = basePath;
            Db = db;
            Layout = new StorageCacheLayout(
                Path.Combine(basePath, "data", "playback-cache"),
                Path.Combine(basePath, "data", "playback-cache", "hls"),
                Path.Combine(basePath, "data", "playback-cache", "trickplay"),
                Path.Combine(basePath, "data", "cache", "artwork"),
                Path.Combine(basePath, "data", "media-segment-cache", "fingerprints"),
                Path.Combine(basePath, "data", "manga-cache"));
            // The HLS folder is Jularr's: the session manager marks the root with its first session, and only marked roots are cleaned.
            Directory.CreateDirectory(Layout.HlsRoot);
            PlaybackCacheOwnership.MarkRoot(Layout.HlsRoot);
            Coordinator = new StorageAvailabilityCoordinator();
            Usage = new StorageUsageService(
                db,
                new LibraryRootAvailabilityService(db, Coordinator),
                new StorageIntegrityService(db));
            Scanner = new StorageCacheScanner(db, Layout, TimeProvider.System);
            Cleanup = CleanupFor(Layout);
        }

        public string Base { get; }
        public AppDbContext Db { get; }
        public StorageCacheLayout Layout { get; }
        public StorageAvailabilityCoordinator Coordinator { get; }
        public StorageUsageService Usage { get; }
        public StorageCacheScanner Scanner { get; }
        public StorageCleanupService Cleanup { get; }

        public static async Task<Scope> CreateAsync()
        {
            var basePath = Path.Combine(Path.GetTempPath(), $"jularr-storage-insights-{Guid.NewGuid():N}");
            Directory.CreateDirectory(basePath);
            var databasePath = Path.Combine(basePath, "test.db");
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite($"Data Source={databasePath};Foreign Keys=True")
                .Options;
            var db = new AppDbContext(options);
            await DatabaseMigrationBridge.UpgradeAsync(db);
            return new Scope(basePath, db);
        }

        public StorageCleanupService CleanupFor(StorageCacheLayout layout) =>
            new(Db, new StorageCacheScanner(Db, layout, TimeProvider.System), layout, NullLogger<StorageCleanupService>.Instance);

        public string Dir(string name)
        {
            var path = Path.Combine(Base, name);
            Directory.CreateDirectory(path);
            return path;
        }

        public LibraryRoot AddRoot(string name, string path, bool wake = false)
        {
            var root = new LibraryRoot
            {
                Name = name,
                Path = path,
                WakeOnLanEnabled = wake,
                WakeMacAddress = wake ? "AA:BB:CC:DD:EE:FF" : null
            };
            Db.LibraryRoots.Add(root);

            // A mounted root is not empty; an empty one with known media reads as not mounted.
            if (Directory.Exists(path))
            {
                File.WriteAllText(Path.Combine(path, ".keep"), "x");
            }

            return root;
        }

        public Anime AddAnime(string title)
        {
            var anime = new Anime { Key = title.ToLowerInvariant(), Title = title };
            Db.Anime.Add(anime);
            return anime;
        }

        public MediaFile AddMedia(LibraryRoot root, Anime anime, int season, int number, long bytes)
        {
            var episode = new Episode { AnimeId = anime.Id, SeasonNumber = season, Number = number };
            Db.Episodes.Add(episode);
            var file = new MediaFile
            {
                LibraryRootId = root.Id,
                EpisodeId = episode.Id,
                Path = Path.Combine(root.Path, $"{anime.Key}-s{season}e{number}.mkv"),
                SizeBytes = bytes,
                LastWriteTimeUtc = new DateTime(2025, 1, 2, 3, 4, 5, DateTimeKind.Utc)
            };
            Db.MediaFiles.Add(file);
            return file;
        }

        public void AddMovieFile(LibraryRoot root, string title, long bytes)
        {
            var work = new Work { MediaType = WorkMediaType.Movie, CanonicalTitle = title };
            var version = new WorkVersion { WorkId = work.Id, VersionKey = $"video-file:{title}" };
            var asset = new MediaAsset { WorkId = work.Id, WorkVersionId = version.Id, Kind = MediaAssetKind.Video };
            Db.Works.Add(work);
            Db.WorkVersions.Add(version);
            Db.MediaAssets.Add(asset);
            Db.MediaFiles.Add(new MediaFile
            {
                LibraryRootId = root.Id,
                MediaAssetId = asset.Id,
                Path = Path.Combine(root.Path, $"{title}.mkv"),
                SizeBytes = bytes,
                LastWriteTimeUtc = new DateTime(2025, 1, 2, 3, 4, 5, DateTimeKind.Utc)
            });
        }

        public void AddAudiobookFile(long bytes)
        {
            var book = new Audiobook { Key = "book", Title = "Book" };
            Db.Audiobooks.Add(book);
            Db.AudiobookFiles.Add(new AudiobookFile
            {
                AudiobookId = book.Id,
                FileKey = "book",
                FileName = "book.m4b",
                Format = "M4B",
                StoragePath = Path.Combine(Base, "audiobooks", "book.m4b"),
                SizeBytes = bytes
            });
        }

        public string Prepared(
            Guid mediaFileId,
            long size,
            long ticks,
            string kind,
            int bytes,
            string suffix = "",
            TimeSpan? age = null) =>
            WriteFile(
                Path.Combine(Layout.PlaybackCacheRoot, $"{mediaFileId:N}-{size}-{ticks}-{kind}.mp4{suffix}"),
                bytes,
                age);

        public string WriteFile(string path, int bytes, TimeSpan? age = null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, new byte[bytes]);
            if (age is { } value)
            {
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow - value);
            }

            return path;
        }

        public string Folder(string parent, string name, int bytes, TimeSpan? age = null)
        {
            var folder = Path.Combine(parent, name);
            WriteFile(Path.Combine(folder, "segment.bin"), bytes, age);
            if (age is { } value)
            {
                Directory.SetLastWriteTimeUtc(folder, DateTime.UtcNow - value);
                Directory.SetCreationTimeUtc(folder, DateTime.UtcNow - value);
            }

            return folder;
        }

        public void Dispose()
        {
            Db.Dispose();
            try
            {
                Directory.Delete(Base, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
