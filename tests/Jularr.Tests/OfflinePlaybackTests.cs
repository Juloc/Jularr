using System.Text.Json;
using Jularr.Web.Features.ClientApi;
using Jularr.Web.Features.Library;
using Jularr.Web.Features.Playback;
using Jularr.Web.Features.Progress;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Tests;

[TestClass]
public sealed class OfflinePlaybackTests
{
    private const long Duration = 1_400_000;

    [TestMethod]
    public void CapabilitiesAdvertiseOfflineDownloads()
    {
        Assert.IsTrue(ClientApiContract.Capabilities().Features.OfflineDownloads);
    }

    [TestMethod]
    public async Task OfflineProgressOnlyMovesForwardAndReplaysAreIdempotent()
    {
        await using var fixture = await EpisodeFlowFixture.CreateAsync();
        var anime = await fixture.AddAnimeAsync("offline-forward");
        var episode = await fixture.AddEpisodeAsync(anime, 1, 1);
        var service = fixture.Service("reader");
        var reconciler = new OfflineProgressReconciler(service);

        await service.UpdateAsync(episode.Id, new EpisodeProgressUpdate(300_000, Duration, false));

        var forward = await reconciler.ReconcileAsync(
            [new OfflineProgressCheckpoint(episode.Id, 600_000, Duration, false)]);
        Assert.AreEqual(OfflineProgressOutcome.Applied, forward.Single().Outcome);
        Assert.AreEqual(600_000, forward.Single().Progress?.ResumePositionMs);
        var historyAfterForward = await CanonicalProgressSeed.CountAsync(fixture.Db, "MediaPlaybackHistory");

        var replay = await reconciler.ReconcileAsync(
            [new OfflineProgressCheckpoint(episode.Id, 600_000, Duration, false)]);
        Assert.AreEqual(OfflineProgressOutcome.Unchanged, replay.Single().Outcome);

        var stale = await reconciler.ReconcileAsync(
            [new OfflineProgressCheckpoint(episode.Id, 120_000, Duration, false)]);
        Assert.AreEqual(OfflineProgressOutcome.IgnoredBehind, stale.Single().Outcome);
        Assert.AreEqual(600_000, stale.Single().Progress?.ResumePositionMs);

        var stored = await service.GetAsync(episode.Id);
        Assert.AreEqual(600_000, stored?.PositionMs, "Offline reconciliation must never move progress backwards.");
        Assert.AreEqual(
            historyAfterForward,
            await CanonicalProgressSeed.CountAsync(fixture.Db, "MediaPlaybackHistory"),
            "Ignored or replayed checkpoints must not create history.");
    }

    [TestMethod]
    public async Task WatchedStateIsStickyAndStaleCompletionKeepsNewerRewatchPosition()
    {
        await using var fixture = await EpisodeFlowFixture.CreateAsync();
        var anime = await fixture.AddAnimeAsync("offline-watched");
        var episode = await fixture.AddEpisodeAsync(anime, 1, 1);
        var service = fixture.Service("reader");
        var reconciler = new OfflineProgressReconciler(service);

        var completed = await reconciler.ReconcileAsync(
            [new OfflineProgressCheckpoint(episode.Id, Duration, Duration, true)]);
        Assert.AreEqual(OfflineProgressOutcome.Completed, completed.Single().Outcome);
        Assert.IsTrue(completed.Single().Progress?.IsCompleted);

        // A later online rewatch stores a resume position on the watched episode.
        await service.UpdateAsync(episode.Id, new EpisodeProgressUpdate(200_000, Duration, false));

        var replayedCompletion = await reconciler.ReconcileAsync(
            [new OfflineProgressCheckpoint(episode.Id, Duration, Duration, true)]);
        Assert.AreEqual(OfflineProgressOutcome.Unchanged, replayedCompletion.Single().Outcome);

        var partial = await reconciler.ReconcileAsync(
            [new OfflineProgressCheckpoint(episode.Id, 900_000, Duration, false)]);
        Assert.AreEqual(OfflineProgressOutcome.IgnoredWatched, partial.Single().Outcome);

        var stored = await service.GetAsync(episode.Id);
        Assert.IsNotNull(stored);
        Assert.IsTrue(stored.IsCompleted, "Offline checkpoints never un-watch an episode.");
        Assert.AreEqual(200_000, stored.ResumePositionMs, "A stale offline checkpoint must not replace the newer rewatch position.");
    }

