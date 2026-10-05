using System.Text.Json;
using Jularr.Web.Data;
using Jularr.Web.Features.ClientApi;
using Jularr.Web.Features.Learning;
using Jularr.Web.Features.Library;
using Jularr.Web.Features.MediaSegments;
using Jularr.Web.Features.Operations;
using Jularr.Web.Features.Playback;
using Jularr.Web.Features.Progress;
using Jularr.Web.Features.Storage;
using Jularr.Web.Features.Subtitles;
using Jularr.Web.Features.Vocabulary;
using Jularr.Web.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Jularr.Tests;

[TestClass]
public sealed class MediaSegmentTests
{

    [TestMethod]
    public void MostAuthoritativeSourceWinsPerKindRegardlessOfConfidence()
    {
        var episodeId = Guid.NewGuid();
        var rows = new[]
        {
            Row(episodeId, MediaSegmentKind.Intro, MediaSegmentSource.Detector, 0, 90_000, 0.99),
            Row(episodeId, MediaSegmentKind.Intro, MediaSegmentSource.Imported, 1_000, 91_000, 0.5),
            Row(episodeId, MediaSegmentKind.Outro, MediaSegmentSource.Detector, 1_300_000, 1_390_000, 0.9),
            Row(episodeId, MediaSegmentKind.Outro, MediaSegmentSource.Manual, 1_310_000, 1_400_000, 1),
            Row(episodeId, MediaSegmentKind.Recap, MediaSegmentSource.Provider, 95_000, 95_500, 1)
        };

        var resolved = MediaSegmentPolicy.Resolve(rows, 0.8);

        Assert.AreEqual(2, resolved.Count, "A range shorter than one second is never a marker.");
        var intro = resolved.Single(x => x.Kind == MediaSegmentKind.Intro);
        Assert.AreEqual(MediaSegmentSource.Imported, intro.Source);
        Assert.AreEqual(91_000, intro.EndMs);
        Assert.IsFalse(intro.CanSkip, "The imported marker wins but stays below the skip threshold.");
        var outro = resolved.Single(x => x.Kind == MediaSegmentKind.Outro);
        Assert.AreEqual(MediaSegmentSource.Manual, outro.Source);
        Assert.AreEqual(1_400_000, outro.EndMs);
        Assert.IsTrue(outro.CanSkip);
        Assert.AreEqual(MediaSegmentKind.Intro, resolved[0].Kind, "Markers are ordered by start time.");
    }

    [TestMethod]
    public void SkipIsOfferedOnlyAtOrAboveTheConfiguredConfidence()
    {
        var episodeId = Guid.NewGuid();
        var rows = new[]
        {
            Row(episodeId, MediaSegmentKind.Intro, MediaSegmentSource.Detector, 0, 90_000, 0.8),
            Row(episodeId, MediaSegmentKind.Outro, MediaSegmentSource.Detector, 1_300_000, 1_390_000, 0.79)
        };

        var resolved = MediaSegmentPolicy.Resolve(rows, 0.8);

        Assert.IsTrue(resolved.Single(x => x.Kind == MediaSegmentKind.Intro).CanSkip);
        Assert.IsFalse(resolved.Single(x => x.Kind == MediaSegmentKind.Outro).CanSkip);
        Assert.IsFalse(
            MediaSegmentPolicy.Resolve(rows, 0.95).Any(x => x.CanSkip),
            "A stricter threshold removes every uncertain skip action.");
    }

