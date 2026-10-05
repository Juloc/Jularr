using System.Data.Common;
using Jularr.Web.Data;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.Library;
using Jularr.Web.Features.MediaMapping;
using Jularr.Web.Features.Metadata;
using Jularr.Web.Features.Operations;
using Jularr.Web.Features.Sonarr;
using Jularr.Web.Features.Storage;
using Jularr.Web.Features.Subtitles;
using Jularr.Web.Features.Tracking;
using Jularr.Web.Features.Vocabulary;
using Jularr.Web.Infrastructure;
using Jularr.Web.Pages.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jularr.Tests;

// Covers issue #133 (per-anime rescan, identify and metadata repair tools): a rescan goes through
// the one library scan coordinator as a folder-scoped run and honours its guard, re-analysis
// invalidates only the target anime's media inventory, local refresh stays distinct from the
// filesystem scan, identify surfaces scored candidates without applying anything, a manual match
// is never overwritten automatically, and the repair page is owner-only.
[TestClass]
public sealed class AnimeRepairServiceTests
{
    [TestMethod]
    public async Task RescanGoesThroughTheCoordinatorAsAFolderScopedRun()
    {
        await using var host = await AnimeRepairTestHost.CreateAsync();
        var root = await host.AddRootAsync("Anime");
        host.WriteMedia(Path.Combine("Frieren", "Season 01", "Frieren - S01E01.mkv"));
        host.WriteMedia(Path.Combine("Bocchi", "Season 01", "Bocchi - S01E01.mkv"));

        await host.StartWorkerAsync();
        var initial = await host.Scans.QueueAsync(new LibraryScanRequest(root.Id, LibraryScanTrigger.Manual));
        await host.WaitForAsync(initial.OperationId!.Value, x => x.Status == OperationStatus.Succeeded);

        var animeId = await host.GetAnimeIdAsync("Frieren");

        var result = await host.Repair.RescanFolderAsync(animeId, "owner", CancellationToken.None);
        Assert.IsTrue(result.Queued);
        Assert.IsNotNull(result.OperationId);

        var operation = await host.WaitForAsync(result.OperationId!.Value, x => x.Status == OperationStatus.Succeeded);
        Assert.AreEqual(LibraryScanCoordinator.OperationKind, operation.Kind, "Rescan must reuse the one scan operation kind.");
        Assert.AreEqual("owner", operation.ProfileId);

        var details = LibraryScanDetails.TryParse(operation.Details);
        Assert.IsNotNull(details);
        Assert.AreEqual(root.Id, details.RootId);
        CollectionAssert.AreEqual(new[] { "Frieren" }, details.Folders!.ToArray());
        Assert.AreEqual(LibraryScanTrigger.Manual, details.Trigger);

        // Only Frieren's folder was in scope; Bocchi's media was left alone.
        Assert.AreEqual(2, await host.CountMediaFilesAsync());
    }

    [TestMethod]
    public async Task RescanReturnsNoFolderWhenTheAnimeHasNoKnownMedia()
    {
        await using var host = await AnimeRepairTestHost.CreateAsync();
        var animeId = await host.AddBareAnimeAsync("Orphan");

        var result = await host.Repair.RescanFolderAsync(animeId, "owner", CancellationToken.None);

        Assert.IsFalse(result.Queued);
        Assert.IsNull(result.OperationId);
    }

    [TestMethod]
    public async Task RescanRespectsTheRootsActiveScanGuard()
    {
        await using var host = await AnimeRepairTestHost.CreateAsync();
        var root = await host.AddRootAsync("Anime");
        var frierenPath = host.WriteMedia(Path.Combine("Frieren", "Season 01", "Frieren - S01E01.mkv"));

        await host.StartWorkerAsync();
        var initial = await host.Scans.QueueAsync(new LibraryScanRequest(root.Id, LibraryScanTrigger.Manual));
        await host.WaitForAsync(initial.OperationId!.Value, x => x.Status == OperationStatus.Succeeded);
        var animeId = await host.GetAnimeIdAsync("Frieren");

        // The scan's own discovery queues a background learning-text preparation batch on the
        // same worker; let it finish so it cannot itself occupy the single test worker while the
        // gate below is meant to hold the *next* scan instead.
        await host.WaitForNoActiveOperationsOfKindAsync("learning-text-batch");

        // Change the file so the second scan has something to re-analyse, then hold that second
        // full-root run inside its media-analysis phase.
        File.WriteAllBytes(frierenPath, [0x00, 0x01]);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Probe.Gate = gate.Task;
        host.Probe.ClearCalls();
        var running = await host.Scans.QueueAsync(new LibraryScanRequest(root.Id, LibraryScanTrigger.Manual));
        Assert.AreEqual(LibraryScanQueueOutcome.Queued, running.Outcome, running.Message);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (host.Probe.Calls.Count == 0 || !host.Scans.GetActive(root.Id).Any(x => x.IsRunning))
        {
            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail("The second full-root scan never reached its gated media-analysis phase.");
            }

            await Task.Delay(25);
        }

