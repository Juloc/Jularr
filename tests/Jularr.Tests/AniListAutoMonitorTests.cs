using System.Net;
using System.Text;
using Jularr.Web.Features.Acquisition.Monitoring;
using Jularr.Web.Features.Acquisition.Ownership;
using Jularr.Web.Features.Tracking;

namespace Jularr.Tests;

/// <summary>
/// P1 item 7: auto-monitor anime on the profile's AniList Current/Planning lists that already
/// exist locally. Never adds a non-local anime and never touches a Sonarr-owned one.
/// </summary>
[TestClass]
public sealed class AniListAutoMonitorTests
{
    private const string ProfileId = "owner";
    private const int AniListMediaId = 154587; // Matches AnimeAcquisitionEnvironment.SeedFrierenAsync's mapping.

    [TestMethod]
    public async Task EnablingAutoMonitorMonitorsALocalAnimeOnTheCurrentList()
    {
        await using var environment = await AnimeAcquisitionEnvironment.CreateAsync();
        await environment.SeedFrierenAsync();
        await SetMonitoredAsync(environment, false);
        await ConnectAniListAsync(environment, "CURRENT");
        await environment.AniListAutoMonitorSettings.SetEnabledAsync(ProfileId, true, DateTimeOffset.UtcNow, CancellationToken.None);

        var result = await environment.RunAniListAutoMonitorAsync(ProfileId);

        Assert.AreEqual(1, result.NewlyMonitored, string.Join(" ", result.Notes));
        Assert.IsTrue((await environment.AnimeMonitoringAsync()).IsWorkMonitored);
    }

    [TestMethod]
    public async Task DisabledAutoMonitorNeverTouchesMonitoringState()
    {
        await using var environment = await AnimeAcquisitionEnvironment.CreateAsync();
        await environment.SeedFrierenAsync();
        await SetMonitoredAsync(environment, false);
        await ConnectAniListAsync(environment, "CURRENT");
        // Auto-monitor left off (the default).

        var result = await environment.RunAniListAutoMonitorAsync(ProfileId);

        Assert.AreEqual(0, result.NewlyMonitored);
        Assert.IsFalse((await environment.AnimeMonitoringAsync()).IsWorkMonitored);
    }

    [TestMethod]
    public async Task PlanningListAlsoQualifiesButCompletedDoesNot()
    {
        await using var environment = await AnimeAcquisitionEnvironment.CreateAsync();
        await environment.SeedFrierenAsync();
        await SetMonitoredAsync(environment, false);
        await ConnectAniListAsync(environment, "COMPLETED");
        await environment.AniListAutoMonitorSettings.SetEnabledAsync(ProfileId, true, DateTimeOffset.UtcNow, CancellationToken.None);

        var completed = await environment.RunAniListAutoMonitorAsync(ProfileId);
        Assert.AreEqual(0, completed.NewlyMonitored, "COMPLETED is neither Current nor Planning.");

        environment.AniListHandler = new FakeAniListLibraryHandler(LibraryJson("PLANNING"));
        var planning = await environment.RunAniListAutoMonitorAsync(ProfileId);
        Assert.AreEqual(1, planning.NewlyMonitored);
    }

    [TestMethod]
    public async Task NeverEnablesMonitoringForAnAnimeSonarrOwnsReadOnly()
    {
        await using var environment = await AnimeAcquisitionEnvironment.CreateAsync();
        await environment.SeedFrierenAsync(mode: null); // null seeds no explicit mode assignment -> defaults to read-only coexistence.
        await SetMonitoredAsync(environment, false);
        await ConnectAniListAsync(environment, "CURRENT");
        await environment.AniListAutoMonitorSettings.SetEnabledAsync(ProfileId, true, DateTimeOffset.UtcNow, CancellationToken.None);

        var result = await environment.RunAniListAutoMonitorAsync(ProfileId);

        Assert.AreEqual(0, result.NewlyMonitored, "Read-only Sonarr coexistence is never touched, even when opted in.");
        Assert.IsFalse((await environment.AnimeMonitoringAsync()).IsWorkMonitored);
    }

    [TestMethod]
    public async Task AlreadyMonitoredAnimeIsNotCountedAsNewlyMonitored()
    {
        await using var environment = await AnimeAcquisitionEnvironment.CreateAsync();
        await environment.SeedFrierenAsync(); // Already monitored by SeedFrierenAsync.
        await ConnectAniListAsync(environment, "CURRENT");
        await environment.AniListAutoMonitorSettings.SetEnabledAsync(ProfileId, true, DateTimeOffset.UtcNow, CancellationToken.None);

        var result = await environment.RunAniListAutoMonitorAsync(ProfileId);

        Assert.AreEqual(0, result.NewlyMonitored);
    }

    private static async Task SetMonitoredAsync(AnimeAcquisitionEnvironment environment, bool monitored) =>
        await environment.Scheduler.RunExclusiveAsync(
            (pipeline, token) => pipeline.UpdateAnimeSettingsAsync(environment.AnimeId, monitored, false, null, [], token),
            CancellationToken.None);

    private static async Task ConnectAniListAsync(AnimeAcquisitionEnvironment environment, string listStatus)
    {
        environment.AniListHandler = new FakeAniListLibraryHandler(LibraryJson(listStatus));
        await environment.AniListAccounts.SaveAsync(
            ProfileId,
            new StoredAniListAccount(
                12345,
                999,
                "viewer",
                null,
                "token-0123456789",
                DateTimeOffset.UtcNow,
                null),
            CancellationToken.None);
    }

    private static string LibraryJson(string listStatus) =>
        "{\"data\":{\"MediaListCollection\":{\"lists\":[{\"status\":\"" + listStatus + "\",\"entries\":[" +
        "{\"id\":1,\"status\":\"" + listStatus + "\",\"progress\":0,\"repeat\":0,\"updatedAt\":1," +
        "\"media\":{\"id\":" + AniListMediaId + ",\"type\":\"ANIME\",\"format\":\"TV\"," +
        "\"title\":{\"romaji\":\"Sousou no Frieren\",\"english\":\"Frieren\",\"native\":null}," +
        "\"coverImage\":{\"extraLarge\":null,\"large\":null},\"status\":\"RELEASING\"," +
        "\"episodes\":28,\"chapters\":null,\"volumes\":null,\"seasonYear\":2023," +
        "\"startDate\":{\"year\":2023},\"genres\":[],\"isAdult\":false}}" +
        "]}]}}}";

    private sealed class FakeAniListLibraryHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
    }
}
