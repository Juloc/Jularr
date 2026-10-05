using System.Net;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.Library;
using Jularr.Web.Features.MediaCore;

namespace Jularr.Tests;

/// <summary>
/// The web player of a Movie or Series episode: it resolves only the canonical Work/WorkEpisode target and renders the shared
/// player stage with the canonical plan, progress and bootstrap routes.
/// </summary>
[TestClass]
public sealed class VideoWatchPageTests
{
    private static async Task<Work> AddTitleAsync(VideoDetailPageTestHost host, WorkMediaType type, string title)
    {
        var work = new Work { MediaType = type, CanonicalTitle = title, Year = 2024 };
        host.Db.Add(work);
        await host.Db.SaveChangesAsync();
        return work;
    }

    /// <summary>Attaches a real (tiny) file the fake probe describes as H.264 + AAC, exactly as an import does.</summary>
    private static async Task AttachFileAsync(VideoDetailPageTestHost host, Work work, WorkEpisode? episode, string name)
    {
        if (!host.Db.LibraryRoots.Any())
        {
            host.Db.LibraryRoots.Add(new LibraryRoot { Name = "Media", Path = host.MediaDirectory });
            await host.Db.SaveChangesAsync();
        }

        var path = Path.Combine(host.MediaDirectory, name);
        await File.WriteAllBytesAsync(path, [1, 2, 3]);
        host.Probe.Returns(path, MediaProbeFixtures.H264Stereo);
        await new CanonicalMediaStorageService(host.Db).AttachVideoAsync(work.Id, episode?.Id, path, host.MediaDirectory, CancellationToken.None);
    }

    [TestMethod]
    public async Task TheCanonicalTargetIsValidatedBeforeAnythingIsRendered()
    {
        await using var host = await VideoDetailPageTestHost.CreateAsync();
        var movie = await AddTitleAsync(host, WorkMediaType.Movie, "Moon Empire");
        var series = await AddTitleAsync(host, WorkMediaType.Series, "Dark Harbor");
        var anime = await AddTitleAsync(host, WorkMediaType.Anime, "Starfall");
        var episode = await new LibraryCanonicalSeed(host.Db).AddEpisodeAsync(series, 1, 1);
        var foreign = await new LibraryCanonicalSeed(host.Db).AddEpisodeAsync(anime, 1, 1);

        foreach (var path in new[]
        {
            $"/Library/Watch/{Guid.NewGuid()}",
            $"/Library/Watch/{anime.Id}/{foreign.Id}",
            $"/Library/Watch/{movie.Id}/{episode.Id}",
            $"/Library/Watch/{series.Id}",
            $"/Library/Watch/{series.Id}/{Guid.NewGuid()}",
            $"/Library/Watch/{series.Id}/{foreign.Id}"
        })
        {
            Assert.AreEqual(HttpStatusCode.NotFound, (await host.GetAsync(path)).Status, path);
        }
    }