    [TestMethod]
    public async Task ManualCorrectionOverridesAndRemovalFallsBack()
    {
        await using var fixture = await MediaInventoryFixture.CreateAsync();
        var media = await fixture.AddMediaAsync("episode.mkv", [1, 2, 3]);
        fixture.Db.EpisodeMediaSegments.Add(
            Row(media.EpisodeId!.Value, MediaSegmentKind.Intro, MediaSegmentSource.Imported, 60_000, 150_000, 1));
        await fixture.Db.SaveChangesAsync();
        var (service, _, _) = CreateServices(fixture);

        await service.SaveManualAsync(media.EpisodeId!.Value, MediaSegmentKind.Intro, 61_000, 149_000, CancellationToken.None);
        var corrected = await service.GetSegmentsAsync(media.EpisodeId!.Value, CancellationToken.None);
        Assert.AreEqual(MediaSegmentSource.Manual, corrected.Segments.Single().Source);
        Assert.AreEqual(149_000, corrected.Segments.Single().EndMs);

        await service.SaveManualAsync(media.EpisodeId!.Value, MediaSegmentKind.Intro, 62_000, 148_000, CancellationToken.None);
        Assert.AreEqual(
            1,
            await fixture.Db.EpisodeMediaSegments.CountAsync(x => x.Source == MediaSegmentSource.Manual),
            "Saving again updates the one manual marker.");

        Assert.IsTrue(await service.RemoveManualAsync(media.EpisodeId!.Value, MediaSegmentKind.Intro, CancellationToken.None));
        var fallback = await service.GetSegmentsAsync(media.EpisodeId!.Value, CancellationToken.None);
        Assert.AreEqual(MediaSegmentSource.Imported, fallback.Segments.Single().Source);

        await Assert.ThrowsExactlyAsync<ArgumentException>(() =>
            service.SaveManualAsync(media.EpisodeId!.Value, MediaSegmentKind.Recap, 5_000, 5_500, CancellationToken.None));
    }

    [TestMethod]
    public void SidecarParserAcceptsTimecodesAndAliasesAndSkipsInvalidEntries()
    {
        var result = MediaSegmentSidecar.Parse("""
        {
          "version": 1,
          "segments": [
            { "kind": "OP", "start": "1:02", "end": "2:31.5" },
            { "kind": "ed", "startMs": 1330000, "endMs": 1420000, "confidence": 1.7 },
            { "kind": "eyecatch", "startMs": 0, "endMs": 5000 },
            { "kind": "recap", "startMs": 9000, "endMs": 9500 },
            { "kind": "preview", "start": "-1", "end": "0:10" }
          ],
          "episodes": [ { "episode": 4, "segments": [ { "kind": "intro", "start": 30, "end": 120 } ] } ]
        }
        """);

        Assert.IsTrue(result.IsValid);
        Assert.AreEqual(2, result.Segments.Count);
        Assert.AreEqual(new SidecarSegment(MediaSegmentKind.Intro, 62_000, 151_500, 1), result.Segments[0]);
        Assert.AreEqual(1.0, result.Segments[1].Confidence, "Confidence is clamped to 1.");
        Assert.AreEqual(3, result.Warnings.Count);
        var episode = result.Episodes.Single();
        Assert.AreEqual(1, episode.SeasonNumber, "Season defaults to 1.");
        Assert.AreEqual(120_000, episode.Segments.Single().EndMs);

        Assert.IsFalse(MediaSegmentSidecar.Parse("""{ "version": 2, "segments": [] }""").IsValid);
        Assert.IsFalse(MediaSegmentSidecar.Parse("[1,2]").IsValid);
        Assert.IsFalse(MediaSegmentSidecar.Parse("{ nope").IsValid);
    }

