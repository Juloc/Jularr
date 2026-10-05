using Jularr.Web.Features.Playback;
using Jularr.Web.Features.Playback.Decision;
using Jularr.Web.Features.Playback.Transcoding;
using Jularr.Web.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using static Jularr.Tests.PlaybackTestPlans;

namespace Jularr.Tests;

/// <summary>
/// The server resource policy of playback (#403): the settings store, slots by cost class, admission refusals, the
/// HLS cache budget and free-space floor, the sweeper and the hosted service that runs it. ffmpeg and the clock are
/// fakes; the cache folders are real temporary directories.
/// </summary>
[TestClass]
public sealed class PlaybackServerResourceTests
{
    private static readonly DateTimeOffset s_start = DateTimeOffset.Parse("2026-10-05T10:00:00Z");

    [TestMethod]
    public void DefaultsMatchTheBindingPolicy()
    {
        var defaults = PlaybackTranscodingSettings.Default;

        Assert.IsTrue(defaults.TranscodingEnabled);
        Assert.AreEqual(2, defaults.SoftwareVideoSessions);
        Assert.AreEqual(4, defaults.HardwareVideoSessions);
        Assert.AreEqual(6, defaults.RemuxSessions);
        Assert.AreEqual(8, defaults.AudioOnlySessions);
        Assert.AreEqual("/data/playback-cache/hls", defaults.HlsCachePath);
        Assert.AreEqual(10L * 1024 * 1024 * 1024, defaults.CacheBudgetBytes);
        Assert.AreEqual(5L * 1024 * 1024 * 1024, defaults.FreeSpaceFloorBytes);
        Assert.AreEqual(0, PlaybackTranscodingSettingsRules.Validate(defaults).Count);
    }

    [TestMethod]
    [DataRow("", PlaybackSettingsIssueCode.PathRequired)]
    [DataRow("   ", PlaybackSettingsIssueCode.PathRequired)]
    [DataRow("relative/cache", PlaybackSettingsIssueCode.PathNotAbsolute)]
    [DataRow("../cache", PlaybackSettingsIssueCode.PathNotAbsolute)]
    [DataRow("/data/playback-cache/../../etc", PlaybackSettingsIssueCode.PathTraversal)]
    [DataRow("/data/..", PlaybackSettingsIssueCode.PathTraversal)]
    [DataRow("/data\\..\\etc", PlaybackSettingsIssueCode.PathTraversal)]
    [DataRow("/", PlaybackSettingsIssueCode.PathInvalid)]
    [DataRow("/data/cache\0x", PlaybackSettingsIssueCode.PathInvalid)]
    public void CacheFolderMustBeAnAbsolutePathWithoutTraversal(string path, PlaybackSettingsIssueCode expected)
    {
        Assert.AreEqual((PlaybackSettingsIssueCode?)expected, PlaybackTranscodingSettingsRules.ValidatePath(path));
    }

    [TestMethod]
    public void NormalFoldersAreAccepted()
    {
        Assert.IsNull(PlaybackTranscodingSettingsRules.ValidatePath("/data/playback-cache/hls"));
        Assert.IsNull(PlaybackTranscodingSettingsRules.ValidatePath("/mnt/fast ssd/hls/"));
        Assert.AreEqual("/mnt/fast", PlaybackTranscodingSettingsRules.NormalizePath(" /mnt/fast/ "));
    }

    [TestMethod]
    public void LimitsBudgetAndFloorAreRangeChecked()
    {
        var defaults = PlaybackTranscodingSettings.Default;

        var issues = PlaybackTranscodingSettingsRules.Validate(defaults with
        {
            SoftwareVideoSessions = -1,
            AudioOnlySessions = PlaybackTranscodingSettings.MaxSessionsPerClass + 1,
            CacheBudgetBytes = 0,
            FreeSpaceFloorBytes = -1
        });

        CollectionAssert.AreEquivalent(
            new[] { "SoftwareVideo", "AudioOnly", nameof(PlaybackTranscodingSettings.CacheBudgetBytes), nameof(PlaybackTranscodingSettings.FreeSpaceFloorBytes) },
            issues.Select(x => x.Field).ToArray());
        Assert.AreEqual(0, PlaybackTranscodingSettingsRules.Validate(defaults with { RemuxSessions = 0 }).Count, "A class may be switched off with zero.");
    }

    [TestMethod]
    public async Task SavedSettingsSurviveARestartAndApplyImmediately()
    {
        var kit = PlaybackServerTestKit.Create();
        var cache = Path.Combine(kit.DataRoot, "hls-cache");
        try
        {
            var result = await kit.Settings.SaveAsync(PlaybackTranscodingSettings.Default with { SoftwareVideoSessions = 1, HlsCachePath = cache + "/", TranscodingEnabled = false });

            Assert.IsTrue(result.Succeeded);
            Assert.AreEqual(cache, kit.Settings.Current.HlsCachePath, "The stored path is normalized.");
            Assert.AreEqual(1, kit.Slots.Capacity(PlaybackCostClass.SoftwareVideo), "Slots read the new limit without a restart.");
            Assert.IsTrue(Directory.Exists(cache), "Saving proves the folder is writable by creating it.");

            var restarted = new PlaybackTranscodingSettingsStore(kit.DataRoot);
            Assert.AreEqual(PlaybackTranscodingSettings.Default, restarted.Current, "Nothing is applied before the stored file is loaded.");
            var loaded = await restarted.LoadAsync();
            Assert.AreEqual(kit.Settings.Current, loaded);
            Assert.IsFalse(loaded.TranscodingEnabled);
        }
        finally
        {
            Directory.Delete(kit.DataRoot, recursive: true);
        }
    }

    [TestMethod]
    public async Task ARejectedSaveStoresNothingAndChangesNothing()
    {
        var kit = PlaybackServerTestKit.Create();

        var traversal = await kit.Settings.SaveAsync(PlaybackTranscodingSettings.Default with { HlsCachePath = "/data/cache/../../etc" });

        Assert.IsFalse(traversal.Succeeded);
        Assert.AreEqual(PlaybackSettingsIssueCode.PathTraversal, traversal.Issues.Single().Code);
        Assert.AreEqual(PlaybackTranscodingSettings.Default, kit.Settings.Current);
        Assert.IsFalse(Directory.Exists(kit.DataRoot), "A rejected save writes nothing.");
    }

