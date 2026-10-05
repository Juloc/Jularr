using System.Net;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.ClientApi;
using Jularr.Web.Features.Instance;
using Jularr.Web.Features.InstantPlay;
using Jularr.Web.Features.Library;
using Jularr.Web.Features.MediaCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Jularr.Tests;

/// <summary>
/// The Playback instance switch (docs/mockups/instant-play, section 11): a manager-only instance keeps discovery, requests and
/// acquisition and has no Jularr player at all - no play action on a page, no Watch page and no player/plan/progress/stream API.
/// </summary>
[TestClass]
public sealed class InstantPlayManagerOnlyTests
{
    private static async Task<Work> AddTitleAsync(VideoDetailPageTestHost host, WorkMediaType type, string title, string tmdbId)
    {
        var work = await new LibraryCanonicalSeed(host.Db).AddWorkAsync(type, title, 2024);
        host.Db.Add(new WorkExternalIdentity { WorkId = work.Id, MediaType = type, Provider = "tmdb", ExternalId = tmdbId, IsPrimary = true });
        await host.Db.SaveChangesAsync();
        return work;
    }

    private static string Hero(string html)
    {
        var from = html.IndexOf("<section class=\"ad-hero", StringComparison.Ordinal);
        return html[from..html.IndexOf("</section>", from, StringComparison.Ordinal)];
    }

    [TestMethod]
    public void PlaybackIsAnInstanceModuleThatDefaultsToEnabledAndIsConfigurableInAdmin()
    {
        Assert.IsTrue(InstanceModuleSettings.Default.IsEnabled(InstanceModule.Playback), "An upgraded instance keeps playing.");
        CollectionAssert.Contains(Jularr.Web.Pages.Admin.InstanceModel.ConfigurableModules.ToArray(), InstanceModule.Playback);
        Assert.IsTrue(ClientApiContract.Capabilities().Features.PlaybackEnabled);

        var managerOnly = ClientApiContract.Capabilities(InstanceModuleSettings.Default.With(InstanceModule.Playback, false)).Features;
        Assert.IsFalse(managerOnly.PlaybackEnabled);
        Assert.IsFalse(managerOnly.PlaybackIntents);
        Assert.IsTrue(managerOnly.Library, "Everything that is not playback stays.");
    }

    [TestMethod]
    public void EveryPlayerRouteBelongsToThePlaybackModuleAndRequestRoutesDoNot()
    {
        foreach (var path in new[]
                 {
                     "/Library/Watch/1", "/api/client/v1/video/player", "/api/client/v1/video/playback-plan", "/api/client/v1/video/progress",
                     "/api/client/v1/video/playback-intents", "/api/client/v1/video/subtitle-tracks/stream:1/cues", "/api/client/v1/stream-sessions/abc/hls"
                 })
        {
            CollectionAssert.AreEquivalent(new[] { InstanceModule.Playback }, InstanceModuleRoutes.Resolve(new Microsoft.AspNetCore.Http.PathString(path)).ToArray(), path);
        }

        Assert.AreEqual(0, InstanceModuleRoutes.Resolve(new Microsoft.AspNetCore.Http.PathString("/api/client/v1/requests/1")).Count);
        Assert.AreEqual(0, InstanceModuleRoutes.Resolve(new Microsoft.AspNetCore.Http.PathString("/Library/Movie/1")).Count);
    }

    [TestMethod]
    public async Task ManagerOnlyDetailPagesShowAvailableAndLinkNowhereIntoThePlayer()
    {
        await using var host = await VideoDetailPageTestHost.CreateAsync();
        var seed = new LibraryCanonicalSeed(host.Db);
        var movie = await AddTitleAsync(host, WorkMediaType.Movie, "Moon Empire", "603");
        var series = await AddTitleAsync(host, WorkMediaType.Series, "Dark Harbor", "1399");
        var episode = await seed.AddEpisodeAsync(series, 1, 1);
        await seed.AddVideoAsync(movie, null);
        await seed.AddVideoAsync(series, episode);

        var playing = await host.GetOkAsync($"/Library/Movie/{movie.Id}");
        StringAssert.Contains(playing, $"/Library/Watch/{movie.Id}");

        await host.Modules.SetAsync(InstanceModule.Playback, false);

        foreach (var path in new[] { $"/Library/Movie/{movie.Id}", $"/Library/Series/{series.Id}" })
        {
            var html = await host.GetOkAsync(path);
            Assert.IsFalse(html.Contains("/Library/Watch", StringComparison.Ordinal), $"{path} must not link into the player.");
            StringAssert.Contains(Hero(html), ">Available<");
            foreach (var playLabel in new[] { ">Play<", "Continue watching", "Start watching", "Watch now", "Watch again" })
            {
                Assert.IsFalse(html.Contains(playLabel, StringComparison.Ordinal), $"{path} must not show '{playLabel}'.");
            }
        }
    }

