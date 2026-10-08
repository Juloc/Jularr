using Jularr.Web.Data;
using Jularr.Web.Features.Acquisition;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.Import;
using Jularr.Web.Features.Acquisition.Monitoring;
using Jularr.Web.Features.Acquisition.Naming;
using Jularr.Web.Features.Acquisition.Ownership;
using Jularr.Web.Features.Acquisition.Pipeline;
using Jularr.Web.Features.Acquisition.Sabnzbd;
using Jularr.Web.Features.Library;
using Jularr.Web.Features.Metadata;
using Jularr.Web.Features.Operations;
using Jularr.Web.Features.Progress;
using Jularr.Web.Features.Sonarr;
using Jularr.Web.Features.Subtitles;
using Jularr.Web.Features.Vocabulary;
using Jularr.Web.Infrastructure;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jularr.Tests;

[TestClass]
public sealed class AnimeNamingRenameTests
{
    private const string SimpleProfileId = "simple";

    [TestMethod]
    public async Task PreviewChangesNothingAndExecuteKeepsEpisodeIdentityAndProgress()
    {
        await using var fixture = await RenameFixture.CreateAsync();
        var episode = await fixture.AddEpisodeFileAsync(1, "[SubsPlease] Frieren - S01E01 (1080p WEB-DL x264 AAC).mkv", sidecar: true);
        await fixture.Naming.AssignAnimeAsync(fixture.Anime.Id, AnimeNamingPresets.SonarrDefaultId, AnimeSeriesType.Anime);
        var mediaFileId = await fixture.SeedMediaAnalysisAsync(episode.EpisodeId);

        var plan = await fixture.PlanAsync();
        var item = plan.Items.Single();
        var expected = Path.Combine(fixture.SeriesFolder, "Season 1", "Frieren - S01E01 - Episode 1 WEBDL-1080p.mkv");

        Assert.AreEqual(AnimeRenameItemStatus.Rename, item.Status, item.Reason);
        Assert.AreEqual(expected, item.TargetPath);
        Assert.AreEqual(1, item.Sidecars.Count);
        Assert.IsTrue(File.Exists(episode.MediaPath), "Preview must not touch the file.");
        Assert.IsTrue(plan.CanExecute, string.Join(" ", plan.BlockingReasons));

        var result = await fixture.ExecuteAsync(plan);

        Assert.IsTrue(result.Success, result.Message);
        Assert.IsFalse(File.Exists(episode.MediaPath));
        Assert.IsTrue(File.Exists(expected));
        var newSidecar = Path.Combine(fixture.SeriesFolder, "Season 1", "Frieren - S01E01 - Episode 1 WEBDL-1080p.ja.srt");
        Assert.IsTrue(File.Exists(newSidecar));

        fixture.Db.ChangeTracker.Clear();
        var mediaFile = await fixture.Db.MediaFiles.SingleAsync();
        Assert.AreEqual(expected, mediaFile.Path);
        Assert.AreEqual(episode.EpisodeId, mediaFile.EpisodeId);
        var trackPaths = await fixture.Db.SubtitleTracks.Select(x => x.Path).ToListAsync();
        CollectionAssert.Contains(trackPaths, newSidecar);
        CollectionAssert.Contains(trackPaths, EmbeddedSubtitleExtractor.BuildSourcePrefix(expected) + "2");
        Assert.AreEqual(1, (await CanonicalProgressSeed.RowsAsync(fixture.Db, fixture.Anime.Id)).Count(x => x.EpisodeId == episode.EpisodeId));

        var operation = (await new OperationStore(fixture.Db).ListAsync(new OperationListFilter(Category: "Library"))).Single();
        Assert.AreEqual(OperationStatus.Succeeded, operation.Status);
        var logs = await new OperationStore(fixture.Db).ListLogsAsync(new OperationLogFilter(OperationId: operation.Id));
        Assert.IsTrue(logs.Any(log => log.Message.Contains(expected, StringComparison.Ordinal)), "The rename is recorded in the operation log.");

        var scan = await fixture.ScanAsync();
        Assert.AreEqual(0, scan.Discovered);
        Assert.AreEqual(0, scan.Removed);
        fixture.Db.ChangeTracker.Clear();
        Assert.AreEqual(episode.EpisodeId, (await fixture.Db.Episodes.SingleAsync()).Id);
        Assert.AreEqual(1, await fixture.Db.Anime.CountAsync());
        Assert.AreEqual(1, (await CanonicalProgressSeed.RowsAsync(fixture.Db, fixture.Anime.Id)).Count(x => x.EpisodeId == episode.EpisodeId));
        Assert.AreEqual(mediaFileId, (await fixture.Db.MediaFiles.SingleAsync()).Id, "The media file row (and its inventory) survives the rename.");
        Assert.AreEqual(1, await fixture.Db.MediaAnalyses.CountAsync(x => x.MediaFileId == mediaFileId));
        Assert.AreEqual(0, fixture.ProbeRunner.Calls.Count, "A rename must not trigger a new media analysis.");

        var again = await fixture.PlanAsync();
        Assert.AreEqual(AnimeRenameItemStatus.Unchanged, again.Items.Single().Status, "Renaming with the same profile is idempotent.");
    }

