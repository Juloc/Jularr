using Jularr.Web.Features.Monitoring;
using System.Net;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.InstantPlay;
using Jularr.Web.Features.MediaCore;

namespace Jularr.Tests;

/// <summary>The hero action of the Movie and Series pages (<c>?handler=Start</c>) is an explicit playback intent and passes the same guards as the reads.</summary>
[TestClass]
public sealed class InstantPlayPageIntentTests
{
    private static readonly IReadOnlyDictionary<string, string> NoEpisode = new Dictionary<string, string> { ["episodeId"] = "" };

    private static async Task<VideoDetailPageTestHost> ReadyHostAsync()
    {
        var host = await VideoDetailPageTestHost.CreateAsync();
        await host.MakeAcquisitionReadyAsync();
        return host;
    }

    private static async Task<Work> AddTitleAsync(VideoDetailPageTestHost host, WorkMediaType type, string title, string tmdbId)
    {
        var work = await new LibraryCanonicalSeed(host.Db).AddWorkAsync(type, title, 2024);
        host.Db.Add(new WorkExternalIdentity { WorkId = work.Id, MediaType = type, Provider = "tmdb", ExternalId = tmdbId, IsPrimary = true });
        await host.Db.SaveChangesAsync();
        return work;
    }

    private static Task<IReadOnlyList<AcquisitionRequest>> RequestsAsync(VideoDetailPageTestHost host) => new AcquisitionAccessStore(host.Db).ListAllAsync(50, CancellationToken.None);

    [TestMethod]
    public async Task AHiddenMediaTypeCannotStartAnythingThroughItsPage()
    {
        await using var host = await ReadyHostAsync();
        var movie = await AddTitleAsync(host, WorkMediaType.Movie, "Moon Empire", "603");
        await host.Capabilities.SetRoleDefaultAsync(AccountRole.User, WorkMediaType.Movie, MediaCapability.Hidden);

        var (status, _, _) = await host.PostFormAsync($"/Library/Movie/{movie.Id}?handler=Start", NoEpisode);

        Assert.AreEqual(HttpStatusCode.NotFound, status);
        Assert.IsEmpty(await RequestsAsync(host));
    }

    [TestMethod]
    public async Task APageOfAVisibleTypeCannotStartAWorkOfAnotherOrHiddenType()
    {
        await using var host = await ReadyHostAsync();
        var movie = await AddTitleAsync(host, WorkMediaType.Movie, "Moon Empire", "603");
        var series = await AddTitleAsync(host, WorkMediaType.Series, "Dark Harbor", "1399");
        await host.Capabilities.SetRoleDefaultAsync(AccountRole.User, WorkMediaType.Movie, MediaCapability.Hidden);
        await host.Capabilities.SetRoleDefaultAsync(AccountRole.User, WorkMediaType.Series, MediaCapability.Instant);

        var movieThroughSeries = await host.PostFormAsync($"/Library/Series/{movie.Id}?handler=Start", NoEpisode);
        var seriesThroughMovie = await host.PostFormAsync($"/Library/Movie/{series.Id}?handler=Start", NoEpisode);
        var unknown = await host.PostFormAsync($"/Library/Series/{Guid.NewGuid()}?handler=Start", NoEpisode);

        Assert.AreEqual(HttpStatusCode.NotFound, movieThroughSeries.Status, "A Movie id on the Series page would acquire a hidden type.");
        Assert.AreEqual(HttpStatusCode.NotFound, seriesThroughMovie.Status);
        Assert.AreEqual(HttpStatusCode.NotFound, unknown.Status);
        Assert.IsEmpty(await RequestsAsync(host));
    }

    [TestMethod]
    public async Task APermittedStartCreatesExactlyOneRequestEvenWhenSubmittedTwice()
    {
        await using var host = await ReadyHostAsync();
        var series = await AddTitleAsync(host, WorkMediaType.Series, "Dark Harbor", "1399");
        var first = await new LibraryCanonicalSeed(host.Db).AddEpisodeAsync(series, 1, 1);
        await new LibraryCanonicalSeed(host.Db).AddEpisodeAsync(series, 1, 2);
        var fields = new Dictionary<string, string> { ["episodeId"] = first.Id.ToString() };

        var one = await host.PostFormAsync($"/Library/Series/{series.Id}?handler=Start", fields, asOwner: true);
        var two = await host.PostFormAsync($"/Library/Series/{series.Id}?handler=Start", fields, asOwner: true);

        Assert.AreEqual(HttpStatusCode.Redirect, one.Status);
        StringAssert.EndsWith(one.Location!, $"/Library/Series/{series.Id}");
        Assert.AreEqual(HttpStatusCode.Redirect, two.Status);
        var request = Assert.ContainsSingle(await RequestsAsync(host));
        var payload = VideoRequestPayload.Parse(request.PayloadJson)!;
        var requested = payload.Requested!;
        CollectionAssert.AreEqual(new[] { first.Id }, requested.EpisodeIds.ToArray(), "Exactly the chosen episode, applied when the request first runs.");
        Assert.IsFalse(requested.MonitorFuture);
        Assert.AreEqual(1, payload.ActivePlaybackMarkers(DateTime.UtcNow).Count());
    }

    [TestMethod]
    public async Task ALocalTargetOpensThePlayerAndCreatesNothing()
    {
        await using var host = await ReadyHostAsync();
        var movie = await AddTitleAsync(host, WorkMediaType.Movie, "Moon Empire", "603");
        await new LibraryCanonicalSeed(host.Db).AddVideoAsync(movie, null);

        var (status, location, _) = await host.PostFormAsync($"/Library/Movie/{movie.Id}?handler=Start", NoEpisode, asOwner: true);

        Assert.AreEqual(HttpStatusCode.Redirect, status);
        Assert.AreEqual($"/Library/Watch/{movie.Id}", location);
        Assert.IsEmpty(await RequestsAsync(host));
    }

    [TestMethod]
    public async Task ReachingTheLimitShowsAMessageAndStartsNothing()
    {
        await using var host = await ReadyHostAsync();
        var movie = await AddTitleAsync(host, WorkMediaType.Movie, "Moon Empire", "603");
        var store = new AcquisitionAccessStore(host.Db);
        for (var index = 0; index < PlaybackIntentService.MaxOutstandingPlaybackMarkers; index++)
        {
            var marker = new PlaybackMarker(null, VideoDetailPageTestHost.Profile, DateTime.UtcNow);
            var marked = new VideoRequestPayload(Guid.NewGuid(), $"Other {index}", 2020) { PlaybackMarkers = [marker] };
            await store.CreateAsync(new AcquisitionRequestDraft(MediaAcquisitionKind.Movie, "tmdb", $"80{index}", $"Other {index}", null, null, marked.Serialize()), "owner", AcquisitionRequestStatus.Approved, "owner", CancellationToken.None);
        }

        var (status, _, cookies) = await host.PostFormAsync($"/Library/Movie/{movie.Id}?handler=Start", NoEpisode, asOwner: true);

        Assert.AreEqual(HttpStatusCode.Redirect, status);
        Assert.IsTrue(cookies.Any(x => x.Contains("TempData", StringComparison.OrdinalIgnoreCase)), $"The page carries the message to the next view.");
        Assert.AreEqual(PlaybackIntentService.MaxOutstandingPlaybackMarkers, (await RequestsAsync(host)).Count);
    }
}