    [TestMethod]
    public async Task ATitleWithoutAPlayableFileShowsTheEmptyStateInsteadOfAPlayer()
    {
        await using var host = await VideoDetailPageTestHost.CreateAsync();
        var movie = await AddTitleAsync(host, WorkMediaType.Movie, "Moon Empire");

        var html = await host.GetOkAsync($"/Library/Watch/{movie.Id}");

        StringAssert.Contains(html, "No playable file yet");
        StringAssert.Contains(html, $"href=\"/Library/Movie/{movie.Id}\"");
        Assert.IsFalse(html.Contains("data-episode-player", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task AMoviePlaysThroughTheCanonicalRoutesAndExposesNoFilePath()
    {
        await using var host = await VideoDetailPageTestHost.CreateAsync();
        var seed = new LibraryCanonicalSeed(host.Db);
        var movie = await AddTitleAsync(host, WorkMediaType.Movie, "Moon Empire");
        await AttachFileAsync(host, movie, null, "moon-empire.mp4");
        await seed.SetProgressAsync(VideoDetailPageTestHost.Profile, movie, null, 900_000, 1_440_000, completed: false, DateTime.UtcNow);

        var html = await host.GetOkAsync($"/Library/Watch/{movie.Id}");

        var stage = html[html.IndexOf("<section class=\"player-panel\"", StringComparison.Ordinal)..];
        StringAssert.Contains(stage, "data-episode-player");
        StringAssert.Contains(stage, $"data-video-target-work=\"{movie.Id}\"");
        StringAssert.Contains(stage[..stage.IndexOf('>')], "data-video-target-episode=\"\"", "A Movie targets the Work, not an episode.");
        StringAssert.Contains(stage, "data-playback-plan-url=\"/api/client/v1/video/playback-plan\"");
        StringAssert.Contains(stage, "data-progress-url=\"/api/client/v1/video/progress\"");
        StringAssert.Contains(stage, "data-player-bootstrap-url=\"/api/client/v1/video/player\"");
        StringAssert.Contains(stage, "data-storage-state=\"available\"");
        StringAssert.Contains(stage, "data-resume-seconds=\"900\"");
        StringAssert.Contains(stage, $"/api/client/v1/video/subtitle-tracks/__track__/cues?workId={movie.Id}");
        StringAssert.Contains(stage, "<strong>Moon Empire</strong>");
        StringAssert.Contains(html, "/js/episode-player.js");
        Assert.IsFalse(html.Contains("data-autoplay-toggle", StringComparison.Ordinal), "A Movie has no next item to autoplay.");
        StringAssert.Contains(html, "Finished");
        Assert.IsFalse(html.Contains("data-offline-media-item", StringComparison.Ordinal), "Offline packages are episode-keyed and stay out of the canonical player.");
        Assert.IsFalse(html.Contains(host.MediaDirectory, StringComparison.OrdinalIgnoreCase), "No host path reaches the page.");
        Assert.IsFalse(html.Contains("moon-empire.mp4", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task ASeriesEpisodeTargetsTheWorkEpisodeAndChainsToTheNextPlayableOne()
    {
        await using var host = await VideoDetailPageTestHost.CreateAsync();
        var seed = new LibraryCanonicalSeed(host.Db);
        var series = await AddTitleAsync(host, WorkMediaType.Series, "Dark Harbor");
        var first = await seed.AddEpisodeAsync(series, 1, 1);
        await seed.AddEpisodeAsync(series, 1, 2);
        var third = await seed.AddEpisodeAsync(series, 1, 3);
        await AttachFileAsync(host, series, first, "s01e01.mp4");
        await AttachFileAsync(host, series, third, "s01e03.mp4");

        var html = await host.GetOkAsync($"/Library/Watch/{series.Id}/{first.Id}");

        StringAssert.Contains(html, $"data-video-target-work=\"{series.Id}\"");
        StringAssert.Contains(html, $"data-video-target-episode=\"{first.Id}\"");
        StringAssert.Contains(html, $"data-next-url=\"/Library/Watch/{series.Id}/{third.Id}\"");
        StringAssert.Contains(html, "S01 E01");
        StringAssert.Contains(html, $"href=\"/Library/Series/{series.Id}\"");
    }

    [TestMethod]
    public async Task APlayerPageOpensOnlyForProfilesThatMayBrowseTheMediaType()
    {
        await using var host = await VideoDetailPageTestHost.CreateAsync();
        var movie = await AddTitleAsync(host, WorkMediaType.Movie, "Moon Empire");
        await AttachFileAsync(host, movie, null, "moon-empire.mp4");
        await host.Capabilities.SetRoleDefaultAsync(AccountRole.User, WorkMediaType.Movie, MediaCapability.Hidden);

        Assert.AreEqual(HttpStatusCode.NotFound, (await host.GetAsync($"/Library/Watch/{movie.Id}")).Status);
        Assert.AreEqual(HttpStatusCode.OK, (await host.GetAsync($"/Library/Watch/{movie.Id}", asOwner: true)).Status);
    }
}