    [TestMethod]
    public async Task ManagerOnlyMissingMediaIsRequestedAndNeverWatchedNow()
    {
        await using var host = await VideoDetailPageTestHost.CreateAsync();
        await host.MakeAcquisitionReadyAsync();
        var movie = await AddTitleAsync(host, WorkMediaType.Movie, "Moon Empire", "603");

        await host.Modules.SetAsync(InstanceModule.Playback, false);

        StringAssert.Contains(Hero(await host.GetOkAsync($"/Library/Movie/{movie.Id}", asOwner: true)), ">Request<");

        await using var scope = host.Services.CreateAsyncScope();
        var policy = await scope.ServiceProvider.GetRequiredService<InstantPlayPolicyService>().ResolveAsync(WorkMediaType.Movie, CancellationToken.None);
        Assert.IsFalse(policy.PlaybackEnabled);
        Assert.IsFalse(policy.AllowsInstantAcquisition);
    }

    [TestMethod]
    public async Task ManagerOnlyInstancesAnswer404ForTheWatchPageAndEveryPlayerRoute()
    {
        await using var host = await VideoDetailPageTestHost.CreateAsync();
        var movie = await AddTitleAsync(host, WorkMediaType.Movie, "Moon Empire", "603");
        await host.AttachVideoAsync(movie, null, "moon-empire.mp4");
        var target = new { target = new { workId = movie.Id } };

        Assert.AreEqual(HttpStatusCode.OK, (await host.GetAsync($"/Library/Watch/{movie.Id}")).Status);
        Assert.AreEqual(HttpStatusCode.OK, (await host.SendAsync(HttpMethod.Post, "/api/client/v1/video/player", target)).Status);

        await host.Modules.SetAsync(InstanceModule.Playback, false);

        Assert.AreEqual(HttpStatusCode.NotFound, (await host.GetAsync($"/Library/Watch/{movie.Id}")).Status);
        Assert.AreEqual(HttpStatusCode.NotFound, (await host.SendAsync(HttpMethod.Post, "/api/client/v1/video/player", target)).Status);
        Assert.AreEqual(HttpStatusCode.NotFound, (await host.SendAsync(HttpMethod.Post, "/api/client/v1/video/playback-plan", target)).Status);
        Assert.AreEqual(HttpStatusCode.NotFound, (await host.SendAsync(HttpMethod.Put, "/api/client/v1/video/progress", new { target = new { workId = movie.Id }, positionMs = 1000, completed = false })).Status);
        Assert.AreEqual(HttpStatusCode.NotFound, (await host.SendAsync(HttpMethod.Post, "/api/client/v1/video/playback-intents", target)).Status);
        Assert.AreEqual(HttpStatusCode.NotFound, (await host.SendAsync(HttpMethod.Get, $"/api/client/v1/stream-sessions/{Guid.NewGuid()}")).Status);
        Assert.AreEqual(HttpStatusCode.NotFound, (await host.SendAsync(HttpMethod.Delete, $"/api/client/v1/stream-sessions/{Guid.NewGuid()}")).Status);

        await host.Modules.SetAsync(InstanceModule.Playback, true);

        Assert.AreEqual(HttpStatusCode.OK, (await host.GetAsync($"/Library/Watch/{movie.Id}")).Status, "Re-enabling Playback restores the player.");
    }

    [TestMethod]
    public async Task AnOfflineVideoPackageIsRefusedBeforeAnyLookupWhileOtherKindsKeepTheirOwnSwitches()
    {
        var modules = new InstanceModuleStore(Path.Combine(Path.GetTempPath(), $"jularr-offline-{Guid.NewGuid():N}"));
        await modules.SetAsync(InstanceModule.Playback, false);
        var db = new Jularr.Web.Data.AppDbContext(new DbContextOptionsBuilder<Jularr.Web.Data.AppDbContext>().UseNpgsql("Host=localhost;Database=unused").Options);
        await db.DisposeAsync();
        var service = new ClientApiOfflineMediaPackageService(db, null!, modules);

        foreach (var kind in new[] { "episode", "movie", "tv", "series" })
        {
            Assert.IsNull(await service.GetManifestAsync(kind, Guid.NewGuid(), CancellationToken.None), $"{kind} is video playback.");
        }

        // A kind that is not video reaches the database, which this test made unreachable: its own module switch applies, not Playback.
        await Assert.ThrowsAsync<ObjectDisposedException>(() => service.GetManifestAsync("audiobook", Guid.NewGuid(), CancellationToken.None));
    }
}
