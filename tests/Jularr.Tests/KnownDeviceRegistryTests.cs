using System.Net;
using Jularr.Web.Data;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.Devices;
using Jularr.Web.Features.Playback.Decision;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Tests;

/// <summary>
/// Admin &gt; Devices &amp; security and Profile &gt; Devices' known-devices registry (#527, part
/// of epic #510): the owner sees and revokes every account's devices, a signed-in user only ever
/// sees and revokes their own.
/// </summary>
[TestClass]
public sealed class KnownDeviceRegistryTests
{
    [TestMethod]
    public void DevicesAdminPageUsesTheAdminSystemPolicy()
    {
        var authorize = typeof(Jularr.Web.Pages.Admin.DevicesModel)
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
            .Cast<AuthorizeAttribute>()
            .SingleOrDefault();

        Assert.IsNotNull(authorize, "Admin/Devices must require authorization.");
        Assert.AreEqual(
            JularrPolicies.AdminSystem,
            authorize.Policy,
            "Admin/Devices must be owner-only (admin.system), unlike Admin/Sessions.");
    }

    [TestMethod]
    public async Task TouchCreatesADeviceThenRefreshesItWithoutLosingFirstSeen()
    {
        var path = TempDatabasePath();
        try
        {
            await using var db = await CreateDatabaseAsync(path);
            db.OwnerAccounts.Add(Account("reader-a", "Reader A"));
            await db.SaveChangesAsync();

            var time = new TestTimeProvider(new DateTimeOffset(2026, 9, 28, 10, 0, 0, TimeSpan.Zero));
            var store = new PlaybackStreamSessionStore(TimeProvider.System);
            var registry = new KnownDeviceRegistry(db, store, time);

            await registry.TouchAsync("reader-a", "web", "Chrome", "1.0", "Mozilla/5.0", CancellationToken.None);

            time.Advance(TimeSpan.FromMinutes(10));
            await registry.TouchAsync("reader-a", "web", "Chrome", "1.1", "Mozilla/5.0", CancellationToken.None);

            var devices = await registry.ListForProfileAsync("reader-a", CancellationToken.None);

            Assert.AreEqual(1, devices.Count, "The same profile/kind/label must refresh one row, not create a second.");
            var device = devices[0];
            Assert.AreEqual("Chrome", device.Label);
            Assert.AreEqual("1.1", device.AppVersion, "The latest app version must win.");
            Assert.AreEqual(new DateTime(2026, 9, 28, 10, 0, 0, DateTimeKind.Utc), device.FirstSeenUtc);
            Assert.AreEqual(new DateTime(2026, 9, 28, 10, 10, 0, DateTimeKind.Utc), device.LastSeenUtc);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public async Task DifferentClientKindsOrLabelsProduceDifferentDevices()
    {
        var path = TempDatabasePath();
        try
        {
            await using var db = await CreateDatabaseAsync(path);
            db.OwnerAccounts.Add(Account("reader-a", "Reader A"));
            await db.SaveChangesAsync();

            var registry = new KnownDeviceRegistry(db, new PlaybackStreamSessionStore(TimeProvider.System), TimeProvider.System);

            await registry.TouchAsync("reader-a", "web", "Chrome", null, null, CancellationToken.None);
            await registry.TouchAsync("reader-a", "android_tv", "Shield TV", null, null, CancellationToken.None);
            await registry.TouchAsync("reader-a", "web", "Firefox", null, null, CancellationToken.None);

            var devices = await registry.ListForProfileAsync("reader-a", CancellationToken.None);

            Assert.AreEqual(3, devices.Count);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public async Task ListAllJoinsAccountNamesAndMarksALiveDeviceOnlineWithItsPlaybackMethod()
    {
        var path = TempDatabasePath();
        try
        {
            await using var db = await CreateDatabaseAsync(path);
            db.OwnerAccounts.AddRange(Account("reader-a", "Reader A"), Account("reader-b", "Reader B"));
            await db.SaveChangesAsync();

            var store = new PlaybackStreamSessionStore(TimeProvider.System);
            var registry = new KnownDeviceRegistry(db, store, TimeProvider.System);

            await registry.TouchAsync("reader-a", "web", "Chrome", null, null, CancellationToken.None);
            await registry.TouchAsync("reader-b", "android", "Phone", null, null, CancellationToken.None);

            store.Create(
                "reader-a", Guid.NewGuid(), Guid.NewGuid(), "/media/a.mkv", 1200,
                SamplePlan(), SampleSelections("web"));

            var all = await registry.ListAllAsync(CancellationToken.None);

            Assert.AreEqual(2, all.Count, "Admin > Devices must see every account's devices.");
            var deviceA = all.Single(x => x.ProfileId == "reader-a");
            Assert.AreEqual("Reader A", deviceA.ProfileName);
            Assert.IsTrue(deviceA.IsOnline);
            Assert.AreEqual("playback.mode.direct_play", deviceA.LivePlaybackMethodKey);

            var deviceB = all.Single(x => x.ProfileId == "reader-b");
            Assert.AreEqual("Reader B", deviceB.ProfileName);
            Assert.IsNull(deviceB.LivePlaybackMethodKey);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public async Task OwnerCanRevokeAnyProfilesDeviceAndItEndsTheLiveSession()
    {
        var path = TempDatabasePath();
        try
        {
            await using var db = await CreateDatabaseAsync(path);
            db.OwnerAccounts.Add(Account("reader-a", "Reader A"));
            await db.SaveChangesAsync();

            var store = new PlaybackStreamSessionStore(TimeProvider.System);
            var registry = new KnownDeviceRegistry(db, store, TimeProvider.System);
            await registry.TouchAsync("reader-a", "web", "Chrome", null, null, CancellationToken.None);
            var session = store.Create(
                "reader-a", Guid.NewGuid(), Guid.NewGuid(), "/media/a.mkv", 1200,
                SamplePlan(), SampleSelections("web"));

            var deviceId = (await registry.ListForProfileAsync("reader-a", CancellationToken.None)).Single().Id;

            var revoked = await registry.RevokeAsync(deviceId, requesterProfileId: null, CancellationToken.None);

            Assert.IsTrue(revoked, "An owner-context revoke (null requester) must succeed for any device.");
            Assert.IsNull(store.Get(session.Id, "reader-a"), "Revoking a device must end its live session.");
            Assert.AreEqual(0, (await registry.ListForProfileAsync("reader-a", CancellationToken.None)).Count);

            Assert.IsFalse(
                await registry.RevokeAsync(deviceId, requesterProfileId: null, CancellationToken.None),
                "Revoking an already-gone device must fail, not throw.");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public async Task AProfileCannotRevokeAnotherProfilesDevice()
    {
        var path = TempDatabasePath();
        try
        {
            await using var db = await CreateDatabaseAsync(path);
            db.OwnerAccounts.AddRange(Account("reader-a", "Reader A"), Account("reader-b", "Reader B"));
            await db.SaveChangesAsync();

            var store = new PlaybackStreamSessionStore(TimeProvider.System);
            var registry = new KnownDeviceRegistry(db, store, TimeProvider.System);
            await registry.TouchAsync("reader-a", "web", "Chrome", null, null, CancellationToken.None);
            var session = store.Create(
                "reader-a", Guid.NewGuid(), Guid.NewGuid(), "/media/a.mkv", 1200,
                SamplePlan(), SampleSelections("web"));

            var deviceId = (await registry.ListForProfileAsync("reader-a", CancellationToken.None)).Single().Id;

            // Profile > Devices passes the signed-in profile as the requester: reader-b must
            // never be able to revoke reader-a's device this way.
            var revoked = await registry.RevokeAsync(deviceId, requesterProfileId: "reader-b", CancellationToken.None);

            Assert.IsFalse(revoked, "A profile must never revoke another profile's device.");
            Assert.IsNotNull(store.Get(session.Id, "reader-a"), "The foreign revoke attempt must not end the live session.");
            Assert.AreEqual(1, (await registry.ListForProfileAsync("reader-a", CancellationToken.None)).Count);

            // The owning profile can still revoke its own device.
            Assert.IsTrue(await registry.RevokeAsync(deviceId, requesterProfileId: "reader-a", CancellationToken.None));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public async Task OpeningAPlaybackPlanRegistersTheClientAsAKnownDevice()
    {
        const string chromeAgent =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36";

        await using var fixture = await MediaInventoryFixture.CreateAsync();
        var media = await fixture.AddMediaAsync("episode.mkv", new byte[4096]);
        fixture.Runner.Returns(media.Path, MediaProbeFixtures.HevcTenBitHdrMultiAudio);
        fixture.Db.OwnerAccounts.Add(Account("reader", "Reader"));
        await fixture.Db.SaveChangesAsync();

        var store = new PlaybackStreamSessionStore(TimeProvider.System);
        var registry = new KnownDeviceRegistry(fixture.Db, store, TimeProvider.System);
        var service = new PlaybackPlanService(
            fixture.Db,
            fixture.Inventory,
            store,
            new PlaybackServerCapabilityProvider(new PlaybackTranscodeSlots()),
            mediaAvailability: null,
            deviceRegistry: registry);
        var input = new PlaybackPlanInput(null, ClientKinds.Web, chromeAgent, IPAddress.Parse("192.168.1.2"));

        var outcome = await service.PlanAsync(media.EpisodeId!.Value, "reader", input, CancellationToken.None);

        Assert.IsNotNull(outcome!.Session, "The plan must open a session for this to be a real playback attempt.");
        var devices = await registry.ListForProfileAsync("reader", CancellationToken.None);
        Assert.AreEqual(1, devices.Count, "Opening a playback plan must register (or refresh) a known device.");
        Assert.AreEqual(ClientKinds.Web, devices[0].ClientKind);
    }

    [TestMethod]
    public void DeviceKeyIsStableAndDistinguishesProfileKindAndLabel()
    {
        var first = DeviceKey.Compute("reader-a", "web", "Chrome");
        var again = DeviceKey.Compute("reader-a", "web", "Chrome");
        var differentProfile = DeviceKey.Compute("reader-b", "web", "Chrome");
        var differentKind = DeviceKey.Compute("reader-a", "android", "Chrome");
        var differentLabel = DeviceKey.Compute("reader-a", "web", "Firefox");

        Assert.AreEqual(first, again);
        Assert.AreNotEqual(first, differentProfile);
        Assert.AreNotEqual(first, differentKind);
        Assert.AreNotEqual(first, differentLabel);
    }

    private static OwnerAccount Account(string id, string userName) => new()
    {
        Id = id,
        UserName = userName,
        NormalizedUserName = userName.ToUpperInvariant(),
        PasswordHash = "hash",
        Role = AccountRole.User,
        IsEnabled = true,
        CreatedAt = DateTime.UtcNow
    };

    private static PlaybackPlan SamplePlan() => new(
        PlaybackDeliveryMode.DirectPlay,
        PlaybackTransport.File,
        "mp4",
        Video: null,
        Audio: null,
        Quality: new PlaybackQualityResolution(
            PlaybackQualityPreset.Auto,
            PlaybackNetworkClass.Local,
            null,
            PlaybackLimitSource.None,
            8000,
            8000),
        Reasons: [],
        Confidence: PlaybackCapabilitySupport.Confirmed,
        SourceContainer: "mkv");

    private static PlaybackStreamSelections SampleSelections(string clientKind) => new(
        AudioStreamIndex: null,
        SubtitleStreamIndex: null,
        BurnInSubtitle: false,
        Quality: PlaybackQualityPreset.Auto,
        ModePreference: PlaybackModePreference.Auto,
        ClientKind: clientKind);

    private static string TempDatabasePath() =>
        Path.Combine(
            Path.GetTempPath(),
            $"jularr-known-devices-{Guid.NewGuid():N}.db");

    private static async Task<AppDbContext> CreateDatabaseAsync(string path)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={path};Foreign Keys=True")
            .Options;

        var db = new AppDbContext(options);
        await DatabaseMigrationBridge.UpgradeAsync(db);
        return db;
    }

    private sealed class TestTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset current = now;

        public override DateTimeOffset GetUtcNow() => current;

        public void Advance(TimeSpan value) => current += value;
    }
}