    [TestMethod]
    public async Task ACacheFolderTheServerCannotWriteIsRejected()
    {
        var kit = PlaybackServerTestKit.Create();
        Directory.CreateDirectory(kit.DataRoot);
        try
        {
            var blocker = Path.Combine(kit.DataRoot, "a-file");
            await File.WriteAllTextAsync(blocker, "not a folder");

            var result = await kit.Settings.SaveAsync(PlaybackTranscodingSettings.Default with { HlsCachePath = Path.Combine(blocker, "hls") });

            Assert.AreEqual(PlaybackSettingsIssueCode.PathNotWritable, result.Issues.Single().Code);
        }
        finally
        {
            Directory.Delete(kit.DataRoot, recursive: true);
        }
    }

    [TestMethod]
    public async Task AnInvalidStoredFileFailsLoudlyInsteadOfFallingBackToDefaults()
    {
        var kit = PlaybackServerTestKit.Create();
        Directory.CreateDirectory(Path.Combine(kit.DataRoot, "playback"));
        try
        {
            var file = Path.Combine(kit.DataRoot, "playback", PlaybackTranscodingSettingsStore.FileName);

            await File.WriteAllTextAsync(file, "{ not json");
            await Assert.ThrowsAsync<InvalidDataException>(() => kit.Settings.LoadAsync());

            await File.WriteAllTextAsync(file, """{ "hlsCachePath": "/data/../etc", "softwareVideoSessions": 3 }""");
            var exception = await Assert.ThrowsAsync<InvalidDataException>(() => kit.Settings.LoadAsync());
            StringAssert.Contains(exception.Message, nameof(PlaybackSettingsIssueCode.PathTraversal));

            await File.WriteAllTextAsync(file, """{ "softwareVideoSessions": 3 }""");
            var partial = await kit.Settings.LoadAsync();
            Assert.AreEqual(3, partial.SoftwareVideoSessions);
            Assert.AreEqual(PlaybackTranscodingSettings.Default.HardwareVideoSessions, partial.HardwareVideoSessions, "Properties added by later builds default individually.");
        }
        finally
        {
            Directory.Delete(kit.DataRoot, recursive: true);
        }
    }

    [TestMethod]
    public void SlotsAreBoundedPerCostClassAndReleasedOnce()
    {
        var slots = PlaybackServerTestKit.Create().Slots;
        var software = Acquire(slots, PlaybackCostClass.SoftwareVideo, 2);

        Assert.IsNull(slots.TryAcquire(PlaybackCostClass.SoftwareVideo), "Software video is limited to two.");
        Assert.AreEqual(4, Acquire(slots, PlaybackCostClass.HardwareVideo, 4).Count, "A full software class leaves hardware video untouched.");
        Assert.IsNull(slots.TryAcquire(PlaybackCostClass.HardwareVideo));
        Assert.AreEqual(6, Acquire(slots, PlaybackCostClass.Remux, 6).Count);
        Assert.IsNull(slots.TryAcquire(PlaybackCostClass.Remux));
        Assert.AreEqual(8, Acquire(slots, PlaybackCostClass.AudioOnly, 8).Count);
        Assert.IsNull(slots.TryAcquire(PlaybackCostClass.AudioOnly));

        software[0].Dispose();
        software[0].Dispose();
        Assert.AreEqual(1, slots.Available(PlaybackCostClass.SoftwareVideo), "A double dispose releases one slot only.");
        Assert.AreEqual(1, slots.Active(PlaybackCostClass.SoftwareVideo));
    }

    [TestMethod]
    public async Task LoweringALimitStopsNewDeliveriesWithoutEndingRunningOnes()
    {
        var kit = PlaybackServerTestKit.Create();
        try
        {
            var running = Acquire(kit.Slots, PlaybackCostClass.SoftwareVideo, 2);

            var result = await kit.Settings.SaveAsync(PlaybackTranscodingSettings.Default with { SoftwareVideoSessions = 1, HlsCachePath = Path.Combine(kit.DataRoot, "hls") });

            Assert.IsTrue(result.Succeeded);
            Assert.AreEqual(2, kit.Slots.Active(PlaybackCostClass.SoftwareVideo));
            Assert.AreEqual(0, kit.Slots.Available(PlaybackCostClass.SoftwareVideo));
            running[0].Dispose();
            Assert.IsNull(kit.Slots.TryAcquire(PlaybackCostClass.SoftwareVideo), "One session is still running against a limit of one.");
            running[1].Dispose();
            Assert.IsNotNull(kit.Slots.TryAcquire(PlaybackCostClass.SoftwareVideo));
        }
        finally
        {
            Directory.Delete(kit.DataRoot, recursive: true);
        }
    }

    [TestMethod]
    public void CostClassFollowsTheDeliveryAndTheEncoder()
    {
        var hardware = new PlaybackEncoderTarget(PlaybackHardwareBackend.Nvenc);

        Assert.AreEqual(PlaybackCostClass.SoftwareVideo, PlaybackCostClasses.For(Transcode(Video()), PlaybackEncoderTarget.Software));
        Assert.AreEqual(PlaybackCostClass.HardwareVideo, PlaybackCostClasses.For(Transcode(Video()), hardware));
        Assert.AreEqual(PlaybackCostClass.Remux, PlaybackCostClasses.For(Remux(), hardware), "A stream copy costs the same whatever encoder is detected.");
        Assert.AreEqual(PlaybackCostClass.AudioOnly, PlaybackCostClasses.For(AudioOnly(), hardware));
    }

