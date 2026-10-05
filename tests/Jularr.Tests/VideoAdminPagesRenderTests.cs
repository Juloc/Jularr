using System.Net;
using System.Text.RegularExpressions;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Instance;

namespace Jularr.Tests;

/// <summary>Movie and TV on the shared Admin pages (#814): Wanted, Requests and the Admin media page, rendered and posted end to end.</summary>
[TestClass]
public sealed class VideoAdminPagesRenderTests
{
    private const string DuneRelease = "Dune.2021.1080p.WEB-DL.x264-GROUP";
    private const string SeveranceFirst = "Severance.S01E01.1080p.WEB-DL.x264-GROUP";
    private const string SeveranceSecond = "Severance.S01E02.1080p.WEB-DL.x264-GROUP";

    [TestMethod]
    public async Task WantedListsMovieAndSeriesRowsWithCanonicalLinksAndTheSharedActionsOnly()
    {
        await using var movie = await VideoAcquisitionTestHost.CreateAsync(MediaAcquisitionKind.Movie, "Dune", 2021, "438631", DuneRelease);
        await movie.CreateApprovedAsync();
        await using var movieHost = await VideoAdminPageHost.CreateAsync(movie);

        var html = await movieHost.GetHtmlAsync("/Admin/Wanted");

        StringAssert.Contains(html, "Dune");
        StringAssert.Contains(html, "admwant-tag-movie");
        StringAssert.Contains(html, "admwant-state-requested");
        StringAssert.Contains(html, $"href=\"/Library/Movie/{movie.Work.Id:D}\"");
        StringAssert.Contains(html, $"href=\"/Admin/Media/movie/{movie.Work.Id:D}\"");
        Assert.AreEqual(1, Regex.Matches(html, @">\s*Search now\s*</button>").Count);
        Assert.AreEqual(0, Regex.Matches(html, @">\s*Retry\s*</button>").Count);
        Assert.IsFalse(html.Contains("handler=SearchAnime", StringComparison.Ordinal), "A Movie has no anime search.");
        Assert.IsFalse(html.Contains("BookManualSearch", StringComparison.Ordinal), "Manual search is offered only where it exists.");

        await using var series = await VideoAcquisitionTestHost.CreateAsync(MediaAcquisitionKind.Tv, "Severance", 2022, "95396", SeveranceFirst, SeveranceSecond);
        await series.AddEpisodeAsync(1, 1);
        await series.AddEpisodeAsync(1, 2);
        await series.AddEpisodeAsync(2, 1);
        await series.CreateApprovedAsync();
        await using var seriesHost = await VideoAdminPageHost.CreateAsync(series);

        var seriesHtml = await seriesHost.GetHtmlAsync("/Admin/Wanted?type=tv");

        StringAssert.Contains(seriesHtml, "Episodes S01E01-02");
        StringAssert.Contains(seriesHtml, "Episodes S02E01");
        StringAssert.Contains(seriesHtml, $"href=\"/Library/Series/{series.Work.Id:D}\"");
        StringAssert.Contains(seriesHtml, $"href=\"/Admin/Media/series/{series.Work.Id:D}\"");
        Assert.AreEqual(2, Regex.Matches(seriesHtml, @"admwant-row").Count, "One row per season that still misses episodes.");
    }

    [TestMethod]
    public async Task RequestsOpenTheCanonicalPageAndSearchMovieAndSeriesThroughTheSharedAction()
    {
        await using var movie = await VideoAcquisitionTestHost.CreateAsync(MediaAcquisitionKind.Movie, "Dune", 2021, "438631", DuneRelease);
        var request = await movie.CreateApprovedAsync();
        await movie.Requests.UpdateStatusAsync(request.Id, AcquisitionRequestStatus.Approved, "Waiting.", null, "/Search?q=Dune", null, CancellationToken.None);
        await using var host = await VideoAdminPageHost.CreateAsync(movie);

        var html = await host.GetHtmlAsync("/Admin/Requests");

        StringAssert.Contains(html, $"href=\"/Library/Movie/{movie.Work.Id:D}\"");
        StringAssert.Contains(html, $"href=\"/Admin/Media/movie/{movie.Work.Id:D}\"");
        Assert.AreEqual(1, Regex.Matches(html, @">\s*Search now\s*</button>").Count);
        Assert.IsFalse(html.Contains("/Search?q=", StringComparison.Ordinal), "View media never goes to a search.");
        StringAssert.Contains(html, "href=\"/Admin/Wanted\"");
    }

