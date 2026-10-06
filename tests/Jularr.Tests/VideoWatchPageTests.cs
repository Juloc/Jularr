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
    public async Task TheEmptyStateNamesWhatIsMissingForAMovieAndForAnEpisode()
    {
        await using var host = await VideoDetailPageTestHost.CreateAsync();
        var movie = await AddTitleAsync(host, WorkMediaType.Movie, "Moon Empire");
        var series = await AddTitleAsync(host, WorkMediaType.Series, "Dark Harbor");
        var episode = await new LibraryCanonicalSeed(host.Db).AddEpisodeAsync(series, 1, 1);

        var movieHtml = await host.GetOkAsync($"/Library/Watch/{movie.Id}");
        var episodeHtml = await host.GetOkAsync($"/Library/Watch/{series.Id}/{episode.Id}");

        StringAssert.Contains(movieHtml, "No video file has been found for this title yet.");
        StringAssert.Contains(episodeHtml, "This episode is in the library but no video file has been found for it yet.");
        StringAssert.Contains(episodeHtml, $"href=\"/Library/Series/{series.Id}\"");
    }

    // A media tool that could not run says nothing about the file: the viewer is not told there is no file.
    [TestMethod]
    public async Task AFileTheMediaToolCouldNotCheckIsNotReportedAsMissingAndMayBeTriedAgain()
    {
        await using var host = await VideoDetailPageTestHost.CreateAsync();
        var movie = await AddTitleAsync(host, WorkMediaType.Movie, "Moon Empire");
        var series = await AddTitleAsync(host, WorkMediaType.Series, "Dark Harbor");
        var episode = await new LibraryCanonicalSeed(host.Db).AddEpisodeAsync(series, 1, 1);
        var unavailable = new MediaProbeRun(MediaProbeRunStatus.Unavailable, Error: "ffprobe could not be started or timed out.");
        await host.AttachVideoAsync(movie, null, "moon-empire.mp4", unavailable);
        await host.AttachVideoAsync(series, episode, "s01e01.mp4", unavailable);

        var movieHtml = await host.GetOkAsync($"/Library/Watch/{movie.Id}");
        var episodeHtml = await host.GetOkAsync($"/Library/Watch/{series.Id}/{episode.Id}");

        StringAssert.Contains(movieHtml, "Couldn't prepare this movie");
        StringAssert.Contains(episodeHtml, "Couldn't prepare this episode");
        foreach (var html in new[] { movieHtml, episodeHtml })
        {
            StringAssert.Contains(html, "Jularr couldn't check this file yet.");
            StringAssert.Contains(html, ">Try again</a>");
            Assert.IsFalse(html.Contains("No playable file yet", StringComparison.Ordinal), "A tool failure is not an absent file.");
            Assert.IsFalse(html.Contains("ffprobe", StringComparison.OrdinalIgnoreCase), "The diagnosis is for Admin, not for the viewer.");
            Assert.IsFalse(html.Contains("data-episode-player", StringComparison.Ordinal));
        }
    }

    [TestMethod]
    public async Task AFileTheMediaToolRejectedHasNoRetryAndSaysItMayBeDamaged()
    {
        await using var host = await VideoDetailPageTestHost.CreateAsync();
        var movie = await AddTitleAsync(host, WorkMediaType.Movie, "Moon Empire");
        await host.AttachVideoAsync(movie, null, "moon-empire.mp4", new MediaProbeRun(MediaProbeRunStatus.Failed, Error: "Invalid data found when processing input"));

        var html = await host.GetOkAsync($"/Library/Watch/{movie.Id}");

        StringAssert.Contains(html, "Couldn't prepare this movie");
        StringAssert.Contains(html, "may be damaged");
        Assert.IsFalse(html.Contains(">Try again</a>", StringComparison.Ordinal));
        Assert.IsFalse(html.Contains("Invalid data", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task TheWatchRouteOwnsTheWholeViewportAndLeadsBackThroughTheTopChrome()
    {
        await using var host = await VideoDetailPageTestHost.CreateAsync();
        var movie = await AddTitleAsync(host, WorkMediaType.Movie, "Moon Empire");
        var empty = await AddTitleAsync(host, WorkMediaType.Movie, "Empty Reel");
        await host.AttachVideoAsync(movie, null, "moon-empire.mp4");

        foreach (var work in new[] { movie, empty })
        {
            var html = await host.GetOkAsync($"/Library/Watch/{work.Id}");

            StringAssert.Contains(html, "data-player-frame");
            StringAssert.Contains(html, $"<a class=\"player-back\" href=\"/Library/Movie/{work.Id}\" data-context-back");
            Assert.IsFalse(html.Contains("class=\"back-link\"", StringComparison.Ordinal), "The way back is the arrow in the player chrome.");
        }
    }

    [TestMethod]
    public async Task AMoviePlaysThroughTheCanonicalRoutesAndExposesNoFilePath()
    {
        await using var host = await VideoDetailPageTestHost.CreateAsync();
        var seed = new LibraryCanonicalSeed(host.Db);
        var movie = await AddTitleAsync(host, WorkMediaType.Movie, "Moon Empire");
        await host.AttachVideoAsync(movie, null, "moon-empire.mp4");
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
    public async Task TheWatchPlayerDeclaresTheAsymmetricSeekStepsAndKeepsTheVideoInlineWithoutNativeControls()
    {
        await using var host = await VideoDetailPageTestHost.CreateAsync();
        var movie = await AddTitleAsync(host, WorkMediaType.Movie, "Moon Empire");
        await host.AttachVideoAsync(movie, null, "moon-empire.mp4");

        var html = await host.GetOkAsync($"/Library/Watch/{movie.Id}");

        var root = html[html.IndexOf("<section class=\"player-panel\"", StringComparison.Ordinal)..];
        StringAssert.Contains(root[..root.IndexOf('>')], "data-seek-back-seconds=\"10\"");
        StringAssert.Contains(root[..root.IndexOf('>')], "data-seek-forward-seconds=\"30\"");
        StringAssert.Contains(html, "aria-label=\"Back 10 seconds\"");
        StringAssert.Contains(html, "aria-label=\"Forward 30 seconds\"");
        var video = System.Text.RegularExpressions.Regex.Match(html, "<video[^>]*>").Value;
        StringAssert.Contains(video, "playsinline");
        Assert.IsFalse(video.Contains("controls", StringComparison.Ordinal), video);
        StringAssert.Contains(html, "/js/player-presentation.js");
        StringAssert.Contains(html, "data-chrome-system-player-group hidden", "The system player action starts hidden until the device proves it can offer it.");
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
        await host.AttachVideoAsync(series, first, "s01e01.mp4");
        await host.AttachVideoAsync(series, third, "s01e03.mp4");

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
        await host.AttachVideoAsync(movie, null, "moon-empire.mp4");
        await host.Capabilities.SetRoleDefaultAsync(AccountRole.User, WorkMediaType.Movie, MediaCapability.Hidden);

        Assert.AreEqual(HttpStatusCode.NotFound, (await host.GetAsync($"/Library/Watch/{movie.Id}")).Status);
        Assert.AreEqual(HttpStatusCode.OK, (await host.GetAsync($"/Library/Watch/{movie.Id}", asOwner: true)).Status);
    }
}