    [TestMethod]
    public async Task SidecarImportIsIdempotentAndOwnsOnlyImportedMarkers()
    {
        await using var fixture = await MediaInventoryFixture.CreateAsync();
        var first = await fixture.AddMediaAsync("Show - S01E01.mkv", [1, 2, 3]);
        var second = await fixture.AddMediaAsync("Show - S01E02.mkv", [4, 5, 6]);
        var episodeSidecar = Path.Combine(fixture.Root.Path, "Show - S01E01.segments.json");
        var folderSidecar = Path.Combine(fixture.Root.Path, "segments.json");
        await File.WriteAllTextAsync(episodeSidecar, """
        { "version": 1, "segments": [
          { "kind": "intro", "startMs": 60000, "endMs": 150000 },
          { "kind": "outro", "startMs": 1300000, "endMs": 1390000, "confidence": 0.9 } ] }
        """);
        await File.WriteAllTextAsync(folderSidecar, """
        { "version": 1, "episodes": [
          { "season": 1, "episode": 1, "segments": [ { "kind": "recap", "startMs": 0, "endMs": 30000 } ] },
          { "season": 1, "episode": 2, "segments": [ { "kind": "intro", "startMs": 5000, "endMs": 95000 } ] } ] }
        """);
        var (service, _, _) = CreateServices(fixture);
        await service.SaveManualAsync(first.EpisodeId!.Value, MediaSegmentKind.Preview, 1_400_000, 1_420_000, CancellationToken.None);
        var importer = new MediaSegmentSidecarImporter(fixture.Db, NullLogger<MediaSegmentSidecarImporter>.Instance);

        var initial = await importer.ReconcileRootAsync(fixture.Root.Id, CancellationToken.None);
        Assert.AreEqual(new SidecarImportResult(3, 0, 0, 0), initial, "The episode sidecar wins completely over the folder file.");

        var repeated = await importer.ReconcileRootAsync(fixture.Root.Id, CancellationToken.None);
        Assert.AreEqual(new SidecarImportResult(0, 0, 0, 0), repeated);

        await File.WriteAllTextAsync(episodeSidecar, """
        { "version": 1, "segments": [ { "kind": "intro", "startMs": 61000, "endMs": 150000 } ] }
        """);
        var changed = await importer.ReconcileRootAsync(fixture.Root.Id, CancellationToken.None);
        Assert.AreEqual(new SidecarImportResult(0, 1, 1, 0), changed);

        await File.WriteAllTextAsync(episodeSidecar, "{ broken");
        var broken = await importer.ReconcileRootAsync(fixture.Root.Id, CancellationToken.None);
        Assert.AreEqual(new SidecarImportResult(0, 0, 0, 1), broken, "An unreadable sidecar keeps the last import.");

        File.Delete(episodeSidecar);
        File.Delete(folderSidecar);
        var removed = await importer.ReconcileRootAsync(fixture.Root.Id, CancellationToken.None);
        Assert.AreEqual(new SidecarImportResult(0, 0, 2, 0), removed);

        var remaining = await fixture.Db.EpisodeMediaSegments.AsNoTracking().ToListAsync();
        Assert.AreEqual(1, remaining.Count, "Manual markers are never touched by the importer.");
        Assert.AreEqual(MediaSegmentSource.Manual, remaining[0].Source);
        Assert.AreEqual(first.EpisodeId!.Value, remaining[0].EpisodeId);
        Assert.AreNotEqual(first.EpisodeId!.Value, second.EpisodeId!.Value);
    }

    [TestMethod]
    public async Task DetectorResultsAreVersionedRebuildableAndNeverOutrankImports()
    {
        await using var fixture = await MediaInventoryFixture.CreateAsync();
        var media = await fixture.AddMediaAsync("episode.mkv", [1, 2, 3]);
        fixture.Runner.Returns(media.Path, MediaProbeFixtures.H264Stereo);
        var detector = new FakeDetector();
        var (service, _, _) = CreateServices(fixture, detector: detector);

        Assert.AreEqual(
            SegmentDetectionOutcome.NotAnalyzed,
            (await service.RunDetectorAsync(media.EpisodeId!.Value, force: false, CancellationToken.None)).Outcome);

        await fixture.Inventory.ReconcileAsync(fixture.Root.Id, CancellationToken.None);
        var run = await service.RunDetectorAsync(media.EpisodeId!.Value, force: false, CancellationToken.None);
        Assert.AreEqual(new SegmentDetectionRun(SegmentDetectionOutcome.Completed, 1), run);
        Assert.AreEqual(1440.0, detector.LastRequest?.DurationSeconds);

        var again = await service.RunDetectorAsync(media.EpisodeId!.Value, force: false, CancellationToken.None);
        Assert.AreEqual(SegmentDetectionOutcome.Skipped, again.Outcome);
        Assert.AreEqual(1, detector.Calls, "Unchanged identity and detector version skip re-analysis.");

        detector.Version = "2";
        await service.RunDetectorAsync(media.EpisodeId!.Value, force: false, CancellationToken.None);
        Assert.AreEqual(2, detector.Calls, "A new detector version rebuilds its markers.");
        var stored = await fixture.Db.EpisodeMediaSegments.AsNoTracking().SingleAsync();
        Assert.AreEqual("fake", stored.Method);
        Assert.AreEqual("2", stored.Version);
        Assert.AreEqual(0.6, stored.Confidence, 0.0001);
        Assert.IsNotNull(stored.MediaIdentity);

        fixture.Db.EpisodeMediaSegments.Add(
            Row(media.EpisodeId!.Value, MediaSegmentKind.Intro, MediaSegmentSource.Imported, 10_000, 20_000, 0.9));
        await fixture.Db.SaveChangesAsync();
        var resolved = await service.GetSegmentsAsync(media.EpisodeId!.Value, CancellationToken.None);
        Assert.AreEqual(MediaSegmentSource.Imported, resolved.Segments.Single().Source);

        var (disabled, _, _) = CreateServices(fixture);
        Assert.AreEqual(
            SegmentDetectionOutcome.DetectorDisabled,
            (await disabled.RunDetectorAsync(media.EpisodeId!.Value, force: true, CancellationToken.None)).Outcome);
    }