    [TestMethod]
    public async Task SeasonArtworkFollowsARenamedSeasonFolder()
    {
        await using var fixture = await RenameFixture.CreateAsync();
        var episode = await fixture.AddEpisodeFileAsync(1, "Frieren - S01E01 - Episode 1.mkv");
        var oldSeason = Path.GetDirectoryName(episode.MediaPath)!;
        await File.WriteAllTextAsync(Path.Combine(oldSeason, "poster.jpg"), "season poster");
        await File.WriteAllTextAsync(Path.Combine(fixture.SeriesFolder, "poster.jpg"), "series poster");
        await fixture.Naming.UpsertAsync(RenameFixture.SimpleProfile() with { SeasonFolderFormat = "Season {season:00}" });

        var plan = await fixture.PlanAsync();
        var result = await fixture.ExecuteAsync(plan);

        Assert.IsTrue(result.Success, result.Message);
        var newSeason = Path.Combine(fixture.SeriesFolder, "Season 01");
        Assert.AreEqual("season poster", await File.ReadAllTextAsync(Path.Combine(newSeason, "poster.jpg")));
        Assert.IsFalse(Directory.Exists(oldSeason), "The old season folder no longer holds anything and is removed.");
        Assert.AreEqual("series poster", await File.ReadAllTextAsync(Path.Combine(fixture.SeriesFolder, "poster.jpg")), "Series artwork stays in the series folder.");
    }

    [TestMethod]
    public async Task SonarrOwnedAnimeIsNeverRenamed()
    {
        await using var fixture = await RenameFixture.CreateAsync(managed: false);
        var episode = await fixture.AddEpisodeFileAsync(1, "Frieren 01.mkv");

        var plan = await fixture.PlanAsync();
        var item = plan.Items.Single();
        Assert.AreEqual(AnimeRenameItemStatus.Blocked, item.Status);
        StringAssert.Contains(item.Reason, "coexistence");

        var result = await fixture.ExecuteAsync(plan);
        Assert.IsFalse(result.Success);
        Assert.IsTrue(File.Exists(episode.MediaPath));
    }

    [TestMethod]
    public async Task MonitoredSonarrSeriesBlocksRenameThroughCanRename()
    {
        await using var fixture = await RenameFixture.CreateAsync();
        await fixture.AddEpisodeFileAsync(1, "Frieren 01.mkv");
        var state = await fixture.Ownership.UpdateAsync(current => current with
        {
            Anime = new Dictionary<string, AnimeManagementAssignment>(StringComparer.OrdinalIgnoreCase)
            {
                ["frieren"] = new("frieren", AnimeManagementMode.JularrManaged, DateTimeOffset.UtcNow, SonarrSeriesId: 7)
            }
        });
        var sonarr = SonarrObservedState.FromObservation(
            [new SonarrObservedSeries(7, "Frieren", fixture.SeriesFolder, Monitored: true)],
            [],
            [],
            [],
            DateTimeOffset.UtcNow);

        var plan = await fixture.Service.PlanAsync(fixture.Anime.Id, false, new AcquisitionOwnershipSnapshot(state, sonarr), DateTimeOffset.UtcNow);

        var item = plan!.Items.Single();
        Assert.AreEqual(AnimeRenameItemStatus.Blocked, item.Status);
        StringAssert.Contains(item.Reason, "Sonarr still monitors");
        Assert.IsFalse(plan.CanExecute);
    }

