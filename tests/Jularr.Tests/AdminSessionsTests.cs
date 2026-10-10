using Jularr.Web.Data;
using Jularr.Web.Features.Admin;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.Library;
using Jularr.Web.Features.Playback.Decision;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Tests;

/// <summary>
/// Admin &gt; Sessions and Profile &gt; Devices (#518): the owner sees and stops every session, a
/// signed-in user only ever sees and stops their own.
/// </summary>
[TestClass]
public sealed class AdminSessionsTests
{
    [TestMethod]
    public void SessionsPageUsesTheStopOthersPolicy()
    {
        var authorize = typeof(Jularr.Web.Pages.Admin.SessionsModel)
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
            .Cast<AuthorizeAttribute>()
            .SingleOrDefault();

        Assert.IsNotNull(authorize, "Admin/Sessions must require authorization.");
        Assert.AreEqual(
            JularrPolicies.SessionsStopOthers,
            authorize.Policy,
            "Admin/Sessions must enforce the policy for stopping another user's session.");
    }

    [TestMethod]
    public void DevicesPageHasNoOwnerRestriction()
    {
        // Profile/Devices is for any signed-in user: it must not carry an Owner-only attribute
        // (the app's default fallback policy already requires sign-in for every page).
        var authorize = typeof(Jularr.Web.Pages.Profile.DevicesModel)
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
            .Cast<AuthorizeAttribute>()
            .SingleOrDefault();

        Assert.IsTrue(
            authorize is null || authorize.Roles != AccountRoles.Owner,
            "Profile/Devices must be reachable by any signed-in user, not just the owner.");
    }

    [TestMethod]
    public async Task DevicesServiceListsOnlyTheRequestedProfilesSessions()
    {
        var path = TempDatabasePath();

        try
        {
            await using var db = await CreateDatabaseAsync(path);
            var anime = new Anime { Key = "sessions-anime", Title = "Sessions Anime" };
            var episode = new Episode { AnimeId = anime.Id, SeasonNumber = 1, Number = 1, Title = "Episode 1" };
            db.AddRange(anime, episode);
            db.OwnerAccounts.AddRange(
                Account("reader-a", "Reader A"),
                Account("reader-b", "Reader B"));
            await db.SaveChangesAsync();

            var store = new PlaybackStreamSessionStore(TimeProvider.System);
            var sessionA = store.Create(
                "reader-a", new PlaybackVideoTarget(1, episode.Id), Guid.NewGuid(), "/media/a.mkv", 1200,
                SamplePlan(), SampleSelections());
            store.Create(
                "reader-b", new PlaybackVideoTarget(1, episode.Id), Guid.NewGuid(), "/media/b.mkv", 1200,
                SamplePlan(), SampleSelections());

            var service = new AdminSessionsService(db, store);

            var rowsForA = await service.ListForProfileAsync("reader-a", CancellationToken.None);
            Assert.AreEqual(1, rowsForA.Count);
            Assert.AreEqual(sessionA.Id, rowsForA[0].SessionId);
            Assert.AreEqual("Reader A", rowsForA[0].ProfileName);

            var allRows = await service.ListAllAsync(CancellationToken.None);
            Assert.AreEqual(2, allRows.Count, "Admin > Sessions must see every profile's session.");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public void AProfileCannotStopAnotherProfilesSession()
    {
        var store = new PlaybackStreamSessionStore(TimeProvider.System);
        var session = store.Create(
            "reader-a", new PlaybackVideoTarget(1, Guid.NewGuid()), Guid.NewGuid(), "/media/a.mkv", 1200,
            SamplePlan(), SampleSelections());

        Assert.IsFalse(
            store.Remove(session.Id, "reader-b"),
            "Stopping a session as a different profile must fail.");
        Assert.IsNotNull(
            store.Get(session.Id, "reader-a"),
            "The session must still be live after a foreign stop attempt.");

        Assert.IsTrue(
            store.Remove(session.Id, "reader-a"),
            "The owning profile can still stop its own session.");
        Assert.IsNull(store.Get(session.Id, "reader-a"));
    }

    [TestMethod]
    public void OwnerCanStopAnyProfilesSessionThroughRemoveAny()
    {
        var store = new PlaybackStreamSessionStore(TimeProvider.System);
        var session = store.Create(
            "reader-a", new PlaybackVideoTarget(1, Guid.NewGuid()), Guid.NewGuid(), "/media/a.mkv", 1200,
            SamplePlan(), SampleSelections());

        Assert.IsTrue(store.RemoveAny(session.Id));
        Assert.IsNull(store.Get(session.Id, "reader-a"));
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

    private static PlaybackStreamSelections SampleSelections() => new(
        AudioStreamIndex: null,
        SubtitleStreamIndex: null,
        BurnInSubtitle: false,
        Quality: PlaybackQualityPreset.Auto,
        ModePreference: PlaybackModePreference.Auto,
        ClientKind: ClientKinds.Web);

    private static string TempDatabasePath() =>
        Path.Combine(
            Path.GetTempPath(),
            $"jularr-admin-sessions-{Guid.NewGuid():N}.db");

    private static async Task<AppDbContext> CreateDatabaseAsync(string path)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={path};Foreign Keys=True")
            .Options;

        var db = new AppDbContext(options);
        await DatabaseMigrationBridge.UpgradeAsync(db);
        return db;
    }
}