    [TestMethod]
    public async Task TrickplayCacheIsKeyedByInventoryIdentityAndGeneratorVersion()
    {
        await using var fixture = await MediaInventoryFixture.CreateAsync();
        var media = await fixture.AddMediaAsync("episode.mkv", [1, 2, 3, 4]);
        fixture.Runner.DefaultResult = new MediaProbeRun(MediaProbeRunStatus.Completed, MediaProbeFixtures.H264Stereo);
        var (service, trickplay, _) = CreateServices(fixture);
        var playback = new PlaybackService(
            fixture.Db,
            new PlaybackCueProjector(new EmptyMorphology()),
            fixture.Inventory,
            service);

        // Without an inventory analysis there is no identity and nothing is queued.
        var beforeAnalysis = await service.GetTrickplayAsync(media.EpisodeId!.Value, CancellationToken.None);
        Assert.AreEqual(TrickplayState.Unavailable, beforeAnalysis.State);

        await fixture.Inventory.ReconcileAsync(fixture.Root.Id, CancellationToken.None);
        var identity = await CurrentIdentityAsync(fixture, media.Id);
        StringAssert.EndsWith(TrickplayGenerator.CacheKey(media.Id, identity), $"-v{TrickplayGenerator.GeneratorVersion}");
        SeedCache(trickplay, media.Id, identity, TrickplayGenerator.GeneratorVersion);

        var snapshot = await playback.GetSnapshotAsync(media.EpisodeId!.Value, CancellationToken.None);
        Assert.AreEqual(TrickplayState.Ready, snapshot.Navigation?.Trickplay.State);
        var readyClient = ClientApiMappings.ToClientTrickplay(media.EpisodeId!.Value, snapshot.Navigation!.Trickplay);
        Assert.AreEqual("ready", readyClient.State);
        Assert.AreEqual(
            $"{ClientApiRoutes.TrickplayAsset(media.EpisodeId!.Value, "sprite-001.jpg")}?v={identity[..16]}-{TrickplayGenerator.GeneratorVersion}",
            readyClient.SpriteUrls.Single(),
            "Sprite URLs change with the media identity so cached images are never reused.");
        Assert.AreEqual(0, await CountTrickplayOperationsAsync(fixture), "An existing cache is never regenerated.");

        // A touched file with identical content keeps its identity and cache.
        File.SetLastWriteTimeUtc(media.Path, DateTime.UtcNow.AddMinutes(5));
        await fixture.RecordObservedIdentityAsync(media);
        await fixture.Inventory.ReconcileAsync(fixture.Root.Id, CancellationToken.None);
        Assert.AreEqual(identity, await CurrentIdentityAsync(fixture, media.Id));
        await playback.GetSnapshotAsync(media.EpisodeId!.Value, CancellationToken.None);
        Assert.AreEqual(0, await CountTrickplayOperationsAsync(fixture));

        // Changed content yields a new identity and queues exactly one generation.
        await File.WriteAllBytesAsync(media.Path, [9, 9, 9, 9, 9, 9]);
        await fixture.RecordObservedIdentityAsync(media);
        await fixture.Inventory.ReconcileAsync(fixture.Root.Id, CancellationToken.None);
        var changedIdentity = await CurrentIdentityAsync(fixture, media.Id);
        Assert.AreNotEqual(identity, changedIdentity);

        var changed = await playback.GetSnapshotAsync(media.EpisodeId!.Value, CancellationToken.None);
        await playback.GetSnapshotAsync(media.EpisodeId!.Value, CancellationToken.None);
        Assert.AreEqual(TrickplayState.Queued, changed.Navigation?.Trickplay.State);
        Assert.AreEqual(1, await CountTrickplayOperationsAsync(fixture));

        // An index written by another generator version is not served.
        var (otherService, otherTrickplay, _) = CreateServices(fixture, cacheRoot: Path.Combine(fixture.TempRoot, "other-cache"));
        SeedCache(otherTrickplay, media.Id, changedIdentity, TrickplayGenerator.GeneratorVersion + 1);
        Assert.AreEqual(
            TrickplayState.Unavailable,
            (await otherService.GetTrickplayAsync(media.EpisodeId!.Value, CancellationToken.None)).State);
    }