    [TestMethod]
    public async Task TheCacheShedsTheOldestIdleSessionFirstAndAdmitsTheNewOne()
    {
        await using var cache = await CacheAsync(budgetBytes: 1L << 20);
        var ids = new List<Guid>();
        for (var i = 0; i < 3; i++)
        {
            ids.Add((await cache.StartAsync($"profile-{i}", segmentBytes: 400 * 1024)).SessionId);
            cache.Time.Advance(TimeSpan.FromMinutes(2));
        }

        Assert.IsTrue(cache.Manager.IsActive(ids[0], "profile-0"));

        var fourth = await cache.StartAsync("profile-3", segmentBytes: 1);

        Assert.IsFalse(cache.Manager.IsActive(ids[0], "profile-0"), "The oldest idle session made room.");
        Assert.IsFalse(Directory.Exists(cache.SessionDirectory(ids[0])), "Its files are gone.");
        Assert.IsTrue(cache.Manager.IsActive(ids[1], "profile-1"));
        Assert.IsTrue(cache.Manager.IsActive(ids[2], "profile-2"));
        Assert.IsTrue(cache.Manager.IsActive(fourth.SessionId, "profile-3"));
    }

    [TestMethod]
    public async Task APlayingSessionIsNeverDroppedAndAdmissionIsRefusedWithAReason()
    {
        await using var cache = await CacheAsync(budgetBytes: 1L << 20);
        var playing = await cache.StartAsync("profile-0", segmentBytes: 1100 * 1024);
        cache.Time.Advance(TimeSpan.FromSeconds(30));
        var lease = cache.Slots.TryAcquire(PlaybackCostClass.SoftwareVideo);

        var refusal = await Assert.ThrowsAsync<PlaybackAdmissionRefusedException>(() => cache.Manager.StartAsync(Guid.NewGuid(), "profile-1", 0, cache.Arguments, lease, CancellationToken.None));

        Assert.AreEqual(PlaybackAdmissionCodes.CacheBudgetExhausted, refusal.Code);
        Assert.IsTrue(cache.Manager.IsActive(playing.SessionId, "profile-0"), "Only idle sessions are shed.");
        Assert.AreEqual(0, cache.Slots.Active(PlaybackCostClass.SoftwareVideo), "The refused delivery gave its slot back.");
        Assert.AreEqual(1, Directory.GetDirectories(cache.Root).Length, "No directory is created for a refused session.");
    }

    [TestMethod]
    public async Task TheFreeSpaceFloorShedsIdleSessionsThenRefuses()
    {
        await using var cache = await CacheAsync(budgetBytes: 1L << 30, floorBytes: 5L << 30);
        var first = await cache.StartAsync("profile-0", segmentBytes: 10);
        cache.Time.Advance(TimeSpan.FromMinutes(2));
        cache.FreeBytes = 1L << 30;

        var refusal = await Assert.ThrowsAsync<PlaybackAdmissionRefusedException>(() => cache.StartAsync("profile-1", segmentBytes: 10));

        Assert.AreEqual(PlaybackAdmissionCodes.CacheFreeSpaceLow, refusal.Code);
        Assert.IsFalse(cache.Manager.IsActive(first.SessionId, "profile-0"), "Idle sessions were dropped trying to reach the floor.");

        cache.FreeBytes = 6L << 30;
        Assert.IsNotNull(await cache.StartAsync("profile-1", segmentBytes: 10));
    }

