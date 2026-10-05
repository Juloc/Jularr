using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Jularr.Web.Features.ClientApi;
using Jularr.Web.Features.Library;
using Jularr.Web.Features.Localization;
using Jularr.Web.Features.Media.Compatibility;
using Jularr.Web.Features.Playback;
using Jularr.Web.Features.Playback.Decision;
using Jularr.Web.Features.Playback.Transcoding;
using Jularr.Web.Infrastructure;
using Jularr.Web.Features.Storage;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Jularr.Tests;

/// <summary>
/// Starting playback from a plan: sleeping storage is woken only by a play intent, one HLS
/// output per position, the service registration, and the diagnostics contract of the player.
/// </summary>
[TestClass]
public sealed partial class PlaybackPlanStartTests
{
    private const string ChromeAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36";

    [TestMethod]
    public async Task SleepingStorageIsWokenOnlyWhenThePlanIsAPlayIntent()
    {
        await using var fixture = await MediaInventoryFixture.CreateAsync();
        var media = await fixture.AddMediaAsync("episode.mkv", new byte[4096]);
        fixture.Root.WakeOnLanEnabled = true;
        fixture.Root.WakeMacAddress = "AA:BB:CC:DD:EE:FF";
        await fixture.Db.SaveChangesAsync();
        // The NAS is asleep: its share is not mounted.
        Directory.Delete(fixture.Root.Path, recursive: true);

        var sender = new CountingWakeSender();
        var availability = new StorageAvailabilityCoordinator();
        var wake = new StorageWakeCoordinator(
            availability,
            sender,
            new StorageWakeOptions { StartTimeout = TimeSpan.FromSeconds(5), PollInterval = TimeSpan.FromMilliseconds(50) },
            NullLogger<StorageWakeCoordinator>.Instance);
        var roots = new LibraryRootAvailabilityService(fixture.Db, availability, wake);
        var store = new PlaybackStreamSessionStore(TimeProvider.System);
        var service = new PlaybackPlanService(
            fixture.Db,
            fixture.Inventory,
            store,
            PlaybackServerTestKit.Create().Capabilities,
            new MediaAvailabilityService(fixture.Db, roots));
        var input = new PlaybackPlanInput(null, ClientKinds.Web, ChromeAgent, IPAddress.Loopback);

        var opened = await service.PlanAsync(media.EpisodeId!.Value, "reader", input with { Wake = false }, CancellationToken.None);
        Assert.AreEqual(PlaybackDeliveryMode.Unavailable, opened!.Plan.Mode);
        Assert.AreEqual(PlaybackReasonCodes.MediaUnavailable, opened.Plan.Reasons.Single().Code);
        Assert.IsNull(opened.Session);
        Assert.IsFalse(opened.Availability!.IsAvailable);
        Assert.AreEqual(0, sender.Sent, "Opening the page only decides; it never wakes the NAS.");

        var played = await service.PlanAsync(media.EpisodeId!.Value, "reader", input, CancellationToken.None);
        Assert.AreEqual(PlaybackDeliveryMode.Unavailable, played!.Plan.Mode);
        Assert.AreEqual(StorageAvailabilityState.Starting, played.Availability!.State,
            "Play starts the shared wake attempt and reports Starting so the client polls and re-plans.");
        Assert.IsTrue(played.Availability.Retryable);
        await WaitUntilAsync(() => sender.Sent == 1);

        await service.PlanAsync(media.EpisodeId!.Value, "reader", input, CancellationToken.None);
        Assert.AreEqual(1, sender.Sent, "A second play request joins the running start attempt.");
        Assert.AreEqual(0, store.Count, "No stream session exists before the media is readable.");
    }

    [TestMethod]
    public async Task ConcurrentHlsRequestsForOnePositionStartOneOutput()
    {
        var session = Session();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var starts = 0;
        var stopped = new List<Guid>();
        Func<CancellationToken, Task<Guid?>> start = async _ =>
        {
            Interlocked.Increment(ref starts);
            await release.Task;
            return Guid.NewGuid();
        };

        var first = session.EnsureHlsAsync(120, _ => true, start, stopped.Add, CancellationToken.None);
        var second = session.EnsureHlsAsync(120.2, _ => true, start, stopped.Add, CancellationToken.None);
        release.SetResult();
        var running = await first;
        Assert.AreEqual(running, await second, "The probe and the player share the output.");
        Assert.AreEqual(1, starts);
        Assert.AreEqual(0, stopped.Count);

        var restarted = await session.EnsureHlsAsync(120, _ => false, start, stopped.Add, CancellationToken.None);
        Assert.AreNotEqual(running, restarted, "An output that ended (idle cleanup, ffmpeg exit) is started again.");
        CollectionAssert.AreEqual(new[] { running!.Value }, stopped);

        var seeked = await session.EnsureHlsAsync(300, _ => true, start, stopped.Add, CancellationToken.None);
        Assert.AreEqual(3, starts);
        Assert.AreEqual(restarted, stopped[^1], "A new position replaces the running output.");
        Assert.AreEqual(seeked, session.HlsSessionAt(300));

        var busy = await session.EnsureHlsAsync(600, _ => true, _ => Task.FromResult<Guid?>(null), stopped.Add, CancellationToken.None);
        Assert.IsNull(busy, "Without a free transcode slot nothing starts.");
        Assert.IsNull(session.HlsSessionId);
        Assert.AreEqual(seeked, stopped[^1]);
    }

