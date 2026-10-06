using System.Net;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.MediaCore;
using Jularr.Web.Features.Watchlist;

namespace Jularr.Tests;

/// <summary>
/// The watchlist control of the Movie and Series heroes (docs/mockups/movie-detail and anime-series-detail, "Watchlist/Favorite where
/// supported"): a plain form on the canonical profile-scoped watchlist, built from the stored provider identity and guarded like the reads.
/// </summary>
[TestClass]
public sealed class VideoDetailWatchlistTests
{
    private const string Profile = VideoDetailPageTestHost.Profile;
    private static readonly IReadOnlyDictionary<string, string> NoFields = new Dictionary<string, string>();

    private static async Task<Work> AddTitleAsync(VideoDetailPageTestHost host, WorkMediaType type, string title, string? tmdbId)
    {
        var work = await new LibraryCanonicalSeed(host.Db).AddWorkAsync(type, title, 2024);
        if (tmdbId is not null)
        {
            host.Db.Add(new WorkExternalIdentity { WorkId = work.Id, MediaType = type, Provider = "tmdb", ExternalId = tmdbId, IsPrimary = true });
            await host.Db.SaveChangesAsync();
        }

        return work;
    }

    private static Task<IReadOnlyList<WatchlistItem>> FollowedAsync(VideoDetailPageTestHost host, string profile = Profile) => new WatchlistStore(host.Db).GetEffectiveAsync(profile, CancellationToken.None);

    private static string Hero(string html)
    {
        var from = html.IndexOf("<section class=\"ad-hero", StringComparison.Ordinal);
        return html[from..html.IndexOf("</section>", from, StringComparison.Ordinal)];
    }

    [TestMethod]
    public async Task AMovieIsFollowedAndUnfollowedFromItsHeroAndTheStateSurvivesTheRoundTrip()
    {
        await using var host = await VideoDetailPageTestHost.CreateAsync();
        var movie = await AddTitleAsync(host, WorkMediaType.Movie, "Moon Empire", "603");
        var path = $"/Library/Movie/{movie.Id}";

        var before = Hero(await host.GetOkAsync(path));
        StringAssert.Contains(before, $"action=\"{path}?handler=Follow\"");
        StringAssert.Contains(before, "aria-pressed=\"false\"");
        StringAssert.Contains(before, ">Add to watchlist<");

        var followed = await host.PostFormAsync($"{path}?handler=Follow", NoFields);

        Assert.AreEqual((HttpStatusCode.Redirect, path), (followed.Status, followed.Location), "The form returns to the page it came from.");
        var items = await FollowedAsync(host);
        Assert.AreEqual(("movie:tmdb:603", "Moon Empire"), (items.Single().Identity.Key, items.Single().Title), "The follow uses the identity Discover and the Watchlist page use.");
        var after = Hero(await host.GetOkAsync(path));
        StringAssert.Contains(after, $"action=\"{path}?handler=Unfollow\"");
        StringAssert.Contains(after, "aria-pressed=\"true\"");
        StringAssert.Contains(after, ">On watchlist<");
        StringAssert.Contains(after, "title=\"Remove from watchlist\"");

        await host.PostFormAsync($"{path}?handler=Follow", NoFields);
        Assert.HasCount(1, await FollowedAsync(host), "Following twice is the same follow.");

        await host.PostFormAsync($"{path}?handler=Unfollow", NoFields);
        await host.PostFormAsync($"{path}?handler=Unfollow", NoFields);
        Assert.IsEmpty(await FollowedAsync(host), "Unfollowing twice leaves it off.");
        StringAssert.Contains(Hero(await host.GetOkAsync(path)), ">Add to watchlist<");
    }

    [TestMethod]
    public async Task AFollowBelongsToTheProfileThatMadeIt()
    {
        await using var host = await VideoDetailPageTestHost.CreateAsync();
        var movie = await AddTitleAsync(host, WorkMediaType.Movie, "Moon Empire", "603");
        var path = $"/Library/Movie/{movie.Id}";

        await host.PostFormAsync($"{path}?handler=Follow", NoFields);

        Assert.HasCount(1, await FollowedAsync(host));
        Assert.IsEmpty(await FollowedAsync(host, "someone-else"));
        StringAssert.Contains(Hero(await host.GetOkAsync(path, profile: "someone-else")), ">Add to watchlist<");
        StringAssert.Contains(Hero(await host.GetOkAsync(path)), ">On watchlist<");
    }