    [TestMethod]
    public async Task ApprovingAndRetryingARequestRunsTheSharedExecutorOnceAndRejectsUnknownOrUnprotectedPosts()
    {
        await using var movie = await VideoAcquisitionTestHost.CreateAsync(MediaAcquisitionKind.Movie, "Dune", 2021, "438631", DuneRelease);
        var failed = await movie.Requests.CreateAsync(
            new AcquisitionRequestDraft(MediaAcquisitionKind.Movie, "tmdb", "438631", "Dune", null, null),
            "someone",
            AcquisitionRequestStatus.Failed,
            "owner",
            CancellationToken.None);
        await using var host = await VideoAdminPageHost.CreateAsync(movie);
        var path = $"/Admin/Requests?handler=Approve&id={failed.Id}";

        Assert.AreEqual(HttpStatusCode.BadRequest, await host.PostAsync("/Admin/Requests", path, [], withToken: false), "Anti-forgery is enforced.");
        Assert.AreEqual(HttpStatusCode.Forbidden, await host.PostAsync("/Admin/Requests", path, [], asOwner: false, withToken: false));
        Assert.AreEqual(0, movie.Environment.Client.Grabs.Count);

        Assert.AreEqual(HttpStatusCode.Found, await host.PostAsync("/Admin/Requests", path, []));
        Assert.AreEqual(AcquisitionRequestStatus.Downloading, (await movie.GetAsync(failed.Id)).Status);
        Assert.AreEqual(HttpStatusCode.Found, await host.PostAsync("/Admin/Requests", path, []));
        Assert.AreEqual(1, movie.Environment.Client.Grabs.Count, "Retrying twice starts one download.");

        Assert.AreEqual(HttpStatusCode.NotFound, await host.PostAsync("/Admin/Requests", $"/Admin/Requests?handler=Approve&id={Guid.NewGuid()}", []));
    }

    [TestMethod]
    public async Task TheMovieMediaPageShowsMonitoringAndFilesAndSavesMonitoringAndTheProfile()
    {
        await using var movie = await VideoAcquisitionTestHost.CreateAsync(MediaAcquisitionKind.Movie, "Dune", 2021, "438631", DuneRelease);
        await using var host = await VideoAdminPageHost.CreateAsync(movie);
        var page = $"/Admin/Media/movie/{movie.Work.Id:D}";

        var html = await host.GetHtmlAsync(page);

        StringAssert.Contains(html, "Dune");
        StringAssert.Contains(html, "aria-pressed=\"false\"");
        StringAssert.Contains(html, "Quality profile");
        StringAssert.Contains(html, $"href=\"/Library/Movie/{movie.Work.Id:D}\"");
        Assert.IsFalse(html.Contains("data-admin-media-groups", StringComparison.Ordinal), "A Movie has no seasons.");

        Assert.AreEqual(HttpStatusCode.Found, await host.PostAsync(page, $"{page}?handler=MovieMonitor", [new("monitored", "true")]));
        var monitored = await host.GetHtmlAsync(page);
        StringAssert.Contains(monitored, "aria-pressed=\"true\"");

        var profiles = (await movie.Get<Jularr.Web.Features.Acquisition.Quality.QualityProfileStore>().LoadAsync(CancellationToken.None)).Profiles;
        Assert.AreEqual(HttpStatusCode.Found, await host.PostAsync(page, $"{page}?handler=Profile", [new("profileId", profiles[0].Id)]));
        StringAssert.Contains(await host.GetHtmlAsync(page), $"value=\"{profiles[0].Id}\" selected");

        Assert.AreEqual(HttpStatusCode.NotFound, await host.GetStatusAsync($"/Admin/Media/movie/{Guid.NewGuid():D}"));
        Assert.AreEqual(HttpStatusCode.NotFound, await host.GetStatusAsync($"/Admin/Media/series/{movie.Work.Id:D}"), "The Work is a Movie, not a Series.");
        Assert.AreEqual(HttpStatusCode.NotFound, await host.GetStatusAsync($"/Admin/Media/anime/{movie.Work.Id:D}"));
    }