    [TestMethod]
    public async Task TrickplayDegradesCleanlyWhenFfmpegOrMediaIsUnavailable()
    {
        await using var fixture = await MediaInventoryFixture.CreateAsync();
        var media = await fixture.AddMediaAsync("episode.mkv", [1, 2, 3]);
        fixture.Runner.Returns(media.Path, MediaProbeFixtures.H264Stereo);
        await fixture.Inventory.ReconcileAsync(fixture.Root.Id, CancellationToken.None);
        var (service, trickplay, provider) = CreateServices(fixture);
        var identity = await CurrentIdentityAsync(fixture, media.Id);
        var request = new TrickplayRequest(media.EpisodeId!.Value, media.Id, identity, media.Path, 1440, "episode.mkv");
        var operationId = await new OperationStore(fixture.Db).CreateAsync(
            new OperationDescriptor(TrickplayGenerator.OperationKind, "Playback", "Generate seek preview thumbnails"));

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            trickplay.GenerateAsync(request, new OperationExecutionContext(operationId, provider), CancellationToken.None));

        var failed = await service.GetTrickplayAsync(media.EpisodeId!.Value, CancellationToken.None);
        Assert.AreEqual(TrickplayState.Failed, failed.State);
        StringAssert.Contains(failed.Message ?? "", "ffmpeg");
        var client = ClientApiMappings.ToClientTrickplay(media.EpisodeId!.Value, failed);
        Assert.AreEqual("unavailable", client.State);
        Assert.AreEqual(0, client.SpriteUrls.Count);
        Assert.IsNull(await service.GetTrickplayAssetAsync(media.EpisodeId!.Value, "sprite-001.jpg", CancellationToken.None));
        Assert.IsNull(await service.GetTrickplayAssetAsync(media.EpisodeId!.Value, "../jularr.db", CancellationToken.None));
        Assert.IsFalse(Directory.Exists(Path.Combine(trickplay.RootPath, ".tmp")) &&
                       Directory.EnumerateFileSystemEntries(Path.Combine(trickplay.RootPath, ".tmp")).Any());