    [TestMethod]
    public async Task ASeriesFollowReturnsToTheSeasonSortAndLayoutTheViewerChose()
    {
        await using var host = await VideoDetailPageTestHost.CreateAsync();
        var series = await AddTitleAsync(host, WorkMediaType.Series, "Dark Harbor", "1399");
        await new LibraryCanonicalSeed(host.Db).AddEpisodeAsync(series, 2, 1);
        var path = $"/Library/Series/{series.Id}";

        var page = Hero(await host.GetOkAsync($"{path}?season=2&sort=unwatched&view=list"));
        StringAssert.Contains(page, $"action=\"{path}?season=2&sort=unwatched&view=list&handler=Follow\"");

        var followed = await host.PostFormAsync($"{path}?handler=Follow&season=2&sort=unwatched&view=list&ignored=1", NoFields);

        Assert.AreEqual((HttpStatusCode.Redirect, $"{path}?season=2&sort=unwatched&view=list"), (followed.Status, followed.Location), "Only the view state of the page is kept.");
        Assert.AreEqual("tv:tmdb:1399", (await FollowedAsync(host)).Single().Identity.Key);
    }

    [TestMethod]
    public async Task TheFollowIsBuiltFromTheStoredTitleAndIdentityNotFromTheForm()
    {
        await using var host = await VideoDetailPageTestHost.CreateAsync();
        var movie = await AddTitleAsync(host, WorkMediaType.Movie, "Moon Empire", "603");
        var forged = new Dictionary<string, string> { ["title"] = "Forged", ["externalId"] = "1", ["provider"] = "other", ["category"] = "tv" };

        await host.PostFormAsync($"/Library/Movie/{movie.Id}?handler=Follow&title=Forged&externalId=1", forged);

        var item = (await FollowedAsync(host)).Single();
        Assert.AreEqual(("movie:tmdb:603", "Moon Empire"), (item.Identity.Key, item.Title));
    }

    [TestMethod]
    public async Task ATitleTheProviderDoesNotIdentifyCannotBeFollowed()
    {
        await using var host = await VideoDetailPageTestHost.CreateAsync();
        var movie = await AddTitleAsync(host, WorkMediaType.Movie, "Unmatched", tmdbId: null);

        var html = await host.GetOkAsync($"/Library/Movie/{movie.Id}");
        var follow = await host.PostFormAsync($"/Library/Movie/{movie.Id}?handler=Follow", NoFields);

        Assert.IsFalse(Hero(html).Contains("watchlist", StringComparison.OrdinalIgnoreCase), "Without an identity there is nothing to follow, so no control is offered.");
        Assert.AreEqual(HttpStatusCode.NotFound, follow.Status);
        Assert.IsEmpty(await FollowedAsync(host));
    }

    [TestMethod]
    public async Task AHiddenOrMismatchedTitleCannotBeFollowedThroughAPage()
    {
        await using var host = await VideoDetailPageTestHost.CreateAsync();
        var movie = await AddTitleAsync(host, WorkMediaType.Movie, "Moon Empire", "603");
        var series = await AddTitleAsync(host, WorkMediaType.Series, "Dark Harbor", "1399");

        var movieThroughSeries = await host.PostFormAsync($"/Library/Series/{movie.Id}?handler=Follow", NoFields);
        var seriesThroughMovie = await host.PostFormAsync($"/Library/Movie/{series.Id}?handler=Follow", NoFields);
        var unknown = await host.PostFormAsync($"/Library/Movie/{Guid.NewGuid()}?handler=Follow", NoFields);
        await host.Capabilities.SetRoleDefaultAsync(AccountRole.User, WorkMediaType.Movie, MediaCapability.Hidden);
        var hidden = await host.PostFormAsync($"/Library/Movie/{movie.Id}?handler=Follow", NoFields);

        foreach (var (status, _, _) in new[] { movieThroughSeries, seriesThroughMovie, unknown, hidden })
        {
            Assert.AreEqual(HttpStatusCode.NotFound, status);
        }

        Assert.IsEmpty(await FollowedAsync(host));
    }

    [TestMethod]
    public async Task TheWatchlistControlIsOnlyAPersonalActionAndNeverStartsAcquisition()
    {
        await using var host = await VideoDetailPageTestHost.CreateAsync();
        await host.MakeAcquisitionReadyAsync();
        var movie = await AddTitleAsync(host, WorkMediaType.Movie, "Moon Empire", "603");

        await host.PostFormAsync($"/Library/Movie/{movie.Id}?handler=Follow", NoFields);

        Assert.IsEmpty(await new AcquisitionAccessStore(host.Db).ListAllAsync(50, CancellationToken.None), "Watchlist, Favorite and Collection are separate from Request.");
    }
}
