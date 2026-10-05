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
        var movieRequest = await movie.CreateApprovedAsync();
        await using var movieHost = await VideoAdminPageHost.CreateAsync(movie);

        var html = await movieHost.GetHtmlAsync("/Admin/Wanted");
        StringAssert.Contains(html, $"href=\"/Admin/ManualSearch?id={movieRequest.Id:D}\"");

        StringAssert.Contains(html, "Dune");
        StringAssert.Contains(html, "data-kind=\"movie\"");
        StringAssert.Contains(html, "data-status=\"requested\"");
        StringAssert.Contains(html, $"href=\"/Library/Movie/{movie.Work.Id:D}\"");
        StringAssert.Contains(html, $"href=\"/Admin/Media/movie/{movie.Work.Id:D}\"");
        Assert.AreEqual(1, Regex.Matches(html, @"admin-icon-label"">Search now</span>").Count);
        Assert.AreEqual(0, Regex.Matches(html, @"admin-icon-label"">Retry</span>").Count);
        Assert.IsFalse(html.Contains("handler=SearchAnime", StringComparison.Ordinal), "A Movie has no anime search.");
        Assert.IsFalse(html.Contains("BookManualSearch", StringComparison.Ordinal), "Manual search is offered only where it exists.");

        await using var series = await VideoAcquisitionTestHost.CreateAsync(MediaAcquisitionKind.Tv, "Severance", 2022, "95396", SeveranceFirst, SeveranceSecond);
        var s1e1 = await series.AddEpisodeAsync(1, 1);
        await series.AddEpisodeAsync(1, 2);
        var s2e1 = await series.AddEpisodeAsync(2, 1);
        var seriesRequest = await series.CreateApprovedAsync();
        await using var seriesHost = await VideoAdminPageHost.CreateAsync(series);

        var seriesHtml = await seriesHost.GetHtmlAsync("/Admin/Wanted?type=tv");

        StringAssert.Contains(seriesHtml, $"/Admin/ManualSearch?id={seriesRequest.Id:D}&unit={s1e1.Id:D}");
        StringAssert.Contains(seriesHtml, $"/Admin/ManualSearch?id={seriesRequest.Id:D}&unit={s2e1.Id:D}");
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
        Assert.AreEqual(1, Regex.Matches(html, @"admin-icon-label"">Search now</span>").Count);
        StringAssert.Contains(html, $"href=\"/Admin/ManualSearch?id={request.Id:D}\"");
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
        StringAssert.Contains(html, "role=\"switch\"");
        StringAssert.Contains(html, "aria-checked=\"false\"");
        StringAssert.Contains(html, "Quality profile");
        Assert.IsFalse(html.Contains("/Admin/ManualSearch", StringComparison.Ordinal), "Manual search needs a request.");
        StringAssert.Contains(html, $"href=\"/Library/Movie/{movie.Work.Id:D}\"");
        Assert.IsFalse(html.Contains("data-admin-media-groups", StringComparison.Ordinal), "A Movie has no seasons.");

        Assert.AreEqual(HttpStatusCode.Found, await host.PostAsync(page, $"{page}?handler=MovieMonitor", [new("monitored", "true")]));
        var monitored = await host.GetHtmlAsync(page);
        StringAssert.Contains(monitored, "aria-checked=\"true\"");

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
        StringAssert.Contains(await host.GetHtmlAsync(page), "aria-checked=\"false\"");
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
        StringAssert.Contains(html, $"/Admin/ManualSearch?id={request.Id:D}&unit={series.SecondEpisodeId:D}");
        Assert.AreEqual(4, Regex.Matches(html, "/Admin/ManualSearch").Count, "The header and the phone action bar, the season and the one missing aired episode; the episode with a file and the upcoming one have none.");
        StringAssert.Contains(html, "Monitor all episodes");
        StringAssert.Contains(html, "Monitor future episodes only");
        Assert.AreEqual(3, Regex.Matches(html, @"name=""episodeId""").Count);
        Assert.AreEqual(5, Regex.Matches(html, @"name=""monitored"" value=""false""").Count, "Every episode (three) and both seasons of an All request are monitored, so each switch turns its unit off.");
        StringAssert.Contains(html, "Re-analyse");

        var second = series.SecondEpisodeId!.Value.ToString();
        Assert.AreEqual(HttpStatusCode.Found, await host.PostAsync(page, $"{page}?handler=EpisodeMonitor", [new("episodeId", second), new("monitored", "false")]));
        var payload = VideoRequestPayload.Parse((await series.GetAsync(request.Id)).PayloadJson)!;
        Assert.AreEqual(VideoRequestScope.AllCurrentAndFuture, payload.Scope);
        CollectionAssert.Contains(payload.ExcludedEpisodeIds!, series.SecondEpisodeId!.Value);
        var partial = await host.GetHtmlAsync(page);
        StringAssert.Contains(partial, "Partial");
        StringAssert.Contains(partial, "aria-checked=\"mixed\"");
        Assert.IsFalse(Regex.IsMatch(partial, "role=\"switch\"[^>]*aria-checked=\"mixed\""), "A switch is never mixed; only the checkbox of a season or Series is.");

        Assert.AreEqual(HttpStatusCode.Found, await host.PostAsync(page, $"{page}?handler=SeasonMonitor", [new("season", "2"), new("monitored", "false")]));
        Assert.AreEqual(HttpStatusCode.Found, await host.PostAsync(page, $"{page}?handler=SeasonMonitor", [new("season", "1"), new("monitored", "true")]));
        var restored = VideoRequestPayload.Parse((await series.GetAsync(request.Id)).PayloadJson)!;
        CollectionAssert.DoesNotContain(restored.ExcludedEpisodeIds!, series.SecondEpisodeId!.Value, "Switching a season on monitors all of its episodes again.");
        Assert.AreEqual(HttpStatusCode.BadRequest, await host.PostAsync(page, $"{page}?handler=SeasonMonitor", [new("monitored", "false")]), "A missing season never means the specials.");

        var before = (await series.GetAsync(request.Id)).PayloadJson;
        Assert.AreEqual(HttpStatusCode.NotFound, await host.PostAsync(page, $"{page}?handler=EpisodeMonitor", [new("episodeId", Guid.NewGuid().ToString()), new("monitored", "true")]));
        Assert.AreEqual(HttpStatusCode.NotFound, await host.PostAsync(page, $"{page}?handler=SeasonMonitor", [new("season", "9"), new("monitored", "true")]));
        Assert.AreEqual(before, (await series.GetAsync(request.Id)).PayloadJson, "An episode or season that is not part of the Series changes nothing.");

        Assert.AreEqual(HttpStatusCode.Found, await host.PostAsync(page, $"{page}?handler=SeriesScope", [new("scope", "future")]));
        Assert.AreEqual(VideoRequestScope.FutureOnly, VideoRequestPayload.Parse((await series.GetAsync(request.Id)).PayloadJson)!.Scope);
        Assert.AreEqual(HttpStatusCode.NotFound, await host.PostAsync(page, $"/Admin/Media/series/{Guid.NewGuid():D}?handler=SeriesScope", [new("scope", "all")]));
    }

    [TestMethod]
    public async Task ReanalysingAFileOnlyReachesFilesOfTheTitleTheRowBelongsTo()
    {
        await using var movie = await VideoAcquisitionTestHost.CreateAsync(MediaAcquisitionKind.Movie, "Dune", 2021, "438631", DuneRelease);
        await movie.AttachFileAsync(null, "Dune.2021.1080p.mkv", 2048);
        var other = await movie.AddWorkAsync("Other Film", "999");
        await using var host = await VideoAdminPageHost.CreateAsync(movie);
        var page = $"/Admin/Media/movie/{movie.Work.Id:D}";
        var detail = (await movie.Get<Jularr.Web.Features.Library.AdminVideoMediaService>().LoadAsync(MediaAcquisitionKind.Movie, movie.Work.Id, CancellationToken.None))!;
        var ownFile = detail.Files.Single().Id;
        static async Task<IReadOnlyList<Jularr.Web.Features.Operations.OperationSnapshot>> ReanalysesAsync(VideoAcquisitionTestHost video) =>
            await video.Operations.ListAsync(new Jularr.Web.Features.Operations.OperationListFilter(Kind: "video-reanalyze-media"), CancellationToken.None);

        StringAssert.Contains(await host.GetHtmlAsync(page), "name=\"open\"");
        Assert.AreEqual(HttpStatusCode.NotFound, await host.PostAsync(page, $"{page}?handler=ReanalyzeFile", [new("fileId", Guid.NewGuid().ToString())]));
        var otherPage = $"/Admin/Media/movie/{other.Id:D}";
        Assert.AreEqual(HttpStatusCode.NotFound, await host.PostAsync(otherPage, $"{otherPage}?handler=ReanalyzeFile", [new("fileId", ownFile.ToString())]), "A file of another title is not reachable through this one.");
        Assert.AreEqual(0, (await ReanalysesAsync(movie)).Count, "A refused file starts no analysis.");

        Assert.AreEqual(HttpStatusCode.Found, await host.PostAsync(page, $"{page}?handler=ReanalyzeFile", [new("fileId", ownFile.ToString()), new("open", "v-abc")]));
        Assert.AreEqual(1, (await ReanalysesAsync(movie)).Count);
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