        File.Delete(media.Path);
        var missing = new TrickplayRequest(media.EpisodeId!.Value, media.Id, "other", media.Path, 1440, "episode.mkv");
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            trickplay.GenerateAsync(missing, new OperationExecutionContext(operationId, provider), CancellationToken.None));
        Assert.AreEqual(TrickplayState.Failed, trickplay.Describe(media.Id, "other").State);
    }

    [TestMethod]
    public async Task PlayerBootstrapAndWebSnapshotExposeTheSameDescriptor()
    {
        await using var fixture = await MediaInventoryFixture.CreateAsync();
        var media = await fixture.AddMediaAsync("episode.mkv", [1, 2, 3]);
        fixture.Runner.Returns(media.Path, MediaProbeFixtures.H264Stereo);
        await fixture.Inventory.ReconcileAsync(fixture.Root.Id, CancellationToken.None);
        fixture.Db.EpisodeMediaSegments.AddRange(
            Row(media.EpisodeId!.Value, MediaSegmentKind.Intro, MediaSegmentSource.Imported, 60_000, 150_000, 1),
            Row(media.EpisodeId!.Value, MediaSegmentKind.Outro, MediaSegmentSource.Detector, 1_300_000, 1_390_000, 0.4));
        await fixture.Db.SaveChangesAsync();

        var (segments, _, _) = CreateServices(fixture);
        var account = EpisodeFlowFixture.Account("profile-a");
        var availability = new MediaAvailabilityService(
            fixture.Db,
            new LibraryRootAvailabilityService(fixture.Db, new StorageAvailabilityCoordinator()));
        var subtitleExtractor = new EmbeddedSubtitleExtractor(
            new MediaProcessRunner(NullLogger<MediaProcessRunner>.Instance),
            fixture.Inventory,
            NullLogger<EmbeddedSubtitleExtractor>.Instance);
        var playback = new PlaybackService(
            fixture.Db,
            new PlaybackCueProjector(new EmptyMorphology()),
            fixture.Inventory,
            subtitleExtractor,
            availability,
            account,
            segments);
        var api = new ClientApiService(
            fixture.Db,
            playback,
            new LearningService(fixture.Db, new FsrsReviewScheduler(), account),
            availability,
            EpisodeFlowFixture.ProgressService(fixture.Db, account),
            account,
            segments);

        var bootstrap = await api.GetPlayerAsync(media.EpisodeId!.Value, CancellationToken.None)
            ?? throw new AssertFailedException("Expected a player bootstrap.");
        var descriptor = bootstrap.Segments ?? throw new AssertFailedException("Expected segments.");
        Assert.AreEqual(0.8, descriptor.SkipConfidenceThreshold);
        Assert.AreEqual(2, descriptor.Segments.Count);
        var intro = descriptor.Segments.Single(x => x.Kind == "intro");
        Assert.AreEqual(150_000, intro.EndMs);
        Assert.AreEqual("imported", intro.Source);
        Assert.IsTrue(intro.CanSkip);
        Assert.IsFalse(descriptor.Segments.Single(x => x.Kind == "outro").CanSkip, "Uncertain markers carry no skip action.");
        Assert.AreEqual("generating", bootstrap.Trickplay?.State, "Playback does not wait for preview generation.");
        Assert.AreEqual(ClientApiRoutes.Trickplay(media.EpisodeId!.Value), bootstrap.Trickplay?.DescriptorUrl);

        var standalone = await api.GetSegmentsAsync(media.EpisodeId!.Value, CancellationToken.None);
        Assert.AreEqual(
            JsonSerializer.Serialize(descriptor),
            JsonSerializer.Serialize(standalone));
        Assert.IsNull(await api.GetSegmentsAsync(Guid.NewGuid(), CancellationToken.None));

        var snapshot = await playback.GetSnapshotAsync(media.EpisodeId!.Value, CancellationToken.None);
        var web = ClientApiMappings.ToClientSegments(
            snapshot.Navigation?.Segments ?? throw new AssertFailedException("Expected web navigation."));
        Assert.AreEqual(JsonSerializer.Serialize(descriptor), JsonSerializer.Serialize(web));

        var features = ClientApiContract.Capabilities().Features;
        Assert.IsTrue(features.MediaSegments);
        Assert.IsTrue(features.Trickplay);
    }


    private static EpisodeMediaSegment Row(
        Guid episodeId,
        MediaSegmentKind kind,
        MediaSegmentSource source,
        long startMs,
        long endMs,
        double confidence) =>
        new()
        {
            EpisodeId = episodeId,
            Kind = kind,
            Source = source,
            StartMs = startMs,
            EndMs = endMs,
            Method = source == MediaSegmentSource.Imported ? MediaSegmentPolicy.SidecarMethod : "test",
            Version = source == MediaSegmentSource.Imported ? MediaSegmentPolicy.SidecarVersion : "1",
            Confidence = confidence
        };

    private static (MediaSegmentService Service, TrickplayGenerator Trickplay, IServiceProvider Provider) CreateServices(
        MediaInventoryFixture fixture,
        IMediaSegmentDetector? detector = null,
        string? cacheRoot = null)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => new AppDbContext(fixture.Options));
        var provider = services.BuildServiceProvider();
        var trickplay = new TrickplayGenerator(
            new MediaProcessRunner(NullLogger<MediaProcessRunner>.Instance),
            new BackgroundJobQueue(provider.GetRequiredService<IServiceScopeFactory>()),
            NullLogger<TrickplayGenerator>.Instance,
            cacheRoot ?? Path.Combine(fixture.TempRoot, "trickplay"),
            ffmpegExecutable: "jularr-missing-ffmpeg");
        var service = new MediaSegmentService(
            fixture.Db,
            Options.Create(new MediaSegmentOptions()),
            detector ?? new NoOpMediaSegmentDetector(),
            trickplay);
        return (service, trickplay, provider);
    }

    private static Task<int> CountTrickplayOperationsAsync(MediaInventoryFixture fixture) =>
        fixture.Db.Database
            .SqlQuery<int>($"SELECT COUNT(*) AS \"Value\" FROM \"Operations\" WHERE \"Kind\" = {TrickplayGenerator.OperationKind}")
            .SingleAsync();

    private static async Task<string> CurrentIdentityAsync(MediaInventoryFixture fixture, Guid mediaFileId)
    {
        var analysis = await fixture.Db.MediaAnalyses.AsNoTracking().SingleAsync(x => x.MediaFileId == mediaFileId);
        Assert.AreEqual(MediaAnalysisStatus.Succeeded, analysis.Status);
        return MediaIdentity.Compute(
            mediaFileId,
            analysis.SourceFingerprint,
            analysis.SourceSizeBytes,
            analysis.SourceLastWriteTimeUtc);
    }

    private static void SeedCache(TrickplayGenerator trickplay, Guid mediaFileId, string identity, int generatorVersion)
    {
        var directory = trickplay.CacheDirectory(mediaFileId, identity);
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, "sprite-001.jpg"), [0xFF, 0xD8, 0xFF]);
        var index = new TrickplayIndex(
            TrickplayGenerator.IndexVersion,
            generatorVersion,
            identity,
            10_000,
            TrickplayGenerator.TileWidth,
            TrickplayGenerator.TileHeight,
            TrickplayGenerator.Columns,
            TrickplayGenerator.Rows,
            100,
            ["sprite-001.jpg"]);
        File.WriteAllText(
            Path.Combine(directory, TrickplayGenerator.IndexFileName),
            JsonSerializer.Serialize(index, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }

    private sealed class FakeDetector : IMediaSegmentDetector
    {
        public string Method => "fake";

        public string Version { get; set; } = "1";

        public int Calls { get; private set; }

        public MediaSegmentDetectionRequest? LastRequest { get; private set; }

        public Task<IReadOnlyList<DetectedMediaSegment>> DetectAsync(
            MediaSegmentDetectionRequest request,
            CancellationToken cancellationToken)
        {
            Calls++;
            LastRequest = request;
            return Task.FromResult<IReadOnlyList<DetectedMediaSegment>>(
            [
                new DetectedMediaSegment(MediaSegmentKind.Intro, 60_000, 150_000, 0.6),
                new DetectedMediaSegment(MediaSegmentKind.Intro, 61_000, 150_000, 0.4),
                new DetectedMediaSegment(MediaSegmentKind.Recap, 1_000, 1_200, 0.9)
            ]);
        }
    }

    private sealed class EmptyMorphology : IJapaneseMorphology
    {
        public IReadOnlyList<JapaneseMorphToken> Analyze(string text) => [];
    }
}