        var result = await host.Repair.RescanFolderAsync(animeId, "owner", CancellationToken.None);

        Assert.IsFalse(result.Queued, "A folder-scoped rescan must not bypass the root's active-run guard.");
        Assert.AreEqual(LibraryScanQueueOutcome.AlreadyActive, result.Outcome);
        Assert.AreEqual(running.OperationId, result.OperationId);

        gate.SetResult();
        await host.WaitForAsync(running.OperationId!.Value, x => x.Status == OperationStatus.Succeeded);
    }

    [TestMethod]
    public async Task ReanalyzeInvalidatesOnlyTheTargetAnimesMediaFiles()
    {
        await using var host = await AnimeRepairTestHost.CreateAsync();
        var root = await host.AddRootAsync("Anime");
        var frierenPath = host.WriteMedia(Path.Combine("Frieren", "Season 01", "Frieren - S01E01.mkv"));
        var bocchiPath = host.WriteMedia(Path.Combine("Bocchi", "Season 01", "Bocchi - S01E01.mkv"));
        host.Probe.Returns(frierenPath, MediaProbeFixtures.H264Stereo);
        host.Probe.Returns(bocchiPath, MediaProbeFixtures.H264Stereo);

        await host.StartWorkerAsync();
        var initial = await host.Scans.QueueAsync(new LibraryScanRequest(root.Id, LibraryScanTrigger.Manual));
        await host.WaitForAsync(initial.OperationId!.Value, x => x.Status == OperationStatus.Succeeded);

        var frierenId = await host.GetAnimeIdAsync("Frieren");
        var (frierenBefore, bocchiBefore) = await host.GetAnalyzedAtAsync(frierenPath, bocchiPath);

        host.Probe.ClearCalls();
        var result = await host.Repair.ReanalyzeMediaAsync(frierenId, CancellationToken.None);

        Assert.AreEqual(1, result.MediaFilesConsidered);
        Assert.AreEqual(1, result.Analyzed);
        Assert.AreEqual(1, host.Probe.Calls.Count(x => string.Equals(x, frierenPath, StringComparison.Ordinal)));
        Assert.IsFalse(host.Probe.Calls.Any(x => string.Equals(x, bocchiPath, StringComparison.Ordinal)),
            "Re-analysing one anime must never re-probe another anime's media.");

        var (frierenAfter, bocchiAfter) = await host.GetAnalyzedAtAsync(frierenPath, bocchiPath);
        Assert.IsTrue(frierenAfter > frierenBefore, "The invalidated anime's analysis must be refreshed.");
        Assert.AreEqual(bocchiBefore, bocchiAfter, "An anime outside the request must keep its existing analysis untouched.");
    }

    [TestMethod]
    public async Task RefreshLocalReimportsSubtitlesAndNfoWithoutTouchingMediaFilesOrMetadata()
    {
        await using var host = await AnimeRepairTestHost.CreateAsync();
        var root = await host.AddRootAsync("Anime");
        var mediaPath = host.WriteMedia(Path.Combine("Frieren", "Season 01", "Frieren - S01E01.mkv"));

        await host.StartWorkerAsync();
        var initial = await host.Scans.QueueAsync(new LibraryScanRequest(root.Id, LibraryScanTrigger.Manual));
        await host.WaitForAsync(initial.OperationId!.Value, x => x.Status == OperationStatus.Succeeded);

        var animeId = await host.GetAnimeIdAsync("Frieren");
        var mediaFileCountBefore = await host.CountMediaFilesAsync();

        // The scan's own discovery queues a background learning-text batch that would import the
        // sidecar dropped below on its own. Let it settle so this test exercises only the repair
        // action; the repair-vs-background race itself is covered by the concurrency test (#601).
        await host.WaitForNoActiveOperationsOfKindAsync("learning-text-batch");

        // Simulate the user dropping sidecar files after the scan, without triggering a rescan.
        await File.WriteAllTextAsync(
            Path.Combine(host.LibraryPath, "Frieren", "tvshow.nfo"),
            "<tvshow><title>Sousou no Frieren</title></tvshow>");
        await File.WriteAllTextAsync(
            Path.ChangeExtension(mediaPath, null) + ".ja.srt",
            "1\n00:00:01,000 --> 00:00:03,000\n猫が走る\n");

        var result = await host.Repair.RefreshLocalAsync(animeId, CancellationToken.None);

        Assert.AreEqual(1, result.EpisodesConsidered);
        Assert.AreEqual(1, result.SubtitlesImported);

        var animeTitle = await host.GetAnimeTitleAsync(animeId);
        Assert.AreEqual("Sousou no Frieren", animeTitle);

        Assert.AreEqual(mediaFileCountBefore, await host.CountMediaFilesAsync(),
            "Refreshing local subtitles/NFO/artwork must never add, update or remove MediaFiles rows.");
        Assert.AreEqual(0, await host.CountAnimeMetadataAsync(),
            "Refreshing local files is distinct from a metadata refresh/match.");
        Assert.AreEqual(1, await host.CountSubtitleTracksAsync(animeId));
    }

    [TestMethod]
    public async Task RefreshLocalIsIdempotentWhenRepeatedForTheSameSidecar()
    {
        await using var host = await AnimeRepairTestHost.CreateAsync();
        var root = await host.AddRootAsync("Anime");
        var mediaPath = host.WriteMedia(Path.Combine("Frieren", "Season 01", "Frieren - S01E01.mkv"));

        await host.StartWorkerAsync();
        var initial = await host.Scans.QueueAsync(new LibraryScanRequest(root.Id, LibraryScanTrigger.Manual));
        await host.WaitForAsync(initial.OperationId!.Value, x => x.Status == OperationStatus.Succeeded);
        await host.WaitForNoActiveOperationsOfKindAsync("learning-text-batch");
        var animeId = await host.GetAnimeIdAsync("Frieren");

        host.WriteSidecar(mediaPath, "猫が走る", "犬が歩く");

        // Each refresh is its own action with its own DbContext, so the second one must recognise
        // the sidecar from what the database stored (which keeps microseconds, not file ticks).
        var first = await host.RefreshLocalInNewScopeAsync(animeId);
        var tracksAfterFirst = await host.GetSubtitleTracksAsync();

        var second = await host.RefreshLocalInNewScopeAsync(animeId);
        var tracksAfterSecond = await host.GetSubtitleTracksAsync();

        Assert.AreEqual(1, first.SubtitlesImported);
        Assert.AreEqual(1, second.SubtitlesImported, "An unchanged sidecar is still the episode's learning text.");
        Assert.AreEqual(1, tracksAfterFirst.Count);
        Assert.AreEqual(1, tracksAfterSecond.Count, "Repeating the reimport must not add a second track for the same Path.");
        Assert.AreEqual(tracksAfterFirst[0].Id, tracksAfterSecond[0].Id);
        Assert.AreEqual(2, tracksAfterSecond[0].CueCount);
        Assert.AreEqual(
            tracksAfterFirst[0].ImportedAt,
            tracksAfterSecond[0].ImportedAt,
            "An unchanged sidecar must not be re-imported.");
    }

    // Issue #601: IX_SubtitleTracks_Path (Npgsql 23505) was violated when two imports of the same
    // sidecar Path ran at once - the repair action, a scan and the background learning-text batch
    // can all reach the same file - because each looked the Path up, saw no track and inserted one.
    // The lookup delay widens that read-then-write window so any non-atomic import collides here
    // every time, instead of once in a while.
    [TestMethod]
    public async Task ConcurrentRefreshLocalReimportsNeverDuplicateSubtitleTracksOrCues()
    {
        const int episodeCount = 6;
        const int racers = 3;

        await using var host = await AnimeRepairTestHost.CreateAsync();
        var root = await host.AddRootAsync("Anime");
        var mediaPaths = Enumerable.Range(1, episodeCount)
            .Select(number => host.WriteMedia(Path.Combine("Frieren", "Season 01", $"Frieren - S01E{number:00}.mkv")))
            .ToArray();

        await host.StartWorkerAsync();
        var initial = await host.Scans.QueueAsync(new LibraryScanRequest(root.Id, LibraryScanTrigger.Manual));
        await host.WaitForAsync(initial.OperationId!.Value, x => x.Status == OperationStatus.Succeeded);
        await host.WaitForNoActiveOperationsOfKindAsync("learning-text-batch");
        var animeId = await host.GetAnimeIdAsync("Frieren");

        // First import: no track exists yet, so every racer takes the insert path.
        foreach (var mediaPath in mediaPaths)
        {
            host.WriteSidecar(mediaPath, "猫が走る", "犬が歩く");
        }

        host.SubtitleLookupDelay = TimeSpan.FromMilliseconds(50);
        var firstRound = await host.RefreshLocalConcurrentlyAsync(animeId, racers);

        Assert.IsTrue(firstRound.All(x => x.SubtitlesImported == episodeCount));
        var tracks = await host.GetSubtitleTracksAsync();
        Assert.AreEqual(episodeCount, tracks.Count, "Exactly one track per sidecar Path.");
        Assert.AreEqual(episodeCount, tracks.Select(x => x.Path).Distinct(StringComparer.Ordinal).Count());
        Assert.IsTrue(tracks.All(x => x.CueCount == 2), "Each track carries the sidecar's cues exactly once.");

        // Changed sidecars: every racer now takes the update path against an existing track.
        var changedStamp = new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        for (var index = 0; index < mediaPaths.Length; index++)
        {
            host.WriteSidecar(mediaPaths[index], changedStamp.AddMinutes(index), "猫が走る", "犬が歩く", "鳥が飛ぶ");
        }

        var secondRound = await host.RefreshLocalConcurrentlyAsync(animeId, racers);

        Assert.IsTrue(secondRound.All(x => x.SubtitlesImported == episodeCount));
        var updated = await host.GetSubtitleTracksAsync();
        Assert.AreEqual(episodeCount, updated.Count, "Reimporting a changed sidecar updates its track instead of adding one.");
        CollectionAssert.AreEquivalent(
            tracks.Select(x => x.Id).ToArray(),
            updated.Select(x => x.Id).ToArray(),
            "The existing tracks are matched by Path and updated in place.");
        Assert.IsTrue(updated.All(x => x.CueCount == 3), "Concurrent cue replacement must not duplicate cues.");
    }

    // A media file re-linked to another episode keeps its sidecar Path, so the existing track must
    // follow it instead of staying on the old episode (and never gaining a second row for the Path).
    // The sidecar's stamp is fixed and unchanged throughout: "same timestamp" alone must not keep a
    // track that belongs to a different episode.
    [TestMethod]
    public async Task RefreshLocalMovesAnExistingTrackToTheEpisodeNowOwningTheSidecar()
    {
        await using var host = await AnimeRepairTestHost.CreateAsync();
        var root = await host.AddRootAsync("Anime");
        var firstMedia = host.WriteMedia(Path.Combine("Frieren", "Season 01", "Frieren - S01E01.mkv"));
        host.WriteMedia(Path.Combine("Frieren", "Season 01", "Frieren - S01E02.mkv"));

        await host.StartWorkerAsync();
        var initial = await host.Scans.QueueAsync(new LibraryScanRequest(root.Id, LibraryScanTrigger.Manual));
        await host.WaitForAsync(initial.OperationId!.Value, x => x.Status == OperationStatus.Succeeded);
        await host.WaitForNoActiveOperationsOfKindAsync("learning-text-batch");
        var animeId = await host.GetAnimeIdAsync("Frieren");

        host.WriteSidecar(firstMedia, new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc), "猫が走る", "犬が歩く");
        await host.RefreshLocalInNewScopeAsync(animeId);

        var firstEpisodeId = await host.GetEpisodeIdAsync(animeId, 1);
        var secondEpisodeId = await host.GetEpisodeIdAsync(animeId, 2);
        var before = await host.GetSubtitleTracksAsync();
        Assert.AreEqual(1, before.Count);
        Assert.AreEqual(firstEpisodeId, before[0].EpisodeId);

        await host.MoveMediaFileToEpisodeAsync(firstMedia, secondEpisodeId);
        await host.RefreshLocalInNewScopeAsync(animeId);
        await host.RefreshLocalInNewScopeAsync(animeId);

        var after = await host.GetSubtitleTracksAsync();
        Assert.AreEqual(1, after.Count, "The Path still has exactly one track.");
        Assert.AreEqual(before[0].Id, after[0].Id);
        Assert.AreEqual(secondEpisodeId, after[0].EpisodeId, "The track follows its media file to the new episode.");
        Assert.AreEqual(2, after[0].CueCount);
    }

    [TestMethod]
    public async Task IdentifyReturnsScoredCandidatesAndOnlyAppliesOnExplicitConfirmation()
    {
        await using var host = await AnimeRepairTestHost.CreateAsync();
        var animeId = await host.AddBareAnimeAsync("Sousou no Frieren", episodeCount: 28, seasonCount: 1);
        host.Provider.Add(new AnimeMetadataCandidate(
            AniListMetadataProvider.ProviderKey, "154587", "Sousou no Frieren",
            "Sousou no Frieren", "Frieren: Beyond Journey's End", "葬送のフリーレン",
            null, null, null, "TV", "FINISHED", "FALL", 2023, 28, 24));

        var candidates = await host.Repair.SearchCandidatesAsync(animeId, null, CancellationToken.None);

        Assert.AreEqual(1, candidates.Count);
        Assert.IsTrue(candidates[0].Score > 0, "A title match must produce a positive score.");
        Assert.IsTrue(candidates[0].Evidence.Count > 0, "The candidate must carry the matcher's own evidence.");
        Assert.AreEqual(0, await host.CountAnimeMetadataAsync(), "Searching candidates must never apply a match by itself.");

        var applied = await host.Metadata.MatchAsync(
            animeId, candidates[0].Candidate.Provider, candidates[0].Candidate.ExternalId, CancellationToken.None);
        Assert.IsTrue(applied.Success);
        Assert.AreEqual(1, await host.CountAnimeMetadataAsync());
    }

    [TestMethod]
    public async Task ManualMatchIsNeverOverwrittenByAutomaticMatching()
    {
        await using var host = await AnimeRepairTestHost.CreateAsync();
        var animeId = await host.AddBareAnimeAsync("Sousou no Frieren", episodeCount: 28, seasonCount: 1);
        host.Provider.Add(new AnimeMetadataCandidate(
            AniListMetadataProvider.ProviderKey, "1", "Manual pick",
            "Manual pick", null, null, null, null, null, "TV", "FINISHED", "FALL", 2020, 28, 24));
        host.Provider.Add(new AnimeMetadataCandidate(
            AniListMetadataProvider.ProviderKey, "154587", "Sousou no Frieren",
            "Sousou no Frieren", "Frieren: Beyond Journey's End", "葬送のフリーレン",
            null, null, null, "TV", "FINISHED", "FALL", 2023, 28, 24));

        var manual = await host.Metadata.MatchAsync(animeId, AniListMetadataProvider.ProviderKey, "1", CancellationToken.None);
        Assert.IsTrue(manual.Success);

        // Even though "154587" would score far higher against the anime's own title, AutoMatch
        // must refuse to touch an anime that already carries an explicit match.
        var decision = await host.Metadata.AutoMatchAsync(animeId, CancellationToken.None);

        Assert.AreEqual(AutomaticMediaMatchDisposition.None, decision.Disposition);
        var current = await host.Metadata.GetAsync(animeId, CancellationToken.None);
        Assert.AreEqual("1", current!.ExternalId);
    }

    [TestMethod]
    public void TheRepairPageIsForMediaAdmins()
    {
        var attribute = typeof(AnimeRepairModel).GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
            .Cast<AuthorizeAttribute>()
            .SingleOrDefault();

        Assert.IsNotNull(attribute, "The repair page must declare an authorization requirement.");
        Assert.AreEqual(JularrPolicies.AdminMedia, attribute!.Policy);
    }

    // A real SQLite database plus the scan and repair services wired like Program.cs, without the
    // web host. Mirrors LibraryScanTestHost and adds the metadata/repair services it does not need.
    private sealed class AnimeRepairTestHost : IAsyncDisposable
    {
        private readonly SubtitleTrackLookupDelayInterceptor lookupDelay;

        private AnimeRepairTestHost(
            string tempRoot,
            ServiceProvider services,
            FakeMediaProbeRunner probe,
            FakeAniListProvider provider,
            SubtitleTrackLookupDelayInterceptor lookupDelay)
        {
            TempRoot = tempRoot;
            Services = services;
            Probe = probe;
            Provider = provider;
            this.lookupDelay = lookupDelay;
            LibraryPath = Path.Combine(tempRoot, "anime");
        }

        // Holds every SubtitleTracks-by-Path lookup open for this long after it ran, widening the
        // window between "no track for this Path" and the insert that follows it (#601).
        public TimeSpan SubtitleLookupDelay
        {
            get => lookupDelay.Delay;
            set => lookupDelay.Delay = value;
        }

        public string TempRoot { get; }
        public string LibraryPath { get; }
        public ServiceProvider Services { get; }
        public FakeMediaProbeRunner Probe { get; }
        public FakeAniListProvider Provider { get; }
        public BackgroundJobWorker? Worker { get; private set; }

        public IServiceScopeFactory ScopeFactory => Services.GetRequiredService<IServiceScopeFactory>();
        public BackgroundJobQueue Queue => Services.GetRequiredService<BackgroundJobQueue>();
        public LibraryScanCoordinator Scans => Services.GetRequiredService<LibraryScanCoordinator>();
        public AnimeRepairService Repair => Services.GetRequiredService<AnimeRepairService>();
        public AnimeMetadataService Metadata => Services.GetRequiredService<AnimeMetadataService>();

        public static async Task<AnimeRepairTestHost> CreateAsync()
        {
            var tempRoot = Path.Combine(Path.GetTempPath(), $"jularr-repair-{Guid.NewGuid():N}");
            var dictionaryPath = Path.Combine(tempRoot, "dictionary");
            var keysPath = Path.Combine(tempRoot, "keys");
            var integrationPath = Path.Combine(tempRoot, "integrations");
            var databasePath = Path.Combine(tempRoot, "jularr.db");

            Directory.CreateDirectory(dictionaryPath);
            Directory.CreateDirectory(keysPath);
            Directory.CreateDirectory(integrationPath);
            Directory.CreateDirectory(Path.Combine(tempRoot, "anime"));

            await File.WriteAllTextAsync(Path.Combine(dictionaryPath, "jmdict-ger.tsv"), "");
            await File.WriteAllTextAsync(Path.Combine(dictionaryPath, "jmdict-eng-common.tsv"), "");

            var probe = new FakeMediaProbeRunner();
            var provider = new FakeAniListProvider();
            var dataProtection = DataProtectionProvider.Create(new DirectoryInfo(keysPath));

            var lookupDelay = new SubtitleTrackLookupDelayInterceptor();
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddDbContext<AppDbContext>(options =>
                options
                    .UseSqlite($"Data Source={databasePath};Foreign Keys=True")
                    .AddInterceptors(lookupDelay));
            services.AddSingleton<IJapaneseMorphology, EmptyMorphology>();
            services.AddSingleton<JapaneseTermExtractor>();
            services.AddSingleton(new JapaneseDictionary(dictionaryPath));
            services.AddScoped<VocabularyService>();
            services.AddScoped<SubtitleImportService>();
            services.AddSingleton<MediaProcessRunner>();
            services.AddSingleton<EmbeddedSubtitleExtractor>();
            services.AddSingleton(new SonarrConnectionStore(dataProtection));
            services.AddSingleton<IHttpClientFactory, TestHttpClientFactory>();
            services.AddScoped<SonarrArtworkImportService>();
            services.AddScoped<SonarrArtworkSyncService>();
            services.AddSingleton<IMediaProbeRunner>(probe);
            services.AddSingleton<MediaInventoryService>();
            services.AddScoped<LibraryScanner>();
            services.AddSingleton<StorageAvailabilityCoordinator>();
            services.AddScoped<LibraryRootAvailabilityService>();
            services.AddSingleton<BackgroundJobQueue>();
            services.AddSingleton<LibraryScanCoordinator>();

            services.AddSingleton<IAnimeMetadataProvider>(provider);
            services.AddSingleton(new AniListAccountStore(
                dataProtection,
                NullLogger<AniListAccountStore>.Instance,
                new DirectoryInfo(integrationPath)));
            services.AddSingleton(new MediaMappingReviewStore(
                NullLogger<MediaMappingReviewStore>.Instance,
                new DirectoryInfo(integrationPath)));
            services.AddScoped<AnimeMetadataService>();
            services.AddScoped<AnimeRepairService>();

            var built = services.BuildServiceProvider();
            await using (var scope = built.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                await DatabaseMigrationBridge.UpgradeAsync(db);
            }

            return new AnimeRepairTestHost(tempRoot, built, probe, provider, lookupDelay);
        }

        public async Task<LibraryRoot> AddRootAsync(string name)
        {
            await using var scope = Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var root = new LibraryRoot { Name = name, Path = LibraryPath };
            db.LibraryRoots.Add(root);
            db.LibraryRootContentAssignments.Add(new LibraryRootContentAssignment { LibraryRootId = root.Id, ContentType = LibraryContentType.Anime });
            await db.SaveChangesAsync();
            return root;
        }

        public async Task<Guid> AddBareAnimeAsync(string title, int episodeCount = 0, int seasonCount = 0)
        {
            await using var scope = Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var anime = new Anime { Key = title.ToLowerInvariant(), Title = title };
            db.Anime.Add(anime);
            for (var i = 1; i <= episodeCount; i++)
            {
                db.Episodes.Add(new Episode
                {
                    AnimeId = anime.Id,
                    SeasonNumber = seasonCount > 0 ? 1 : 1,
                    Number = i,
                    Title = $"Episode {i}"
                });
            }

            await db.SaveChangesAsync();
            return anime.Id;
        }

        public async Task StartWorkerAsync()
        {
            Worker = new BackgroundJobWorker(Queue, ScopeFactory, NullLogger<BackgroundJobWorker>.Instance);
            await Worker.StartAsync(CancellationToken.None);
        }

        public async Task<OperationSnapshot?> GetOperationAsync(Guid id)
        {
            await using var scope = Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            return await new OperationStore(db).GetAsync(id);
        }

        // A full scan's own discovery queues a "learning-text-batch" job on the same worker. Tests
        // that hold the worker with the probe gate must wait for that unrelated job to settle
        // first, or it would occupy the single test worker instead of the scan under test.
        public async Task WaitForNoActiveOperationsOfKindAsync(string kind, TimeSpan? timeout = null)
        {
            var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(20));
            while (DateTime.UtcNow < deadline)
            {
                await using var scope = Services.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var active = await new OperationStore(db).ListAsync(
                    new OperationListFilter(View: "active", Kind: kind));
                if (active.Count == 0)
                {
                    return;
                }

                await Task.Delay(50);
            }

            Assert.Fail($"Operations of kind '{kind}' were still active after the timeout.");
        }

        public async Task<OperationSnapshot> WaitForAsync(
            Guid operationId,
            Func<OperationSnapshot, bool> predicate,
            TimeSpan? timeout = null)
        {
            var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(20));
            OperationSnapshot? last = null;
            while (DateTime.UtcNow < deadline)
            {
                last = await GetOperationAsync(operationId);
                if (last is not null && predicate(last))
                {
                    return last;
                }

                await Task.Delay(50);
            }

            Assert.Fail($"Operation {operationId} did not reach the expected state; last: {last?.Status} {last?.Message} {last?.Error}");
            return last!;
        }

        public string WriteMedia(string relativePath)
        {
            var fullPath = Path.Combine(LibraryPath, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllBytes(fullPath, [0x00]);
            return Path.GetFullPath(fullPath);
        }

        public async Task<Guid> GetAnimeIdAsync(string title)
        {
            await using var scope = Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            return (await db.Anime.AsNoTracking().SingleAsync(x => x.Title == title)).Id;
        }

        public async Task<string> GetAnimeTitleAsync(Guid animeId)
        {
            await using var scope = Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            return (await db.Anime.AsNoTracking().SingleAsync(x => x.Id == animeId)).Title;
        }

        public async Task<int> CountMediaFilesAsync()
        {
            await using var scope = Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            return await db.MediaFiles.CountAsync();
        }

        public async Task<int> CountAnimeMetadataAsync()
        {
            await using var scope = Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            return await db.AnimeMetadata.CountAsync();
        }

        public async Task<int> CountSubtitleTracksAsync(Guid animeId)
        {
            await using var scope = Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            return await db.SubtitleTracks
                .Join(db.Episodes, track => track.EpisodeId, episode => episode.Id, (track, episode) => new { track, episode })
                .CountAsync(x => x.episode.AnimeId == animeId);
        }

        public string WriteSidecar(string mediaPath, params string[] cueTexts) =>
            WriteSidecar(mediaPath, null, cueTexts);

        // Writes <media>.ja.srt with one cue per text. A fixed stamp makes "the sidecar changed"
        // independent of the filesystem's timestamp resolution.
        public string WriteSidecar(string mediaPath, DateTime? lastWriteUtc, params string[] cueTexts)
        {
            var sidecarPath = Path.ChangeExtension(mediaPath, null) + ".ja.srt";
            var content = string.Join(
                "\n",
                cueTexts.Select((text, index) =>
                    $"{index + 1}\n00:00:{index * 2 + 1:00},000 --> 00:00:{index * 2 + 2:00},000\n{text}\n"));
            File.WriteAllText(sidecarPath, content);
            if (lastWriteUtc is { } stamp)
            {
                File.SetLastWriteTimeUtc(sidecarPath, stamp);
            }

            return sidecarPath;
        }

        // One repair action in its own DI scope (its own DbContext), like each request or job gets.
        // The shared Repair instance keeps one DbContext for the host's whole life, so its tracked
        // entities would go stale between actions and hide what a fresh action reads back.
        public async Task<AnimeRepairLocalRefreshResult> RefreshLocalInNewScopeAsync(Guid animeId)
        {
            await using var scope = Services.CreateAsyncScope();
            return await scope.ServiceProvider
                .GetRequiredService<AnimeRepairService>()
                .RefreshLocalAsync(animeId, CancellationToken.None);
        }

        // One scope per racer, all released at the same instant, like the repair action, a scan and
        // the learning-text batch would be.
        public async Task<AnimeRepairLocalRefreshResult[]> RefreshLocalConcurrentlyAsync(Guid animeId, int racers)
        {
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var tasks = Enumerable.Range(0, racers)
                .Select(_ => Task.Run(async () =>
                {
                    await start.Task;
                    return await RefreshLocalInNewScopeAsync(animeId);
                }))
                .ToArray();

            start.SetResult();
            return await Task.WhenAll(tasks);
        }

        public async Task<Guid> GetEpisodeIdAsync(Guid animeId, int number)
        {
            await using var scope = Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            return (await db.Episodes.AsNoTracking().SingleAsync(x => x.AnimeId == animeId && x.Number == number)).Id;
        }

        public async Task MoveMediaFileToEpisodeAsync(string mediaPath, Guid episodeId)
        {
            await using var scope = Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var moved = await db.MediaFiles
                .Where(x => x.Path == mediaPath)
                .ExecuteUpdateAsync(x => x.SetProperty(media => media.EpisodeId, episodeId));
            Assert.AreEqual(1, moved);
        }

        public async Task<IReadOnlyList<SubtitleTrackRow>> GetSubtitleTracksAsync()
        {
            await using var scope = Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            return await db.SubtitleTracks
                .AsNoTracking()
                .OrderBy(x => x.Path)
                .Select(x => new SubtitleTrackRow(
                    x.Id,
                    x.EpisodeId,
                    x.Path,
                    x.ImportedAt,
                    db.SubtitleCues.Count(cue => cue.SubtitleTrackId == x.Id)))
                .ToListAsync();
        }

        public async Task<(DateTime Frieren, DateTime Bocchi)> GetAnalyzedAtAsync(string frierenPath, string bocchiPath)
        {
            await using var scope = Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var frierenMediaId = await db.MediaFiles.AsNoTracking().Where(x => x.Path == frierenPath).Select(x => x.Id).SingleAsync();
            var bocchiMediaId = await db.MediaFiles.AsNoTracking().Where(x => x.Path == bocchiPath).Select(x => x.Id).SingleAsync();
            var frieren = await db.MediaAnalyses.AsNoTracking().SingleAsync(x => x.MediaFileId == frierenMediaId);
            var bocchi = await db.MediaAnalyses.AsNoTracking().SingleAsync(x => x.MediaFileId == bocchiMediaId);
            return (frieren.AnalyzedAt, bocchi.AnalyzedAt);
        }

        public async ValueTask DisposeAsync()
        {
            if (Worker is not null)
            {
                await Worker.StopAsync(CancellationToken.None);
                Worker.Dispose();
            }

            await Services.DisposeAsync();

            try
            {
                if (Directory.Exists(TempRoot))
                {
                    Directory.Delete(TempRoot, recursive: true);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        public sealed record SubtitleTrackRow(Guid Id, Guid EpisodeId, string Path, DateTime ImportedAt, int CueCount);

        private sealed class SubtitleTrackLookupDelayInterceptor : DbCommandInterceptor
        {
            public TimeSpan Delay { get; set; }

            public override async ValueTask<DbDataReader> ReaderExecutedAsync(
                DbCommand command,
                CommandExecutedEventData eventData,
                DbDataReader result,
                CancellationToken cancellationToken = default)
            {
                if (Delay > TimeSpan.Zero && IsSubtitleTrackPathLookup(command.CommandText))
                {
                    await Task.Delay(Delay, cancellationToken);
                }

                return result;
            }

            private static bool IsSubtitleTrackPathLookup(string sql) =>
                sql.Contains("FROM \"SubtitleTracks\"", StringComparison.Ordinal) &&
                sql.Contains(".\"Path\" =", StringComparison.Ordinal);
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

    private sealed class FakeAniListProvider : IAnimeMetadataProvider
    {
        private readonly List<AnimeMetadataCandidate> catalog = [];

        public string Key => AniListMetadataProvider.ProviderKey;

        public void Add(AnimeMetadataCandidate candidate) => catalog.Add(candidate);

        public Task<IReadOnlyList<AnimeMetadataCandidate>> SearchAsync(
            string query,
            int limit,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<AnimeMetadataCandidate>>(catalog.Take(limit).ToArray());

        public Task<AnimeMetadataCandidate?> GetAsync(
            string externalId,
            CancellationToken cancellationToken) =>
            Task.FromResult(catalog.FirstOrDefault(x => x.ExternalId == externalId));
    }
}