    [TestMethod]
    public async Task TheSweeperExpiresIdleSessionsAndOrphanedDirectoriesWithoutAPlaybackRequest()
    {
        await using var cache = await CacheAsync(budgetBytes: 1L << 30);
        var stale = await cache.StartAsync("profile-0", segmentBytes: 10);
        cache.Time.Advance(TimeSpan.FromMinutes(5));
        var fresh = await cache.StartAsync("profile-1", segmentBytes: 10);
        cache.Time.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1));

        var now = cache.Time.GetUtcNow().UtcDateTime;
        var oldOrphan = Directory.CreateDirectory(Path.Combine(cache.Root, Guid.NewGuid().ToString("N")));
        oldOrphan.LastWriteTimeUtc = now - HlsPlaybackSessionManager.IdleLifetime - TimeSpan.FromMinutes(1);
        var youngOrphan = Directory.CreateDirectory(Path.Combine(cache.Root, Guid.NewGuid().ToString("N")));
        youngOrphan.LastWriteTimeUtc = now - TimeSpan.FromMinutes(1);
        var unrelated = Directory.CreateDirectory(Path.Combine(cache.Root, "not-a-session"));
        unrelated.LastWriteTimeUtc = DateTime.UtcNow.AddDays(-30);

        var result = cache.Manager.Sweep();

        Assert.AreEqual(new HlsSweepResult(1, 0, 1), result);
        Assert.IsFalse(cache.Manager.IsActive(stale.SessionId, "profile-0"));
        Assert.IsTrue(cache.Manager.IsActive(fresh.SessionId, "profile-1"));
        Assert.IsFalse(oldOrphan.Exists, "A directory no live session owns is leftover of a crashed process.");
        Assert.IsTrue(youngOrphan.Exists, "A young directory may belong to a session that is just starting elsewhere.");
        Assert.IsTrue(unrelated.Exists, "Only session-shaped directories are ever removed.");
        Assert.IsTrue(Directory.Exists(cache.SessionDirectory(fresh.SessionId)));
    }

    [TestMethod]
    public async Task AForeignFolderIsNeitherAcceptedNorSweptAndAFailedStartLeavesNoDirectory()
    {
        var kit = PlaybackServerTestKit.Create();
        var foreign = Path.Combine(kit.DataRoot, "shared");
        var lookalike = Directory.CreateDirectory(Path.Combine(foreign, Guid.NewGuid().ToString("N")));
        lookalike.LastWriteTimeUtc = DateTime.UtcNow.AddDays(-5);
        try
        {
            var rejected = await kit.Settings.SaveAsync(PlaybackTranscodingSettings.Default with { HlsCachePath = foreign });
            Assert.AreEqual(PlaybackSettingsIssueCode.PathNotEmpty, rejected.Issues.Single().Code);
            Assert.IsFalse(PlaybackCacheOwnership.IsOwnedRoot(foreign));

            var empty = Path.Combine(kit.DataRoot, "empty-cache");
            var saved = await kit.Settings.SaveAsync(PlaybackTranscodingSettings.Default with { HlsCachePath = empty });
            Assert.IsTrue(saved.Succeeded);
            using var manager = kit.Hls(_ => throw new System.ComponentModel.Win32Exception("ffmpeg missing"));
            await Assert.ThrowsAsync<System.ComponentModel.Win32Exception>(() => manager.StartAsync(Guid.NewGuid(), "profile-0", 0, directory => [directory], null, CancellationToken.None));
            Assert.AreEqual(0, Directory.GetDirectories(empty).Length, "A start that fails leaves no session directory behind.");
            Assert.IsTrue(lookalike.Exists);
        }
        finally
        {
            Directory.Delete(kit.DataRoot, recursive: true);
        }
    }

    [TestMethod]
    public async Task TheSweeperAlsoEnforcesTheBudgetOnGrowingSessions()
    {
        await using var cache = await CacheAsync(budgetBytes: 1L << 20);
        var idle = await cache.StartAsync("profile-0", segmentBytes: 300 * 1024);
        cache.Time.Advance(TimeSpan.FromMinutes(2));
        var playing = await cache.StartAsync("profile-1", segmentBytes: 300 * 1024);
        cache.Grow(playing, 500 * 1024);

        var result = cache.Manager.Sweep();

        Assert.AreEqual(1, result.PrunedForPolicy);
        Assert.IsFalse(cache.Manager.IsActive(idle.SessionId, "profile-0"));
        Assert.IsTrue(cache.Manager.IsActive(playing.SessionId, "profile-1"), "Dropping the idle session was enough; the running one stays.");
    }

    [TestMethod]
    public async Task ARunawaySessionIsEndedByTheSweeperWithAReasonAndAtMostOnePerPass()
    {
        await using var cache = await CacheAsync(budgetBytes: 1L << 20);
        var small = await cache.StartAsync("profile-0", segmentBytes: 100 * 1024);
        var runaway = await cache.StartAsync("profile-1", segmentBytes: 100 * 1024);
        cache.Grow(runaway, 1100 * 1024);
        cache.Time.Advance(TimeSpan.FromSeconds(30));

        var first = cache.Manager.Sweep();

        Assert.AreEqual(1, first.PrunedForPolicy);
        Assert.IsFalse(cache.Manager.IsActive(runaway.SessionId, "profile-1"), "The largest running session outgrew the budget.");
        Assert.AreEqual(HlsSessionEndReason.CacheBudget, cache.Manager.EndReason(runaway.SessionId));
        Assert.IsTrue(cache.Manager.IsActive(small.SessionId, "profile-0"));
        Assert.IsFalse(Directory.Exists(cache.SessionDirectory(runaway.SessionId)));
    }

    [TestMethod]
    public async Task AnIdleSessionIsNotShedBeforeAPlayerCouldHaveBufferedAheadUnlessTheBudgetIsExceeded()
    {
        await using var cache = await CacheAsync(budgetBytes: 1L << 20);
        var paused = await cache.StartAsync("profile-0", segmentBytes: 400 * 1024);
        cache.Time.Advance(TimeSpan.FromSeconds(90));
        await cache.StartAsync("profile-1", segmentBytes: 400 * 1024);

        Assert.IsTrue(cache.Manager.IsActive(paused.SessionId, "profile-0"), "Under the budget, a 90 second pause is a buffering player.");

        await cache.StartAsync("profile-2", segmentBytes: 700 * 1024);
        var refused = await Assert.ThrowsAsync<PlaybackAdmissionRefusedException>(() => cache.StartAsync("profile-3", segmentBytes: 1));
        Assert.AreEqual(PlaybackAdmissionCodes.CacheBudgetExhausted, refused.Code, "Over the budget with nothing idle for two minutes, admission is refused.");
        Assert.IsTrue(cache.Manager.IsActive(paused.SessionId, "profile-0"));
    }

    [TestMethod]
    public async Task AnEncoderThatExitsBeforeThePlaylistReportsItsErrorAndLeavesNothingBehind()
    {
        await using var cache = await CacheAsync(budgetBytes: 1L << 30, startOutcome: directory => new FakeHlsProcess { HasExited = true, ErrorSummary = "Cannot load libcuda.so.1" });
        var lease = cache.Slots.TryAcquire(PlaybackCostClass.HardwareVideo);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => cache.Manager.StartAsync(Guid.NewGuid(), "profile-0", 0, cache.Arguments, lease, CancellationToken.None));

        StringAssert.Contains(exception.Message, "Cannot load libcuda.so.1");
        Assert.AreEqual(0, cache.Manager.ActiveSessions);
        Assert.AreEqual(0, cache.Slots.Active(PlaybackCostClass.HardwareVideo), "The slot is released when the session never starts.");
        Assert.AreEqual(0, Directory.GetDirectories(cache.Root).Length);
    }

    [TestMethod]
    public async Task EndingASessionKillsItsProcessAndReleasesItsSlot()
    {
        await using var cache = await CacheAsync(budgetBytes: 1L << 30);
        var lease = cache.Slots.TryAcquire(PlaybackCostClass.SoftwareVideo);
        var session = await cache.Manager.StartAsync(Guid.NewGuid(), "profile-0", 0, cache.Arguments, lease, CancellationToken.None);

        cache.Manager.Stop(session.SessionId, "profile-0");

        Assert.AreEqual(1, cache.Processes.Count);
        Assert.IsTrue(cache.Processes[0].Killed);
        Assert.IsTrue(cache.Processes[0].Disposed);
        Assert.AreEqual(0, cache.Slots.Active(PlaybackCostClass.SoftwareVideo));
    }

    [TestMethod]
    public async Task TheHostedServiceLoadsThePolicyThenDetectsWithoutHoldingUpStartup()
    {
        var gate = new TaskCompletionSource();
        var runner = new BlockingRunner(gate.Task);
        var time = new ManualTimeProvider(s_start);
        var kit = PlaybackServerTestKit.Create(time);
        var hardware = new PlaybackHardwareService(new PlaybackHardwareProbe(runner, time, () => []), kit.Breaker, time, NullLogger<PlaybackHardwareService>.Instance);
        using var manager = kit.Hls(_ => new FakeHlsProcess());
        var saved = await new PlaybackTranscodingSettingsStore(kit.DataRoot).SaveAsync(PlaybackTranscodingSettings.Default with { SoftwareVideoSessions = 1, HlsCachePath = Path.Combine(kit.DataRoot, "hls") });
        Assert.IsTrue(saved.Succeeded);
        var service = new PlaybackServerResourceService(kit.Settings, hardware, manager, time, NullLogger<PlaybackServerResourceService>.Instance);
        try
        {
            await service.StartAsync(CancellationToken.None);

            Assert.AreEqual(1, kit.Settings.Current.SoftwareVideoSessions, "The stored policy applies before the first request.");
            Assert.IsNull(hardware.Detected, "Startup returned while detection is still running.");

            gate.SetResult();
            await WaitUntilAsync(() => hardware.Detected is not null);
            Assert.IsTrue(hardware.Detected!.FfmpegAvailable);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
            Directory.Delete(kit.DataRoot, recursive: true);
        }
    }

    [TestMethod]
    public async Task TheCacheIsSweptWhileDetectionNeverAnswers()
    {
        await using var cache = await CacheAsync(budgetBytes: 1L << 30);
        var stale = await cache.StartAsync("profile-0", segmentBytes: 10);
        cache.Time.Advance(HlsPlaybackSessionManager.IdleLifetime + TimeSpan.FromMinutes(1));
        var hardware = new PlaybackHardwareService(new PlaybackHardwareProbe(new BlockingRunner(new TaskCompletionSource().Task), cache.Time, () => []), cache.Kit.Breaker, cache.Time, NullLogger<PlaybackHardwareService>.Instance);
        var service = new PlaybackServerResourceService(cache.Kit.Settings, hardware, cache.Manager, cache.Time, NullLogger<PlaybackServerResourceService>.Instance);
        try
        {
            await service.StartAsync(CancellationToken.None);

            await WaitUntilAsync(() => !cache.Manager.IsActive(stale.SessionId, "profile-0"));
            Assert.IsNull(hardware.Detected, "The sweeper did not wait for the detection.");
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [TestMethod]
    public async Task TheHostedServiceKeepsRunningWhenTheStoredPolicyIsBroken()
    {
        var kit = PlaybackServerTestKit.Create();
        Directory.CreateDirectory(Path.Combine(kit.DataRoot, "playback"));
        await File.WriteAllTextAsync(Path.Combine(kit.DataRoot, "playback", PlaybackTranscodingSettingsStore.FileName), "{ broken");
        using var manager = kit.Hls(_ => new FakeHlsProcess());
        var service = new PlaybackServerResourceService(kit.Settings, kit.Hardware, manager, kit.Time, NullLogger<PlaybackServerResourceService>.Instance);
        try
        {
            await service.StartAsync(CancellationToken.None);

            Assert.AreEqual(PlaybackTranscodingSettings.Default, kit.Settings.Current, "The defaults apply (and the error is logged) until the policy is saved again.");
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
            Directory.Delete(kit.DataRoot, recursive: true);
        }
    }

    [TestMethod]
    public async Task ACrashedEncoderEndsItsSessionAndFreesItsSlotWhenTheSessionIsPolled()
    {
        await using var cache = await CacheAsync(budgetBytes: 1L << 30);
        var lease = cache.Slots.TryAcquire(PlaybackCostClass.SoftwareVideo);
        var session = await cache.Manager.StartAsync(Guid.NewGuid(), "profile-0", 0, cache.Arguments, lease, CancellationToken.None);
        Assert.AreEqual(1, cache.Slots.Active(PlaybackCostClass.SoftwareVideo));

        cache.Processes[0].HasExited = true;
        cache.Processes[0].ExitCode = 1;

        Assert.IsFalse(cache.Manager.IsActive(session.SessionId, "profile-0"), "A dead session is not a running one, and asking does not change anything.");
        Assert.AreEqual(1, cache.Slots.Active(PlaybackCostClass.SoftwareVideo));

        Assert.AreEqual(1, cache.Manager.ReapExitedProcesses());

        Assert.AreEqual(0, cache.Slots.Active(PlaybackCostClass.SoftwareVideo), "The slot of a crashed encoder is released by the reap, not at idle expiry.");
        Assert.AreEqual(HlsSessionEndReason.EncoderExited, cache.Manager.EndReason(session.SessionId));
        Assert.IsFalse(Directory.Exists(cache.SessionDirectory(session.SessionId)));
    }

    [TestMethod]
    public async Task AFinishedRemuxFreesItsSlotButStaysReadable()
    {
        await using var cache = await CacheAsync(budgetBytes: 1L << 30);
        var episode = Guid.NewGuid();
        var lease = cache.Slots.TryAcquire(PlaybackCostClass.Remux);
        var session = await cache.Manager.StartAsync(episode, "profile-0", 0, cache.Arguments, lease, CancellationToken.None);

        cache.Processes[0].HasExited = true;
        cache.Processes[0].ExitCode = 0;

        Assert.IsTrue(cache.Manager.IsActive(session.SessionId, "profile-0"));
        Assert.AreEqual(0, cache.Manager.ReapExitedProcesses(), "A clean exit ends nothing.");
        Assert.IsNotNull(cache.Manager.GetAsset(session.SessionId, episode, "profile-0", "index.m3u8"), "A completed remux is still served until it expires.");
        Assert.AreEqual(0, cache.Slots.Active(PlaybackCostClass.Remux), "No encoder runs any more.");
        Assert.IsNull(cache.Manager.EndReason(session.SessionId));
    }

    [TestMethod]
    public async Task TheSweeperReapsCrashedEncodersWithoutAPlaybackRequest()
    {
        await using var cache = await CacheAsync(budgetBytes: 1L << 30);
        var lease = cache.Slots.TryAcquire(PlaybackCostClass.SoftwareVideo);
        await cache.Manager.StartAsync(Guid.NewGuid(), "profile-0", 0, cache.Arguments, lease, CancellationToken.None);
        cache.Processes[0].HasExited = true;
        cache.Processes[0].ExitCode = 137;

        var result = cache.Manager.Sweep();

        Assert.AreEqual(1, result.CrashedSessions);
        Assert.AreEqual(0, cache.Manager.ActiveSessions);
        Assert.AreEqual(0, cache.Slots.Active(PlaybackCostClass.SoftwareVideo));
    }

    [TestMethod]
    public async Task ARefusedStartNeverCostsTheProfileItsOwnRunningSession()
    {
        await using var cache = await CacheAsync(budgetBytes: 1L << 20);
        var first = await cache.StartAsync("profile-0", segmentBytes: 600 * 1024);
        var second = await cache.StartAsync("profile-0", segmentBytes: 600 * 1024);

        var refusal = await Assert.ThrowsAsync<PlaybackAdmissionRefusedException>(() => cache.StartAsync("profile-0", segmentBytes: 1));

        Assert.AreEqual(PlaybackAdmissionCodes.CacheBudgetExhausted, refusal.Code);
        Assert.IsTrue(cache.Manager.IsActive(first.SessionId, "profile-0"), "The refusal came before any eviction.");
        Assert.IsTrue(cache.Manager.IsActive(second.SessionId, "profile-0"));
    }

    [TestMethod]
    public async Task ACacheFolderThatIsNotJularrsIsRefusedAtStartAndLeftUntouched()
    {
        var kit = PlaybackServerTestKit.Create();
        var foreign = Path.Combine(kit.DataRoot, "shared");
        Directory.CreateDirectory(foreign);
        Directory.CreateDirectory(Path.Combine(kit.DataRoot, "playback"));
        await File.WriteAllTextAsync(Path.Combine(foreign, "photos.txt"), "mine");
        await File.WriteAllTextAsync(
            Path.Combine(kit.DataRoot, "playback", PlaybackTranscodingSettingsStore.FileName),
            $"{{ \"hlsCachePath\": \"{foreign.Replace('\\', '/')}\" }}");
        try
        {
            await kit.Settings.LoadAsync();
            using var manager = kit.Hls(_ => new FakeHlsProcess());
            var lease = kit.Slots.TryAcquire(PlaybackCostClass.SoftwareVideo);

            var refusal = await Assert.ThrowsAsync<PlaybackAdmissionRefusedException>(() => manager.StartAsync(Guid.NewGuid(), "profile-0", 0, directory => [directory], lease, CancellationToken.None));

            Assert.AreEqual(PlaybackAdmissionCodes.CacheFolderNotOwned, refusal.Code);
            Assert.AreEqual(0, kit.Slots.Active(PlaybackCostClass.SoftwareVideo));
            Assert.IsFalse(File.Exists(Path.Combine(foreign, PlaybackCacheOwnership.MarkerFileName)), "A foreign folder is never marked as Jularr's.");
            Assert.IsTrue(File.Exists(Path.Combine(foreign, "photos.txt")));
        }
        finally
        {
            Directory.Delete(kit.DataRoot, recursive: true);
        }
    }

    [TestMethod]
    public async Task OnlySessionDirectoriesOfAnOwnedRootAreDeletable()
    {
        await using var cache = await CacheAsync(budgetBytes: 1L << 30);
        var session = await cache.StartAsync("profile-0", segmentBytes: 10);
        var other = Directory.CreateDirectory(Path.Combine(cache.Root, "not-a-session"));

        Assert.IsTrue(PlaybackCacheOwnership.IsDeletableSession(cache.Root, cache.SessionDirectory(session.SessionId)));
        Assert.IsFalse(PlaybackCacheOwnership.IsDeletableSession(cache.Root, other.FullName), "Only 32-hex session directories.");
        Assert.IsFalse(PlaybackCacheOwnership.IsDeletableSession(cache.Root, cache.Root), "Never the root itself.");
        Assert.IsFalse(PlaybackCacheOwnership.IsDeletableSession(Path.Combine(cache.Root, "elsewhere"), cache.SessionDirectory(session.SessionId)), "Only direct children of the root.");
    }

    [TestMethod]
    public void APercentSignWouldBreakTheSegmentFilenameTemplateAndIsRefused()
    {
        Assert.AreEqual(PlaybackSettingsIssueCode.PathInvalid, PlaybackTranscodingSettingsRules.ValidatePath("/data/cache%05d"));
    }

    [TestMethod]
    public async Task OpeningTheSettingsPageReadsWithoutChangingWhatTheServerEnforces()
    {
        var kit = PlaybackServerTestKit.Create();
        var other = new PlaybackTranscodingSettingsStore(kit.DataRoot);
        try
        {
            var saved = await other.SaveAsync(PlaybackTranscodingSettings.Default with { SoftwareVideoSessions = 1, HlsCachePath = Path.Combine(kit.DataRoot, "hls") });
            Assert.IsTrue(saved.Succeeded);

            var read = await kit.Settings.ReadStoredAsync();

            Assert.AreEqual(1, read.SoftwareVideoSessions);
            Assert.AreEqual(PlaybackTranscodingSettings.Default, kit.Settings.Current, "A read for display is not a load.");
        }
        finally
        {
            Directory.Delete(kit.DataRoot, recursive: true);
        }
    }

    [TestMethod]
    public void OperatingSystemEntriesDoNotMakeADedicatedMountPointForeign()
    {
        var root = Path.Combine(Path.GetTempPath(), $"jularr-owned-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "lost+found"));
            Directory.CreateDirectory(Path.Combine(root, ".Trash-1000"));
            Assert.IsTrue(PlaybackCacheOwnership.IsOwnedRoot(root), "A fresh ext4 mount holds lost+found and a trash folder.");

            File.WriteAllText(Path.Combine(root, "holiday.jpg"), "x");
            Assert.IsFalse(PlaybackCacheOwnership.IsOwnedRoot(root), "Any other entry makes the folder somebody else's.");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task SavingClaimsTheFolderSoLaterFilesCannotMakeItForeign()
    {
        var kit = PlaybackServerTestKit.Create();
        var cache = Path.Combine(kit.DataRoot, "claimed");
        try
        {
            var saved = await kit.Settings.SaveAsync(PlaybackTranscodingSettings.Default with { HlsCachePath = cache });
            Assert.IsTrue(saved.Succeeded);
            Assert.IsTrue(File.Exists(Path.Combine(cache, PlaybackCacheOwnership.MarkerFileName)), "The marker is written at save time.");

            await File.WriteAllTextAsync(Path.Combine(cache, "added-later.txt"), "x");

            Assert.IsTrue(PlaybackCacheOwnership.IsOwnedRoot(cache));
        }
        finally
        {
            Directory.Delete(kit.DataRoot, recursive: true);
        }
    }

    [TestMethod]
    public async Task OrphansAreDeletedBeforeAHealthySessionIsEnded()
    {
        await using var cache = await CacheAsync(budgetBytes: 1L << 20);
        var session = await cache.StartAsync("profile-0", segmentBytes: 100 * 1024);
        var orphan = Directory.CreateDirectory(Path.Combine(cache.Root, Guid.NewGuid().ToString("N")));
        await File.WriteAllBytesAsync(Path.Combine(orphan.FullName, "segment-00000.m4s"), new byte[1100 * 1024]);
        orphan.LastWriteTimeUtc = cache.Time.GetUtcNow().UtcDateTime - HlsPlaybackSessionManager.IdleLifetime - TimeSpan.FromMinutes(1);
        cache.Time.Advance(TimeSpan.FromSeconds(30));

        var result = cache.Manager.Sweep();

        Assert.IsFalse(orphan.Exists, "The restart leftovers go first.");
        Assert.IsTrue(cache.Manager.IsActive(session.SessionId, "profile-0"), "Deleting them already cleared the budget.");
        Assert.AreEqual(0, result.PrunedForPolicy);
        Assert.AreEqual(1, result.OrphanDirectories);
    }

    [TestMethod]
    public async Task ASessionIsNotEndedForAConditionItsOwnBytesCannotClear()
    {
        await using var cache = await CacheAsync(budgetBytes: 1L << 20);
        var session = await cache.StartAsync("profile-0", segmentBytes: 100 * 1024);
        var young = Directory.CreateDirectory(Path.Combine(cache.Root, Guid.NewGuid().ToString("N")));
        await File.WriteAllBytesAsync(Path.Combine(young.FullName, "segment-00000.m4s"), new byte[1100 * 1024]);
        young.LastWriteTimeUtc = cache.Time.GetUtcNow().UtcDateTime;
        cache.Time.Advance(TimeSpan.FromSeconds(30));

        var result = cache.Manager.Sweep();

        Assert.IsTrue(cache.Manager.IsActive(session.SessionId, "profile-0"), "Killing a 100 KiB session cannot fix a cache that foreign or young bytes keep over its budget.");
        Assert.IsTrue(young.Exists);
        Assert.AreEqual(0, result.PrunedForPolicy);
    }

    [TestMethod]
    public async Task AVolumeThatIsLowForOtherReasonsDoesNotCostTheLargestSessionButAClearableOneDoes()
    {
        await using var cache = await CacheAsync(budgetBytes: 1L << 30, floorBytes: 5L << 30);
        var session = await cache.StartAsync("profile-0", segmentBytes: 100 * 1024);
        cache.Time.Advance(TimeSpan.FromSeconds(30));

        cache.FreeBytes = (5L << 30) - (10L << 20);
        cache.Manager.Sweep();
        Assert.IsTrue(cache.Manager.IsActive(session.SessionId, "profile-0"), "100 KiB would not lift the volume over its floor.");

        cache.FreeBytes = (5L << 30) - 50 * 1024;
        cache.Manager.Sweep();
        Assert.IsFalse(cache.Manager.IsActive(session.SessionId, "profile-0"), "Ending it clears the floor.");
        Assert.AreEqual(HlsSessionEndReason.CacheFreeSpace, cache.Manager.EndReason(session.SessionId));
    }

    [TestMethod]
    public async Task RunningSessionsOfAnOldFolderStillCountAndItsLeftoversAreSweptOnce()
    {
        await using var cache = await CacheAsync(budgetBytes: 1L << 20);
        var oldRoot = cache.Root;
        var running = await cache.StartAsync("profile-0", segmentBytes: 600 * 1024);
        var leftover = Directory.CreateDirectory(Path.Combine(oldRoot, Guid.NewGuid().ToString("N")));
        await File.WriteAllBytesAsync(Path.Combine(leftover.FullName, "segment-00000.m4s"), new byte[10]);
        leftover.LastWriteTimeUtc = cache.Time.GetUtcNow().UtcDateTime - HlsPlaybackSessionManager.IdleLifetime - TimeSpan.FromMinutes(1);

        var newRoot = Path.Combine(cache.Kit.DataRoot, "hls-new");
        Assert.IsTrue((await cache.Kit.Settings.SaveAsync(cache.Kit.Settings.Current with { HlsCachePath = newRoot })).Succeeded);
        var inNew = await cache.StartAsync("profile-1", segmentBytes: 300 * 1024);
        Assert.IsTrue(Directory.Exists(Path.Combine(newRoot, inNew.SessionId.ToString("N"))), "New sessions use the new folder.");

        await cache.StartAsync("profile-2", segmentBytes: 400 * 1024);
        var refusal = await Assert.ThrowsAsync<PlaybackAdmissionRefusedException>(() => cache.StartAsync("profile-3", segmentBytes: 1));
        Assert.AreEqual(PlaybackAdmissionCodes.CacheBudgetExhausted, refusal.Code, "The old folder's running session is part of the budget until it ends.");

        var result = cache.Manager.Sweep();
        Assert.IsFalse(leftover.Exists, "The retired folder's aged leftovers are swept.");
        Assert.IsTrue(result.OrphanDirectories >= 1);
        Assert.AreEqual(HlsSessionEndReason.CacheBudget, cache.Manager.EndReason(running.SessionId), "The old folder's session is the largest and ends first when the budget is exceeded.");
    }

    [TestMethod]
    public async Task ASweepOfAVanishedCacheFolderIsHarmless()
    {
        await using var cache = await CacheAsync(budgetBytes: 1L << 30);
        await cache.StartAsync("profile-0", segmentBytes: 10);
        Directory.Delete(cache.Root, recursive: true);

        var result = cache.Manager.Sweep();

        Assert.AreEqual(0, result.OrphanDirectories);
    }

    [TestMethod]
    public async Task OnlyTheNewestEndReasonsAreRemembered()
    {
        await using var cache = await CacheAsync(budgetBytes: 1L << 30);
        var sessions = new List<Guid>();
        for (var i = 0; i < 140; i++)
        {
            sessions.Add((await cache.StartAsync($"profile-{i}", segmentBytes: 1)).SessionId);
        }

        cache.Time.Advance(HlsPlaybackSessionManager.IdleLifetime + TimeSpan.FromMinutes(1));
        Assert.AreEqual(140, cache.Manager.CleanupExpired());

        Assert.AreEqual(128, sessions.Count(id => cache.Manager.EndReason(id) is not null), "The memory is bounded; the oldest endings make room.");
    }

    private static List<IDisposable> Acquire(PlaybackTranscodeSlots slots, PlaybackCostClass costClass, int count) =>
        [.. Enumerable.Range(0, count).Select(_ => slots.TryAcquire(costClass) ?? throw new AssertFailedException($"{costClass} slot {_} should be free."))];

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.IsTrue(DateTime.UtcNow < deadline, "The condition did not become true in time.");
            await Task.Delay(10);
        }
    }

    private static async Task<CacheHarness> CacheAsync(long budgetBytes, long floorBytes = 0, long? freeBytes = null, Func<string, FakeHlsProcess>? startOutcome = null)
    {
        var kit = PlaybackServerTestKit.Create(new ManualTimeProvider(s_start));
        var root = Path.Combine(kit.DataRoot, "hls");
        var saved = await kit.Settings.SaveAsync(PlaybackTranscodingSettings.Default with { HlsCachePath = root, CacheBudgetBytes = budgetBytes, FreeSpaceFloorBytes = floorBytes });
        Assert.IsTrue(saved.Succeeded);
        return new CacheHarness(kit, root, freeBytes, startOutcome);
    }

    /// <summary>A session manager over a real temporary cache folder whose ffmpeg writes the playlist and segments itself.</summary>
    private sealed class CacheHarness : IAsyncDisposable
    {
        private readonly PlaybackServerTestKit _kit;
        private readonly Func<string, FakeHlsProcess>? _startOutcome;
        private string? _lastDirectory;

        public CacheHarness(PlaybackServerTestKit kit, string root, long? freeBytes, Func<string, FakeHlsProcess>? startOutcome)
        {
            _kit = kit;
            _startOutcome = startOutcome;
            Root = root;
            FreeBytes = freeBytes;
            Manager = _kit.Hls(StartProcess, _ => FreeBytes);
        }

        public PlaybackServerTestKit Kit => _kit;

        public string Root { get; }

        public long? FreeBytes { get; set; }

        public HlsPlaybackSessionManager Manager { get; }

        public ManualTimeProvider Time => (ManualTimeProvider)_kit.Time;

        public PlaybackTranscodeSlots Slots => _kit.Slots;

        public List<FakeHlsProcess> Processes { get; } = [];

        public IReadOnlyList<string> Arguments(string directory)
        {
            _lastDirectory = directory;
            return [directory];
        }

        public string SessionDirectory(Guid sessionId) => Path.Combine(Root, sessionId.ToString("N"));

        public async Task<HlsPlaybackSession> StartAsync(string profileId, int segmentBytes)
        {
            SegmentBytes = segmentBytes;
            return await Manager.StartAsync(Guid.NewGuid(), profileId, 0, Arguments, null, CancellationToken.None);
        }

        public void Grow(HlsPlaybackSession session, int bytes) =>
            File.WriteAllBytes(Path.Combine(SessionDirectory(session.SessionId), "segment-00099.m4s"), new byte[bytes]);

        private int SegmentBytes { get; set; }

        private IHlsEncoderProcess StartProcess(IReadOnlyList<string> arguments)
        {
            var directory = _lastDirectory!;
            var process = _startOutcome?.Invoke(directory) ?? new FakeHlsProcess();
            Processes.Add(process);
            if (!process.HasExited)
            {
                File.WriteAllText(Path.Combine(directory, "index.m3u8"), "#EXTM3U\n");
                File.WriteAllBytes(Path.Combine(directory, "init.mp4"), [0]);
                File.WriteAllBytes(Path.Combine(directory, "segment-00000.m4s"), new byte[SegmentBytes]);
            }

            return process;
        }

        public ValueTask DisposeAsync()
        {
            Manager.Dispose();
            if (Directory.Exists(_kit.DataRoot))
            {
                Directory.Delete(_kit.DataRoot, recursive: true);
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeHlsProcess : IHlsEncoderProcess
    {
        public bool HasExited { get; set; }

        public int? ExitCode { get; set; }

        public string ErrorSummary { get; set; } = "";

        public bool Killed { get; private set; }

        public bool Disposed { get; private set; }

        public void Kill()
        {
            Killed = true;
            HasExited = true;
        }

        public void Dispose() => Disposed = true;
    }

    private sealed class BlockingRunner(Task gate) : IMediaProcessRunner
    {
        public async Task<MediaProcessResult?> RunAsync(string executable, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
        {
            await gate.WaitAsync(cancellationToken);
            return arguments.Contains("-encoders") ? new MediaProcessResult(0, "Encoders:\n ------\n V....D libx264 x264\n", "") : new MediaProcessResult(0, "", "");
        }
    }
}