    [TestMethod]
    public async Task OfflinePositionPastTheThresholdIsOnlyAResumePointUnlessCompletionIsDeclared()
    {
        await using var fixture = await EpisodeFlowFixture.CreateAsync();
        var anime = await fixture.AddAnimeAsync("offline-threshold");
        var episode = await fixture.AddEpisodeAsync(anime, 1, 1);
        var service = fixture.Service("reader");
        var reconciler = new OfflineProgressReconciler(service);

        await service.UpdateAsync(episode.Id, new EpisodeProgressUpdate(100_000, 1_000_000, false));

        var seeked = await reconciler.ReconcileAsync(
            [new OfflineProgressCheckpoint(episode.Id, 960_000, null, false)]);

        Assert.AreEqual(OfflineProgressOutcome.Applied, seeked.Single().Outcome);
        var stored = await service.GetAsync(episode.Id);
        Assert.IsFalse(stored!.IsCompleted, "The server never infers completion from an offline position.");
        Assert.AreEqual(960_000, stored.ResumePositionMs);

        var declared = await reconciler.ReconcileAsync(
            [new OfflineProgressCheckpoint(episode.Id, 970_000, null, true)]);

        Assert.AreEqual(OfflineProgressOutcome.Completed, declared.Single().Outcome);
        Assert.IsTrue((await service.GetAsync(episode.Id))?.IsCompleted);
    }

    [TestMethod]
    public async Task AccidentalStartsAndUnknownEpisodesCreateNoState()
    {
        await using var fixture = await EpisodeFlowFixture.CreateAsync();
        var anime = await fixture.AddAnimeAsync("offline-accidental");
        var episode = await fixture.AddEpisodeAsync(anime, 1, 1);
        var reconciler = new OfflineProgressReconciler(fixture.Service("reader"));
        var unknown = Guid.NewGuid();

        var results = await reconciler.ReconcileAsync(
        [
            new OfflineProgressCheckpoint(episode.Id, EpisodeProgressService.MinimumResumeMs - 1, Duration, false),
            new OfflineProgressCheckpoint(unknown, 600_000, Duration, false)
        ]);

        Assert.AreEqual(OfflineProgressOutcome.IgnoredAccidentalStart, results[0].Outcome);
        Assert.AreEqual(OfflineProgressOutcome.EpisodeNotFound, results[1].Outcome);
        Assert.IsNull(results[1].Progress);
        Assert.AreEqual(0, await CanonicalProgressSeed.CountAsync(fixture.Db, "MediaProgress"));
        Assert.AreEqual(0, await CanonicalProgressSeed.CountAsync(fixture.Db, "MediaPlaybackHistory"));
    }

    [TestMethod]
    public async Task ReconciliationIsProfileScoped()
    {
        await using var fixture = await EpisodeFlowFixture.CreateAsync();
        var anime = await fixture.AddAnimeAsync("offline-profiles");
        var episode = await fixture.AddEpisodeAsync(anime, 1, 1);
        var readerA = fixture.Service("reader-a");
        var readerB = fixture.Service("reader-b");

        await readerB.UpdateAsync(episode.Id, new EpisodeProgressUpdate(900_000, Duration, false));

        var result = await new OfflineProgressReconciler(readerA).ReconcileAsync(
            [new OfflineProgressCheckpoint(episode.Id, 400_000, Duration, false)]);

        Assert.AreEqual(OfflineProgressOutcome.Applied, result.Single().Outcome);
        Assert.AreEqual(400_000, (await readerA.GetAsync(episode.Id))?.PositionMs);
        Assert.AreEqual(900_000, (await readerB.GetAsync(episode.Id))?.PositionMs);
    }

    [TestMethod]
    public void EveryOutcomeHasAStableWireName()
    {
        var names = Enum.GetValues<OfflineProgressOutcome>()
            .Select(ClientApiOfflineContract.OutcomeName)
            .ToArray();

        Assert.AreEqual(names.Length, names.Distinct(StringComparer.Ordinal).Count());
        CollectionAssert.Contains(names, "ignored_behind");
        CollectionAssert.Contains(names, "ignored_watched");
    }