    [TestMethod]
    public async Task AMovieInTheLibraryListsItsFilesAndMonitoringItHasNothingToAcquire()
    {
        await using var movie = await VideoAcquisitionTestHost.CreateAsync(MediaAcquisitionKind.Movie, "Dune", 2021, "438631", DuneRelease);
        await movie.AttachFileAsync(null, "Dune.2021.1080p.mkv", 2048);
        await using var host = await VideoAdminPageHost.CreateAsync(movie);
        var page = $"/Admin/Media/movie/{movie.Work.Id:D}";

        StringAssert.Contains(await host.GetHtmlAsync(page), "Dune.2021.1080p.mkv");
        Assert.AreEqual(HttpStatusCode.Found, await host.PostAsync(page, $"{page}?handler=MovieMonitor", [new("monitored", "true")]));

        Assert.AreEqual(0, (await movie.Requests.ListAllAsync(10, CancellationToken.None)).Count, "Nothing is requested for a movie that is there.");
        StringAssert.Contains(await host.GetHtmlAsync(page), "aria-pressed=\"false\"");
    }

    [TestMethod]
    public async Task TheSeriesMediaPageListsSeasonsAndEpisodesAndSavesAScopeThatTheWantedPassFollows()
    {
        await using var series = await VideoAcquisitionTestHost.CreateAsync(MediaAcquisitionKind.Tv, "Severance", 2022, "95396", SeveranceFirst, SeveranceSecond, addEpisode: true, addSecondEpisode: true);
        await series.AddEpisodeAsync(2, 1, DateTime.UtcNow.AddDays(5));
        await series.AttachFileAsync(series.EpisodeId);
        var request = await series.CreateApprovedAsync();
        await using var host = await VideoAdminPageHost.CreateAsync(series);
        var page = $"/Admin/Media/series/{series.Work.Id:D}";

        var html = await host.GetHtmlAsync(page);

        StringAssert.Contains(html, "data-admin-media-groups");
        StringAssert.Contains(html, "Season 1");
        StringAssert.Contains(html, "Season 2");
        StringAssert.Contains(html, "Upcoming");
        StringAssert.Contains(html, "All episodes");
        StringAssert.Contains(html, "Future only");
        StringAssert.Contains(html, "Save selection");
        Assert.AreEqual(3, Regex.Matches(html, @"name=""episodeIds""").Count);
        Assert.AreEqual(3, Regex.Matches(html, @"data-episode-monitor checked=""checked""").Count, "Every episode of an All request is monitored.");

        Assert.AreEqual(HttpStatusCode.Found, await host.PostAsync(page, $"{page}?handler=SeriesScope", [new("scope", "custom"), new("episodeIds", series.SecondEpisodeId!.Value.ToString())]));
        var payload = VideoRequestPayload.Parse((await series.GetAsync(request.Id)).PayloadJson)!;
        Assert.AreEqual(VideoRequestScope.Custom, payload.Scope);
        CollectionAssert.AreEqual(new[] { series.SecondEpisodeId!.Value }, payload.SelectedEpisodeIds);
        StringAssert.Contains(await host.GetHtmlAsync(page), "Selection");

        var before = (await series.GetAsync(request.Id)).PayloadJson;
        Assert.AreEqual(HttpStatusCode.Found, await host.PostAsync(page, $"{page}?handler=SeriesScope", [new("scope", "custom"), new("episodeIds", Guid.NewGuid().ToString())]));
        Assert.AreEqual(before, (await series.GetAsync(request.Id)).PayloadJson, "A selection that is not part of the Series is rejected.");

        Assert.AreEqual(HttpStatusCode.NotFound, await host.PostAsync(page, $"/Admin/Media/series/{Guid.NewGuid():D}?handler=SeriesScope", [new("scope", "all")]));
    }