    [TestMethod]
    public void PlansNameTheSourceContainerForDiagnostics()
    {
        var media = PlaybackMediaProfile.From(
            "/media/episode.mkv",
            3_200_000_000,
            MediaProbeParser.Parse(MediaProbeFixtures.HevcTenBitHdrMultiAudio));
        var plan = PlaybackDecisionEngine.Decide(new(
            media,
            ClientPlaybackCapabilities.InferFromUserAgent(ChromeAgent, ClientKinds.Web),
            PlaybackServerCapabilities.Software()));

        Assert.AreEqual("matroska", plan.SourceContainer);
        Assert.AreEqual("mp4", plan.Container);
        var json = JsonSerializer.Serialize(plan, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        StringAssert.Contains(json, "\"sourceContainer\":\"matroska\"");
        StringAssert.Contains(json, "\"mode\":\"transcode\"");
        StringAssert.Contains(json, "\"confidence\":\"inferred\"");
    }

    [TestMethod]
    public void PlaybackDecisionServicesAndRateLimitAreRegisteredTogether()
    {
        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton(TimeProvider.System)
            .AddSingleton<IMediaProcessRunner>(new FakeMediaProcessRunner(_ => null))
            .AddPlaybackDecision();
        using var provider = services.BuildServiceProvider();

        Assert.IsNotNull(provider.GetRequiredService<PlaybackStreamSessionStore>());
        Assert.IsNotNull(provider.GetRequiredService<HlsPlaybackSessionManager>());
        Assert.AreSame(provider.GetRequiredService<PlaybackHardwareService>(), provider.GetRequiredService<PlaybackHardwareService>());
        Assert.IsTrue(
            provider.GetServices<IHostedService>().Any(x => x is PlaybackServerResourceService),
            "Hardware detection and the cache sweeper run in the one hosted resource service.");
        Assert.AreSame(
            provider.GetRequiredService<PlaybackTranscodeSlots>(),
            provider.GetRequiredService<PlaybackTranscodeSlots>());
        Assert.IsTrue(provider.GetRequiredService<PlaybackServerCapabilityProvider>().Current().ProcessingAvailable);
        Assert.IsTrue(services.Any(x =>
            x.ServiceType == typeof(PlaybackPlanService) && x.Lifetime == ServiceLifetime.Scoped));
        // Materializing the options runs the policy registration (a duplicate name would throw).
        Assert.IsNotNull(provider.GetRequiredService<IOptions<RateLimiterOptions>>().Value);
    }

    [TestMethod]
    public void DiagnosticsPanelOnlyUsesExistingUiTexts()
    {
        var root = PlayerControlsTests.RepositoryRoot();
        var player = File.ReadAllText(Path.Combine(root, "src", "Jularr.Web", "wwwroot", "js", "episode-player.js"));
        var page = EpisodePlayerSource.Read(root);

        foreach (Match match in StaticTextKey().Matches(player))
        {
            var key = match.Groups["key"].Value;
            Assert.IsTrue(UiTranslationResources.TryGet(key, out _), $"{key} is used by the player but missing.");
        }

        foreach (var row in new[] { "mode", "source", "delivered", "audio", "quality", "support", "processing", "buffer", "droppedFrames" })
        {
            StringAssert.Contains(player, $"[\"{row}\",");
            Assert.IsTrue(UiTranslationResources.TryGet($"playback.diagnostics.{row}", out _), $"playback.diagnostics.{row} is missing.");
        }

        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        foreach (var support in Enum.GetValues<PlaybackCapabilitySupport>())
        {
            var name = JsonSerializer.Serialize(support, options).Trim('"');
            Assert.IsTrue(UiTranslationResources.TryGet($"playback.support.{name}", out _), $"playback.support.{name} is missing.");
        }

        foreach (var network in Enum.GetValues<PlaybackNetworkClass>())
        {
            var name = JsonSerializer.Serialize(network, options).Trim('"');
            Assert.IsTrue(UiTranslationResources.TryGet($"playback.network.{name}", out _), $"playback.network.{name} is missing.");
        }

        // Diagnostics live folded inside the player settings, never as a permanent overlay.
        var settings = page.IndexOf("player-setting-status", StringComparison.Ordinal);
        var diagnostics = page.IndexOf("data-playback-diagnostics", StringComparison.Ordinal);
        Assert.IsTrue(settings > 0 && diagnostics > settings);
        StringAssert.Contains(page, "<details class=\"player-diagnostics\"");
        StringAssert.Contains(page, "data-app-version=", "The capability cache is keyed by the Jularr build.");
    }

    private static PlaybackStreamSession Session()
    {
        var media = PlaybackMediaProfile.From(
            "/media/episode.mkv",
            3_200_000_000,
            MediaProbeParser.Parse(MediaProbeFixtures.HevcTenBitHdrMultiAudio));
        var plan = PlaybackDecisionEngine.Decide(new(
            media,
            ClientPlaybackCapabilities.FromProfile(PlaybackClientProfiles.AppleWebKit, ClientKinds.Pwa, hls: true),
            PlaybackServerCapabilities.Software()));
        Assert.AreEqual(PlaybackTransport.Hls, plan.Transport);
        return new PlaybackStreamSessionStore(TimeProvider.System).Create(
            "reader",
            Guid.NewGuid(),
            Guid.NewGuid(),
            "/media/episode.mkv",
            1420,
            plan,
            new PlaybackStreamSelections(null, null, false, PlaybackQualityPreset.Auto, PlaybackModePreference.Auto, ClientKinds.Pwa));
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        Assert.IsTrue(condition());
    }

    [GeneratedRegex(@"(?:text\[|format\()""(?<key>playback\.[A-Za-z0-9_.]+)""")]
    private static partial Regex StaticTextKey();

    private sealed class CountingWakeSender : IWakeOnLanPacketSender
    {
        private int sent;

        public int Sent => Volatile.Read(ref sent);

        public Task SendAsync(string normalizedMacAddress, IPEndPoint broadcastEndpoint, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref sent);
            return Task.CompletedTask;
        }
    }
}