    [TestMethod]
    public async Task ReadOnlyLibraryIsRefusedBeforeAnyMove()
    {
        await using var fixture = await RenameFixture.CreateAsync();
        var episode = await fixture.AddEpisodeFileAsync(1, "Frieren 01.mkv");
        fixture.FileSystem.ReadOnly = true;

        var plan = await fixture.PlanAsync();

        Assert.IsFalse(plan.CanExecute);
        Assert.IsTrue(plan.BlockingReasons.Any(reason => reason.Contains("read-only", StringComparison.Ordinal)));
        var result = await fixture.ExecuteAsync(plan);
        Assert.IsFalse(result.Success);
        StringAssert.StartsWith(result.Message, "Nothing was renamed.");
        Assert.IsTrue(File.Exists(episode.MediaPath));
        Assert.AreEqual(0, fixture.FileSystem.Moves);
    }

    [TestMethod]
    public async Task DetectsExistingTargetsDuplicateTargetsAndCrossDeviceMoves()
    {
        await using var fixture = await RenameFixture.CreateAsync();
        await fixture.AddEpisodeFileAsync(1, "Frieren 01.mkv");
        var existingTarget = Path.Combine(fixture.SeriesFolder, "Season 1", "Frieren - S01E01 - Episode 1.mkv");
        await File.WriteAllBytesAsync(existingTarget, [1]);

        var blocked = (await fixture.PlanAsync()).Items.Single();
        Assert.AreEqual(AnimeRenameItemStatus.Blocked, blocked.Status);
        StringAssert.Contains(blocked.Reason, "already exists");

        File.Delete(existingTarget);
        await fixture.AddExtraFileForEpisodeAsync(1, "Frieren 01 v2.mkv");
        var duplicates = (await fixture.PlanAsync()).Items;
        Assert.AreEqual(2, duplicates.Count(item => item.Status == AnimeRenameItemStatus.Blocked));
        Assert.IsTrue(duplicates.All(item => item.Reason!.Contains("also be named", StringComparison.Ordinal)));

        await using var single = await RenameFixture.CreateAsync();
        await single.AddEpisodeFileAsync(1, "Frieren 01.mkv");
        await single.Naming.UpsertAsync(RenameFixture.SimpleProfile() with { SeasonFolderFormat = "Season {season:00}" });
        single.FileSystem.OtherVolumeDirectory = single.SeriesFolder;
        var crossDevice = (await single.PlanAsync()).Items.Single();
        Assert.AreEqual(AnimeRenameItemStatus.Blocked, crossDevice.Status);
        StringAssert.Contains(crossDevice.Reason, "another filesystem");
    }

    [TestMethod]
    public async Task CaseOnlyRenameWorksOnCaseInsensitiveAndSensitiveFilesystems()
    {
        await using var fixture = await RenameFixture.CreateAsync();
        var episode = await fixture.AddEpisodeFileAsync(1, "frieren - S01E01 - episode 1.mkv");

        var plan = await fixture.PlanAsync();
        var item = plan.Items.Single();
        Assert.AreEqual(AnimeRenameItemStatus.Rename, item.Status, item.Reason);

        var result = await fixture.ExecuteAsync(plan);

        Assert.IsTrue(result.Success, result.Message);
        var names = Directory.GetFiles(Path.GetDirectoryName(episode.MediaPath)!).Select(Path.GetFileName).ToArray();
        CollectionAssert.AreEqual(new[] { "Frieren - S01E01 - Episode 1.mkv" }, names);
    }

    [TestMethod]
    public async Task PartialFailureRollsBackEveryMoveAndLeavesRecordsUnchanged()
    {
        await using var fixture = await RenameFixture.CreateAsync();
        var first = await fixture.AddEpisodeFileAsync(1, "Frieren 01.mkv");
        var second = await fixture.AddEpisodeFileAsync(2, "Frieren 02.mkv");
        fixture.FileSystem.FailOnMove = 2;

        var plan = await fixture.PlanAsync();
        Assert.AreEqual(2, plan.RenameCount);
        var result = await fixture.ExecuteAsync(plan);

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Message, "rolled back");
        Assert.IsTrue(File.Exists(first.MediaPath));
        Assert.IsTrue(File.Exists(second.MediaPath));
        Assert.AreEqual(0, Directory.GetFiles(Path.GetDirectoryName(first.MediaPath)!).Count(path => path.Contains("S01E", StringComparison.Ordinal)));

