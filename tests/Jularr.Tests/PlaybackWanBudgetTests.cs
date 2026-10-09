using Jularr.Web.Features.Playback.Decision;
using Jularr.Web.Features.Playback.Transcoding;
using static Jularr.Tests.PlaybackTestPlans;

namespace Jularr.Tests;

[TestClass]
public sealed class PlaybackWanBudgetTests
{
    [TestMethod]
    public async Task Settings_ManualUploadBudget_PersistsAndRejectsInvalidValues()
    {
        var kit = PlaybackServerTestKit.Create();
        try
        {
            Assert.AreEqual(PlaybackWanUploadMode.Off, PlaybackTranscodingSettings.Default.WanUploadMode);
            Assert.AreEqual(0, kit.Capabilities.WanUploadBudgetKbps);

            var saved = await kit.Settings.SaveAsync(
                PlaybackTranscodingSettings.Default with
                {
                    HlsCachePath = Path.Combine(kit.DataRoot, "hls"),
                    WanUploadBudgetKbps = 10_000,
                    WanUploadMode = PlaybackWanUploadMode.Manual
                });
            Assert.IsTrue(saved.Succeeded);
            Assert.AreEqual(10_000, kit.Capabilities.WanUploadBudgetKbps);

            var restarted = new PlaybackTranscodingSettingsStore(kit.DataRoot);
            Assert.AreEqual(PlaybackWanUploadMode.Manual, (await restarted.LoadAsync()).WanUploadMode);
            Assert.AreEqual(10_000, restarted.Current.EffectiveWanUploadBudgetKbps);

            var settingsPath = Path.Combine(kit.DataRoot, "playback", PlaybackTranscodingSettingsStore.FileName);
            var legacyJson = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(settingsPath))!.AsObject();
            legacyJson.Remove("wanUploadMode");
            await File.WriteAllTextAsync(settingsPath, legacyJson.ToJsonString());
            var legacy = new PlaybackTranscodingSettingsStore(kit.DataRoot);
            Assert.AreEqual(PlaybackWanUploadMode.Manual, (await legacy.LoadAsync()).WanUploadMode);

            Assert.IsTrue((await kit.Settings.SaveAsync(kit.Settings.Current with
            {
                WanUploadMode = PlaybackWanUploadMode.Automatic
            })).Succeeded);
            Assert.AreEqual(PlaybackTranscodingSettings.AutomaticFallbackBudgetKbps, kit.Capabilities.WanUploadBudgetKbps);

            Assert.IsTrue((await kit.Settings.SaveAsync(kit.Settings.Current with
            {
                WanUploadMode = PlaybackWanUploadMode.Off
            })).Succeeded);
            Assert.AreEqual(0, kit.Capabilities.WanUploadBudgetKbps);

            Assert.IsTrue((await kit.Settings.SaveAsync(kit.Settings.Current with
            {
                WanUploadMode = PlaybackWanUploadMode.Manual
            })).Succeeded);
            Assert.AreEqual(10_000, kit.Capabilities.WanUploadBudgetKbps);

            var invalid = await kit.Settings.SaveAsync(
                kit.Settings.Current with
                {
                    WanUploadBudgetKbps = PlaybackTranscodingSettings.MaxWanUploadBudgetKbps + 1
                });
            Assert.IsFalse(invalid.Succeeded);
            Assert.AreEqual(PlaybackSettingsIssueCode.WanUploadBudgetInvalid, invalid.Issues.Single().Code);
            Assert.AreEqual(10_000, kit.Settings.Current.WanUploadBudgetKbps);

            var invalidMode = await kit.Settings.SaveAsync(
                kit.Settings.Current with { WanUploadMode = (PlaybackWanUploadMode)999 });
            Assert.IsFalse(invalidMode.Succeeded);
            Assert.AreEqual(PlaybackSettingsIssueCode.WanUploadModeInvalid, invalidMode.Issues.Single().Code);
        }
        finally
        {
            if (Directory.Exists(kit.DataRoot))
            {
                Directory.Delete(kit.DataRoot, recursive: true);
            }
        }
    }

    [TestMethod]
    public void ActiveExternalDeliveries_DoesNotCountLocalOrReplacedViewerTwice()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-10-09T12:00:00Z"));
        var sessions = new PlaybackStreamSessionStore(clock);
        var selections = new PlaybackStreamSelections(
            null, null, false, PlaybackQualityPreset.Auto, PlaybackModePreference.Auto, "web");
        var remote = Transcode(Video()) with
        {
            Quality = new PlaybackQualityResolution(
                PlaybackQualityPreset.Auto, PlaybackNetworkClass.Remote,
                8_000, PlaybackLimitSource.Network, 12_000, 8_000)
        };
        var local = remote with
        {
            Quality = remote.Quality with { Network = PlaybackNetworkClass.Local }
        };
        var first = sessions.Create(
            "viewer", PlaybackVideoTarget.Movie(1), Guid.NewGuid(),
            "/media/a.mkv", 1400, remote, selections);
        sessions.Create(
            "local", PlaybackVideoTarget.Movie(1), Guid.NewGuid(),
            "/media/a.mkv", 1400, local, selections);

        Assert.AreEqual(0, sessions.ActiveExternalDeliveries(), "A playback plan without a successful stream is not an active WAN viewer.");
        first.MarkDeliveryStarted();
        Assert.AreEqual(1, sessions.ActiveExternalDeliveries());
        sessions.Create(
            "viewer", PlaybackVideoTarget.Movie(1), Guid.NewGuid(),
            "/media/a.mkv", 1400, remote, selections,
            replaces: first.Id, deferRetirement: true);
        Assert.AreEqual(1, sessions.ActiveExternalDeliveries());

        clock.Advance(TimeSpan.FromSeconds(31));
        Assert.AreEqual(0, sessions.ActiveExternalDeliveries());
    }

    [TestMethod]
    public void ActiveExternalDeliveries_AuthenticatedPlaybackTelemetryActivatesDirectFileSession()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-10-09T12:00:00Z"));
        var sessions = new PlaybackStreamSessionStore(clock);
        var plan = Transcode(Video()) with
        {
            Quality = new PlaybackQualityResolution(
                PlaybackQualityPreset.Auto, PlaybackNetworkClass.Remote,
                8_000, PlaybackLimitSource.Network, 12_000, 8_000)
        };
        var session = sessions.Create(
            "viewer", PlaybackVideoTarget.Movie(1), Guid.NewGuid(), "/media/movie.mp4", 1400,
            plan, new PlaybackStreamSelections(null, null, false, PlaybackQualityPreset.Auto, PlaybackModePreference.Auto, "web"));

        Assert.AreEqual(0, sessions.ActiveExternalDeliveries());

        Assert.IsTrue(sessions.ReportTelemetry(session.Id, "viewer", new PlaybackTelemetry(
            1, clock.GetUtcNow(), PlaybackClientState.Playing, 5, 5000, 0, 0, 1)));

        Assert.AreEqual(1, sessions.ActiveExternalDeliveries());
        Assert.IsTrue(sessions.ReportTelemetry(session.Id, "viewer", new PlaybackTelemetry(
            2, clock.GetUtcNow(), PlaybackClientState.Paused, 5, null, 0, 0, 1)));
        Assert.AreEqual(0, sessions.ActiveExternalDeliveries());
    }
}