    [TestMethod]
    public async Task OnlyMediaManagersMayOpenOrChangeTheMediaPage()
    {
        await using var movie = await VideoAcquisitionTestHost.CreateAsync(MediaAcquisitionKind.Movie, "Dune", 2021, "438631", DuneRelease);
        await using var host = await VideoAdminPageHost.CreateAsync(movie);
        var page = $"/Admin/Media/movie/{movie.Work.Id:D}";

        Assert.AreEqual(HttpStatusCode.OK, await host.GetStatusAsync(page, asOwner: true));
        Assert.AreEqual(HttpStatusCode.Forbidden, await host.GetStatusAsync(page, asOwner: false));
        Assert.AreEqual(HttpStatusCode.Forbidden, await host.PostAsync(page, $"{page}?handler=MovieMonitor", [new("monitored", "true")], asOwner: false, withToken: false));
        Assert.AreEqual(0, (await movie.Requests.ListAllAsync(10, CancellationToken.None)).Count, "A forbidden change does nothing.");
    }

    [TestMethod]
    public async Task ADisabledMediaModuleLeavesNoMovieRowRequestOrControl()
    {
        await using var movie = await VideoAcquisitionTestHost.CreateAsync(MediaAcquisitionKind.Movie, "Dune", 2021, "438631", DuneRelease);
        var request = await movie.CreateApprovedAsync();
        await using var host = await VideoAdminPageHost.CreateAsync(movie);
        var page = $"/Admin/Media/movie/{movie.Work.Id:D}";
        StringAssert.Contains(await host.GetHtmlAsync("/Admin/Wanted"), "Dune");

        await host.Modules.SetAsync(InstanceModule.Movie, false);

        Assert.IsFalse((await host.GetHtmlAsync("/Admin/Wanted")).Contains("Dune", StringComparison.Ordinal), "The disabled kind leaves the worklist.");
        var requests = await host.GetHtmlAsync("/Admin/Requests");
        Assert.IsFalse(requests.Contains("Dune", StringComparison.Ordinal));
        Assert.IsFalse(requests.Contains("value=\"movie\"", StringComparison.Ordinal), "The kind is not offered as a filter.");
        Assert.AreEqual(HttpStatusCode.NotFound, await host.GetStatusAsync(page));
        Assert.AreEqual(HttpStatusCode.NotFound, await host.PostAsync("/Admin/Requests", $"/Admin/Requests?handler=Approve&id={request.Id}", []));
    }

    [TestMethod]
    public async Task WithAcquisitionOffTheMediaPageOffersNoMonitoringControlsAndNoAction()
    {
        await using var movie = await VideoAcquisitionTestHost.CreateAsync(MediaAcquisitionKind.Movie, "Dune", 2021, "438631", DuneRelease);
        await using var host = await VideoAdminPageHost.CreateAsync(movie);
        var page = $"/Admin/Media/movie/{movie.Work.Id:D}";
        await host.Modules.SetAsync(InstanceModule.Acquisition, false);

        var html = await host.GetHtmlAsync(page);

        StringAssert.Contains(html, "Acquisition is not available for this title.");
        Assert.IsFalse(html.Contains("handler=MovieMonitor", StringComparison.Ordinal));
        Assert.IsFalse(html.Contains("handler=Profile", StringComparison.Ordinal));
        Assert.AreEqual(HttpStatusCode.NotFound, await host.PostAsync("/Admin/Requests", $"{page}?handler=MovieMonitor", [new("monitored", "true")]));
        Assert.AreEqual(0, (await movie.Requests.ListAllAsync(10, CancellationToken.None)).Count);
    }
}