    // The Android client computes the same fingerprint after a download; these vectors are
    // duplicated in clients/android app-mobile OfflineMediaVerifierTest.
    [TestMethod]
    [DataRow(1_000, "9bb6b1984c6fa246dd692298ea3af4ec7d98884b4a7efd50094a63ba81cfd47e")]
    [DataRow(100_000, "3b940a43c416bd37a9ca45d5f93b1c1dc20ce046fcca79a0dbd5389b5d7ed4a7")]
    [DataRow(200_000, "0380b2aa8688f6914fe14c854b25a82175708fd91a37f577b43ab5feb001a321")]
    public async Task FingerprintMatchesTheSharedClientVectors(int length, string expected)
    {
        var path = WritePatternFile(length);
        try
        {
            Assert.AreEqual(
                expected,
                await MediaInventoryService.TryComputeFingerprintAsync(path, CancellationToken.None));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public void EntityTagChangesWithEveryFileVersion()
    {
        var writtenAt = new DateTime(2026, 9, 26, 12, 0, 0, DateTimeKind.Utc);
        var tag = ClientApiOfflineContract.EntityTag(1234, writtenAt);

        Assert.IsTrue(tag.StartsWith('"') && tag.EndsWith('"'), "An HTTP entity tag must be quoted.");
        Assert.AreEqual(tag, ClientApiOfflineContract.EntityTag(1234, writtenAt));
        Assert.AreNotEqual(tag, ClientApiOfflineContract.EntityTag(1235, writtenAt));
        Assert.AreNotEqual(tag, ClientApiOfflineContract.EntityTag(1234, writtenAt.AddSeconds(1)));
    }

    [TestMethod]
    public async Task DescriptorCarriesCanonicalIdentityAndNoHostPaths()
    {
        var path = WritePatternFile(200_000);
        try
        {
            var mediaFileId = Guid.NewGuid();
            var episodeId = Guid.NewGuid();
            var file = new FileInfo(path);
            var fingerprint = await MediaInventoryService.TryComputeFingerprintAsync(path, CancellationToken.None);
            var player = new ClientPlayerBootstrap(
                ClientApiContract.ApiVersion,
                new ClientPlayerEpisode(episodeId, Guid.NewGuid(), "Anime", "Episode 1", 1, 1),
                null,
                [new ClientMediaTrack("stream:1", 1, "audio", "aac", "jpn", "Japanese", true, false, false)],
                [new ClientMediaTrack("stream:2", 2, "subtitle", "ass", "eng", null, false, false, true)],
                [],
                null,
                "stream:1",
                null,
                new ClientCompatibilityFallback(false, null, false, false, null),
                new ClientPlayerDefaults(
                    "stream:1",
                    "off",
                    null,
                    1.0,
                    ClientApiMappings.ToClientPreferences(PlaybackPreferencesSnapshot.Default)),
                new ClientPlayerControls(PlaybackPreferenceRules.Speeds, PlaybackQuality.Names));
            var media = new ClientPlayerMedia(
                mediaFileId,
                "episode.mkv",
                "video/x-matroska",
                1,
                Duration,
                "hevc",
                "yuv420p10le",
                "aac",
                ClientApiRoutes.DirectContent(mediaFileId),
                true,
                new ClientPlaybackOption("ready", "Direct", false),
                new ClientPlaybackOption("ready", "Server", true),
                new ClientMediaAvailability("available", false, 0, false, null, ClientApiRoutes.MediaAvailability(mediaFileId), null));

            var descriptor = ClientApiOfflineService.BuildDescriptor(
                player,
                media,
                file,
                fingerprint!,
                new ClientCueResponse(null, null, null, []),
                new EpisodeProgressSnapshot(episodeId, 600_000, Duration, false, DateTime.UtcNow),
                DateTime.UtcNow);

            Assert.AreEqual(200_000, descriptor.Media.SizeBytes, "The canonical size comes from the file on disk.");
            Assert.AreEqual(ClientApiOfflineContract.EntityTag(file.Length, file.LastWriteTimeUtc), descriptor.Media.ETag);
            Assert.AreEqual(ClientApiOfflineContract.FingerprintAlgorithm, descriptor.Media.FingerprintAlgorithm);
            Assert.AreEqual(ClientApiOfflineRoutes.Content(mediaFileId), descriptor.Media.ContentUrl);
            Assert.AreEqual(600_000, descriptor.Progress.ResumePositionMs);
            Assert.AreEqual("stream:1", descriptor.DefaultAudioTrackId);

            var json = JsonSerializer.Serialize(descriptor, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            StringAssert.Contains(json, "\"eTag\":", "The Android client parses the camelCase wire name.");
            StringAssert.Contains(json, "\"fingerprintAlgorithm\":");
            StringAssert.Contains(json, "\"learningCues\":");
            Assert.IsFalse(json.Contains(Path.GetDirectoryName(path)!.Replace("\\", "\\\\"), StringComparison.OrdinalIgnoreCase));
            Assert.IsFalse(json.Contains(Path.GetFileName(path), StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public void OfflineRoutesStayInsideTheVersionedClientApi()
    {
        var id = Guid.NewGuid();

        foreach (var route in new[]
                 {
                     ClientApiOfflineRoutes.Download(id),
                     ClientApiOfflineRoutes.Content(id),
                     ClientApiOfflineRoutes.Progress
                 })
        {
            Assert.IsTrue(ClientApiRoutes.IsClientApi(new Microsoft.AspNetCore.Http.PathString(route)), route);
        }
    }

    private static string WritePatternFile(int length)
    {
        var data = new byte[length];
        for (var i = 0; i < length; i++)
        {
            data[i] = (byte)((i * 31 + 7) % 256);
        }

        var path = Path.Combine(Path.GetTempPath(), $"jularr-offline-{Guid.NewGuid():N}.bin");
        File.WriteAllBytes(path, data);
        return path;
    }
}