        fixture.Db.ChangeTracker.Clear();
        var paths = await fixture.Db.MediaFiles.Select(x => x.Path).OrderBy(x => x).ToListAsync();
        CollectionAssert.AreEqual(new[] { first.MediaPath, second.MediaPath }, paths);
        var operation = (await new OperationStore(fixture.Db).ListAsync(new OperationListFilter(Category: "Library"))).Single();
        Assert.AreEqual(OperationStatus.Failed, operation.Status);
    }

    [TestMethod]
    public async Task SeriesFolderRenameRekeysAnimeAndOwnershipWithoutLosingEpisodes()
    {
        await using var fixture = await RenameFixture.CreateAsync();
        var episode = await fixture.AddEpisodeFileAsync(1, "Frieren - S01E01 - Episode 1.mkv");
        fixture.Db.AnimeMetadata.Add(new AnimeMetadata
        {
            AnimeId = fixture.Anime.Id,
            Provider = AniListMetadataProvider.ProviderKey,
            ExternalId = "154587",
            PreferredTitle = "Frieren",
            SeasonYear = 2023
        });
        await fixture.Db.SaveChangesAsync();
        await fixture.Naming.UpsertAsync(RenameFixture.SimpleProfile() with { SeriesFolderFormat = "{Series TitleYear}" });
        var now = DateTimeOffset.UtcNow;
        var acquisitionId = Guid.NewGuid();
        await fixture.Acquisitions.UpdateAsync(state => state.Blocklist.Add(new SabnzbdBlockedRelease("release-1", "Frieren - 02", "frieren", SabnzbdFailureKind.Download, "failed", null, now)));
        var requests = new AcquisitionAccessStore(fixture.Db);
        var inFlight = await requests.CreateAsync(
            new AcquisitionRequestDraft(MediaAcquisitionKind.Anime, "anilist", "154587", "Frieren", null, null, new AnimeRequestPayload("frieren", [new AnimeEpisodeKey("frieren", 1, 2)]).Serialize()),
            "owner",
            AcquisitionRequestStatus.Downloading,
            "owner",
            CancellationToken.None);
        await fixture.Monitoring.UpdateAsync(state => state with
        {
            Anime = new Dictionary<string, AnimeMonitorSettings>(StringComparer.OrdinalIgnoreCase)
            {
                ["frieren"] = new("frieren", true, TargetRootId: fixture.Root.Id)
            }
        });
        var importId = Guid.NewGuid();
        await fixture.Imports.UpsertAsync(new AnimeImportRecord(
            importId,
            Guid.NewGuid(),
            null,
            acquisitionId,
            "frieren",
            "Frieren",
            null,
            AnimeImportStatus.ManualRequired,
            [new AnimeImportFileRecord(episode.MediaPath, 1, AnimeImportFileStatus.Imported, [], [], [], 1, [], episode.MediaPath, null)],
            "needs a decision",
            now,
            now));

        var plan = await fixture.PlanAsync(renameSeriesFolder: true);
        Assert.AreEqual("frieren (2023)", plan.TargetAnimeKey);
        Assert.AreEqual(1, plan.FolderMoves.Count);
        Assert.IsTrue(plan.CanExecute, string.Join(" ", plan.BlockingReasons.Concat(plan.Items.Select(item => item.Reason ?? ""))));

        var result = await fixture.ExecuteAsync(plan);

        Assert.IsTrue(result.Success, result.Message);
        var target = Path.Combine(fixture.Root.Path, "Frieren (2023)", "Season 1", "Frieren - S01E01 - Episode 1.mkv");
        Assert.IsTrue(File.Exists(target));
        Assert.IsFalse(Directory.Exists(fixture.SeriesFolder));
        fixture.Db.ChangeTracker.Clear();
        Assert.AreEqual("frieren (2023)", (await fixture.Db.Anime.SingleAsync()).Key);
        var ownership = await fixture.Ownership.LoadAsync();
        Assert.IsTrue(ownership.Anime.ContainsKey("frieren (2023)"));
        Assert.IsFalse(ownership.Anime.ContainsKey("frieren"));
        var payload = AnimeRequestPayload.Of((await requests.GetAsync(inFlight.Id, CancellationToken.None))!);
        Assert.AreEqual("frieren (2023)", payload.AnimeKey, "In-flight downloads follow the new anime key.");
        Assert.AreEqual("frieren (2023)", payload.Episodes!.Single().AnimeKey);
        Assert.AreEqual("frieren (2023)", (await fixture.Acquisitions.LoadAsync()).Blocklist.Single().AnimeKey);
        var monitoring = await fixture.Monitoring.LoadAsync();
        Assert.IsTrue(monitoring.Anime["frieren (2023)"].SearchOnAdd, "Monitoring settings follow the new anime key.");
        Assert.IsFalse(monitoring.Anime.ContainsKey("frieren"));
        Assert.AreEqual(fixture.Root.Id, monitoring.Anime["frieren (2023)"].TargetRootId, "The target root assignment follows the new anime key.");
        var import = await fixture.Imports.GetAsync(importId);
        Assert.AreEqual("frieren (2023)", import!.AnimeKey, "Pending manual imports follow the new anime key.");
        Assert.AreEqual(target, import.Files.Single().ImportedPath);

        var scan = await fixture.ScanAsync();
        Assert.AreEqual(0, scan.Removed);
        fixture.Db.ChangeTracker.Clear();
        Assert.AreEqual(1, await fixture.Db.Anime.CountAsync());
        Assert.AreEqual(episode.EpisodeId, (await fixture.Db.Episodes.SingleAsync()).Id);
        Assert.AreEqual(1, (await CanonicalProgressSeed.RowsAsync(fixture.Db, fixture.Anime.Id)).Count);
    }

    [TestMethod]
    public async Task NamesThatWouldChangeEpisodeIdentityAreBlocked()
    {
        await using var fixture = await RenameFixture.CreateAsync();
        await fixture.AddEpisodeFileAsync(1, "Frieren 01.mkv");
        await fixture.AddEpisodeFileAsync(2, "Frieren 02.mkv");
        await fixture.AddEpisodeFileAsync(1, "Frieren S02E01.mkv", season: 2);
        await fixture.Naming.UpsertAsync(RenameFixture.SimpleProfile() with
        {
            UseSeasonFolders = false,
            AnimeEpisodeFormat = "{Series Title} - {absolute:000}"
        });

        var plan = await fixture.PlanAsync();
        var seasonTwo = plan.Items.Single(item => item.SeasonNumber == 2);

        Assert.AreEqual(AnimeRenameItemStatus.Blocked, seasonTwo.Status);
        StringAssert.Contains(seasonTwo.Reason, "would be scanned as S01E03");
    }

    [TestMethod]
    public async Task RunningAcquisitionImportBlocksRename()
    {
        await using var fixture = await RenameFixture.CreateAsync();
        await fixture.AddEpisodeFileAsync(1, "Frieren 01.mkv");
        var store = new OperationStore(fixture.Db);
        var importId = await store.CreateAsync(new OperationDescriptor(AnimeImportExecutor.OperationKind, AnimeImportExecutor.OperationCategory, "Anime import"));
        await store.MarkRunningAsync(importId);

        var plan = await fixture.PlanAsync();

        Assert.IsFalse(plan.CanExecute);
        Assert.IsTrue(plan.BlockingReasons.Any(reason => reason.Contains("Anime import", StringComparison.Ordinal)));
        await store.MarkSucceededAsync(importId, "done");
        Assert.IsTrue((await fixture.PlanAsync()).CanExecute);
    }

    [TestMethod]
    public async Task RunningLibraryScanBlocksRename()
    {
        await using var fixture = await RenameFixture.CreateAsync();
        var episode = await fixture.AddEpisodeFileAsync(1, "Frieren 01.mkv");
        var store = new OperationStore(fixture.Db);
        var scanId = await store.CreateAsync(new OperationDescriptor("library-scan", "Library", "Scan library", fixture.Root.Name));
        await store.MarkRunningAsync(scanId);

        var plan = await fixture.PlanAsync();

        Assert.IsFalse(plan.CanExecute);
        Assert.IsTrue(plan.BlockingReasons.Any(reason => reason.Contains("Scan library", StringComparison.Ordinal)));
        var result = await fixture.ExecuteAsync(plan);
        Assert.IsFalse(result.Success);
        Assert.IsTrue(File.Exists(episode.MediaPath));

        await store.MarkSucceededAsync(scanId, "done");
        Assert.IsTrue((await fixture.PlanAsync()).CanExecute);
    }

    [TestMethod]
    public async Task ExecutionRefusesAPlanThatChangedSinceThePreview()
    {
        await using var fixture = await RenameFixture.CreateAsync();
        var episode = await fixture.AddEpisodeFileAsync(1, "Frieren 01.mkv");

        var result = await fixture.Service.ExecuteAsync(
            fixture.Anime.Id,
            false,
            "stale-fingerprint",
            await fixture.SnapshotAsync(),
            DateTimeOffset.UtcNow);

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Message, "changed since the preview");
        Assert.IsTrue(File.Exists(episode.MediaPath));
    }

    [TestMethod]
    public void RekeyOwnershipMovesAssignmentJobsAndOwnedPaths()
    {
        var now = DateTimeOffset.UtcNow;
        var oldPath = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "lib", "Frieren", "a.mkv"));
        var newPath = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "lib", "Frieren (2023)", "b.mkv"));
        var state = SonarrParallelSafety.RegisterPath(
            SonarrParallelSafety.RegisterJob(
                SonarrParallelSafety.SetMode(AcquisitionOwnershipState.Empty(), "frieren", AnimeManagementMode.JularrManaged, now),
                new AcquisitionOwnership("job-1", "frieren", AcquisitionOwner.Jularr, "release", AcquisitionOwnershipStatus.Completed, now)),
            new ManagedMediaPath(oldPath, "frieren", AcquisitionOwner.Jularr, "job-1", now));

        var updated = AnimeRenameService.RekeyOwnership(state, "frieren", "frieren (2023)", path => path == oldPath ? newPath : path);

        Assert.AreEqual(AnimeManagementMode.JularrManaged, updated.Anime["frieren (2023)"].Mode);
        Assert.IsFalse(updated.Anime.ContainsKey("frieren"));
        Assert.AreEqual("frieren (2023)", updated.Jobs["job-1"].AnimeKey);
        Assert.AreEqual("frieren (2023)", updated.Paths[SonarrParallelSafety.NormalizePath(newPath)].AnimeKey);
        Assert.IsFalse(updated.Paths.ContainsKey(oldPath));
        Assert.AreSame(state, AnimeRenameService.RekeyOwnership(state, "other", "other", path => path));
    }

    private sealed class RenameFixture : IAsyncDisposable
    {
        private RenameFixture(
            string tempRoot,
            DbContextOptions<AppDbContext> options,
            AppDbContext db,
            LibraryRoot root,
            Anime anime)
        {
            TempRoot = tempRoot;
            Options = options;
            Db = db;
            Root = root;
            Anime = anime;
            Naming = new AnimeNamingProfileStore(new DirectoryInfo(Path.Combine(tempRoot, "acquisition")));
            Ownership = new AcquisitionOwnershipStore(tempRoot);
            var protection = DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(tempRoot, "keys")));
            Acquisitions = new SabnzbdAcquisitionStore(new DirectoryInfo(Path.Combine(tempRoot, "acquisition")));
            FileSystem = new TestFileSystem();
            Monitoring = new AnimeMonitoringStore(tempRoot);
            Imports = new AnimeImportStore(new DirectoryInfo(Path.Combine(tempRoot, "acquisition")));
            var services = new ServiceCollection().AddSingleton(db).BuildServiceProvider();
            var observation = new SonarrObservationService(
                new SonarrConnectionStore(protection),
                new UnusedObserverClient(),
                Ownership,
                new AnimeImportSettingsStore(tempRoot),
                NullLogger<SonarrObservationService>.Instance);
            Service = new AnimeRenameService(
                db,
                Naming,
                Ownership,
                Acquisitions,
                new AcquisitionAccessStore(db),
                Monitoring,
                Imports,
                observation,
                new OperationRunner(db, services),
                FileSystem,
                NullLogger<AnimeRenameService>.Instance);
        }

        public string TempRoot { get; }
        public DbContextOptions<AppDbContext> Options { get; }
        public FakeMediaProbeRunner ProbeRunner { get; } = new();
        public SabnzbdAcquisitionStore Acquisitions { get; }
        public AnimeMonitoringStore Monitoring { get; }
        public AnimeImportStore Imports { get; }
        public AppDbContext Db { get; }
        public LibraryRoot Root { get; }
        public Anime Anime { get; }
        public AnimeNamingProfileStore Naming { get; }
        public AcquisitionOwnershipStore Ownership { get; }
        public TestFileSystem FileSystem { get; }
        public AnimeRenameService Service { get; }
        public string SeriesFolder => Path.Combine(Root.Path, "Frieren");

        public static AnimeNamingProfile SimpleProfile() =>
            new(
                SimpleProfileId,
                "Simple",
                "{Series Title}",
                "Season {season}",
                "Specials",
                "{Series Title} - S{season:00}E{episode:00} - {Episode Title}",
                "{Series Title} - {Air-Date} - {Episode Title}",
                "{Series Title} - S{season:00}E{episode:00} - {Episode Title}",
                UseSeasonFolders: true,
                AnimeMultiEpisodeStyle.PrefixedRange,
                ReplaceIllegalCharacters: true,
                AnimeColonReplacement.Smart);

        public static async Task<RenameFixture> CreateAsync(bool managed = true)
        {
            var tempRoot = Path.Combine(Path.GetTempPath(), $"jularr-rename-{Guid.NewGuid():N}");
            var libraryPath = Path.Combine(tempRoot, "anime");
            Directory.CreateDirectory(libraryPath);
            Directory.CreateDirectory(Path.Combine(tempRoot, "dictionary"));
            await File.WriteAllTextAsync(Path.Combine(tempRoot, "dictionary", "jmdict-ger.tsv"), "");
            await File.WriteAllTextAsync(Path.Combine(tempRoot, "dictionary", "jmdict-eng-common.tsv"), "");

            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite($"Data Source={Path.Combine(tempRoot, "jularr.db")};Foreign Keys=True")
                .Options;
            var db = new AppDbContext(options);
            await DatabaseMigrationBridge.UpgradeAsync(db);

            var root = new LibraryRoot { Name = "Anime", Path = libraryPath };
            var anime = new Anime { Key = "frieren", Title = "Frieren" };
            db.LibraryRoots.Add(root);
            db.LibraryRootContentAssignments.Add(new LibraryRootContentAssignment { LibraryRootId = root.Id, ContentType = LibraryContentType.Anime });
            db.Anime.Add(anime);
            await db.SaveChangesAsync();

            var fixture = new RenameFixture(tempRoot, options, db, root, anime);
            await fixture.Naming.UpsertAsync(SimpleProfile());
            await fixture.Naming.SetDefaultAsync(SimpleProfileId);
            if (managed)
            {
                await fixture.Ownership.UpdateAsync(state =>
                    SonarrParallelSafety.SetMode(state, "frieren", AnimeManagementMode.JularrManaged, DateTimeOffset.UtcNow));
            }

            return fixture;
        }

        public async Task<(Guid EpisodeId, string MediaPath)> AddEpisodeFileAsync(
            int number,
            string fileName,
            bool sidecar = false,
            int season = 1)
        {
            var directory = Path.Combine(SeriesFolder, $"Season {season}");
            Directory.CreateDirectory(directory);
            var mediaPath = Path.Combine(directory, fileName);
            await File.WriteAllBytesAsync(mediaPath, [0]);

            var episode = new Episode { AnimeId = Anime.Id, SeasonNumber = season, Number = number, Title = $"Episode {number}" };
            Db.Episodes.Add(episode);
            Db.MediaFiles.Add(new MediaFile
            {
                LibraryRootId = Root.Id,
                EpisodeId = episode.Id,
                Path = mediaPath,
                SizeBytes = 1,
                LastWriteTimeUtc = File.GetLastWriteTimeUtc(mediaPath)
            });

            if (sidecar)
            {
                var sidecarPath = Path.Combine(directory, Path.GetFileNameWithoutExtension(fileName) + ".ja.srt");
                await File.WriteAllTextAsync(sidecarPath, "1\n00:00:01,000 --> 00:00:02,000\nこんにちは\n");
                Db.SubtitleTracks.Add(new SubtitleTrack { EpisodeId = episode.Id, Path = sidecarPath, Language = "ja", Format = "srt", SourceUpdatedAt = File.GetLastWriteTimeUtc(sidecarPath) });
                Db.SubtitleTracks.Add(new SubtitleTrack { EpisodeId = episode.Id, Path = EmbeddedSubtitleExtractor.BuildSourcePrefix(mediaPath) + "2", Language = "ja", Format = "ass", SourceUpdatedAt = DateTime.UtcNow });
            }

            await Db.SaveChangesAsync();
            await CanonicalProgressSeed.SetAsync(Db, "owner", episode.Id, 90_000, null, false);
            return (episode.Id, mediaPath);
        }

        public async Task<Guid> SeedMediaAnalysisAsync(Guid episodeId)
        {
            var mediaFile = await Db.MediaFiles.SingleAsync(x => x.EpisodeId == episodeId);
            Db.MediaAnalyses.Add(new MediaAnalysis
            {
                MediaFileId = mediaFile.Id,
                Status = MediaAnalysisStatus.Succeeded,
                ProbeVersion = MediaInventoryService.CurrentProbeVersion,
                SourceSizeBytes = mediaFile.SizeBytes,
                SourceLastWriteTimeUtc = mediaFile.LastWriteTimeUtc,
                Container = "matroska"
            });
            await Db.SaveChangesAsync();
            return mediaFile.Id;
        }

        public async Task AddExtraFileForEpisodeAsync(int number, string fileName)
        {
            var episode = await Db.Episodes.SingleAsync(x => x.Number == number && x.SeasonNumber == 1);
            var mediaPath = Path.Combine(SeriesFolder, "Season 1", fileName);
            await File.WriteAllBytesAsync(mediaPath, [0]);
            Db.MediaFiles.Add(new MediaFile { LibraryRootId = Root.Id, EpisodeId = episode.Id, Path = mediaPath, SizeBytes = 1, LastWriteTimeUtc = DateTime.UtcNow });
            await Db.SaveChangesAsync();
        }

        public async Task<AcquisitionOwnershipSnapshot> SnapshotAsync() =>
            new(await Ownership.LoadAsync(), SonarrObservedState.NotConfigured);

        public async Task<AnimeRenamePlan> PlanAsync(bool renameSeriesFolder = false) =>
            (await Service.PlanAsync(Anime.Id, renameSeriesFolder, await SnapshotAsync(), DateTimeOffset.UtcNow))!;

        public async Task<AnimeRenameResult> ExecuteAsync(AnimeRenamePlan plan) =>
            await Service.ExecuteAsync(Anime.Id, plan.RenameSeriesFolder, plan.Fingerprint, await SnapshotAsync(), DateTimeOffset.UtcNow);

        public async Task<ScanResult> ScanAsync()
        {
            var vocabulary = new VocabularyService(
                Db,
                new JapaneseTermExtractor(new EmptyMorphology()),
                new JapaneseDictionary(Path.Combine(TempRoot, "dictionary")));
            var sonarrStore = new SonarrConnectionStore(DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(TempRoot, "keys"))));
            var inventory = MediaInventoryTestSupport.Create(Options, ProbeRunner);
            var scanner = new LibraryScanner(
                Db,
                new SubtitleImportService(Db, vocabulary),
                new EmbeddedSubtitleExtractor(
                    new MediaProcessRunner(NullLogger<MediaProcessRunner>.Instance),
                    inventory,
                    NullLogger<EmbeddedSubtitleExtractor>.Instance),
                inventory,
                new SonarrArtworkSyncService(
                    sonarrStore,
                    new SonarrArtworkImportService(Db, new TestHttpClientFactory(), NullLogger<SonarrArtworkImportService>.Instance),
                    NullLogger<SonarrArtworkSyncService>.Instance),
                NullLogger<LibraryScanner>.Instance);
            return await scanner.ScanAsync(Root.Id, CancellationToken.None);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            if (Directory.Exists(TempRoot))
            {
                Directory.Delete(TempRoot, recursive: true);
            }
        }
    }

    private sealed class TestFileSystem : AnimeRenameFileSystem
    {
        public bool ReadOnly { get; set; }
        public string? OtherVolumeDirectory { get; set; }
        public int FailOnMove { get; set; }
        public int Moves { get; private set; }

        public override void MoveFile(string sourcePath, string targetPath)
        {
            Moves++;
            if (Moves == FailOnMove)
            {
                throw new IOException("Simulated NAS disconnect.");
            }

            base.MoveFile(sourcePath, targetPath);
        }

        public override void MoveDirectory(string sourcePath, string targetPath)
        {
            Moves++;
            base.MoveDirectory(sourcePath, targetPath);
        }

        public override string? GetWriteBlocker(string directory) =>
            ReadOnly ? $"'{directory}' is mounted read-only." : base.GetWriteBlocker(directory);

        public override string GetVolumeKey(string path) =>
            OtherVolumeDirectory is not null && string.Equals(path, OtherVolumeDirectory, StringComparison.Ordinal)
                ? "other-volume"
                : base.GetVolumeKey(path);
    }

    private sealed class UnusedObserverClient : ISonarrObserverClient
    {
        public Task<IReadOnlyList<SonarrObservedSeries>> GetSeriesAsync(SonarrConnectionSettings settings, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<SonarrObservedEpisodeFile>> GetEpisodeFilesAsync(SonarrConnectionSettings settings, int seriesId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<SonarrObservedQueueItem>> GetQueueAsync(SonarrConnectionSettings settings, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<SonarrObservedHistoryEvent>> GetRecentHistoryAsync(SonarrConnectionSettings settings, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class TestHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private sealed class EmptyMorphology : IJapaneseMorphology
    {
        public IReadOnlyList<JapaneseMorphToken> Analyze(string text) => [];
    }
}
