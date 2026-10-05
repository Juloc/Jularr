using System.Net;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.MediaCore;

namespace Jularr.Tests;

/// <summary>
/// A media type the profile cannot browse has no client API surface: every route that names a canonical target, a legacy
/// episode or a file answers 404 before its handler runs, while a profile that may browse the type reaches the handler.
/// </summary>
[TestClass]
public sealed class ClientVideoAccessTests
{
    private const string Denied = "video_target_not_found";
    private const string Api = "/api/client/v1";

    private static async Task<Work> AddMovieAsync(VideoDetailPageTestHost host, string probeJson = MediaProbeFixtures.H264Stereo)
    {
        var movie = new Work { MediaType = WorkMediaType.Movie, CanonicalTitle = "Moon Empire", Year = 2024 };
        host.Db.Add(movie);
        await host.Db.SaveChangesAsync();
        await host.AttachVideoAsync(movie, null, "moon-empire.mp4", probeJson);
        return movie;
    }

    private static Task<(HttpStatusCode Status, string Body)> CallAsync(VideoDetailPageTestHost host, string route, Work movie, bool asOwner = false) => route switch
    {
        "player" => host.SendAsync(HttpMethod.Post, $"{Api}/video/player", new { target = new { workId = movie.Id } }, asOwner),
        "plan" => host.SendAsync(HttpMethod.Post, $"{Api}/video/playback-plan", new { target = new { workId = movie.Id } }, asOwner),
        "progress" => host.SendAsync(HttpMethod.Put, $"{Api}/video/progress", new { target = new { workId = movie.Id }, positionMs = 60_000, durationMs = 1_440_000, completed = false }, asOwner),
        "cues" => host.SendAsync(HttpMethod.Get, $"{Api}/video/subtitle-tracks/stream:9/cues?workId={movie.Id}", null, asOwner),
        _ => throw new ArgumentOutOfRangeException(nameof(route))
    };

    [TestMethod]
    [DataRow("player")]
    [DataRow("plan")]
    [DataRow("progress")]
    [DataRow("cues")]
    public async Task AProfileThatCannotBrowseTheTypeGetsNotFoundAndOneThatCanReachesTheHandler(string route)
    {
        await using var host = await VideoDetailPageTestHost.CreateAsync();
        var movie = await AddMovieAsync(host);

        var open = await CallAsync(host, route, movie);
        Assert.IsFalse(open.Body.Contains(Denied, StringComparison.Ordinal), $"{route} must reach its handler: {open.Status} {open.Body}");
        if (route is "player" or "progress")
        {
            Assert.AreEqual(HttpStatusCode.OK, open.Status, $"{route}: {open.Body}");
        }

        await host.Capabilities.SetRoleDefaultAsync(AccountRole.User, WorkMediaType.Movie, MediaCapability.Hidden);

        var hidden = await CallAsync(host, route, movie);
        Assert.AreEqual(HttpStatusCode.NotFound, hidden.Status, route);
        StringAssert.Contains(hidden.Body, Denied, route);
        var owner = await CallAsync(host, route, movie, asOwner: true);
        Assert.IsFalse(owner.Body.Contains(Denied, StringComparison.Ordinal), $"The owner is unrestricted on {route}.");
    }

    [TestMethod]
    public async Task AnUnknownOrInvalidCanonicalTargetIsNotFoundOrABadRequestForEveryone()
    {
        await using var host = await VideoDetailPageTestHost.CreateAsync();

        var unknown = await host.SendAsync(HttpMethod.Post, $"{Api}/video/player", new { target = new { workId = Guid.NewGuid() } });
        var invalid = await host.SendAsync(HttpMethod.Post, $"{Api}/video/player", new { target = new { workId = Guid.Empty } });

        Assert.AreEqual(HttpStatusCode.NotFound, unknown.Status);
        StringAssert.Contains(unknown.Body, Denied);
        Assert.AreEqual(HttpStatusCode.BadRequest, invalid.Status);
    }

    [TestMethod]
    public async Task ALegacyEpisodeRouteIsGatedByTheAnimeType()
    {
        await using var host = await VideoDetailPageTestHost.CreateAsync();
        var path = $"{Api}/episodes/{Guid.NewGuid()}/playback-plan";

        var open = await host.SendAsync(HttpMethod.Post, path, new { });
        Assert.AreEqual(HttpStatusCode.NotFound, open.Status);
        StringAssert.Contains(open.Body, "media_not_found", "A profile that may browse Anime reaches the handler, which does not know the episode.");

        await host.Capabilities.SetRoleDefaultAsync(AccountRole.User, WorkMediaType.Anime, MediaCapability.Hidden);

        var hidden = await host.SendAsync(HttpMethod.Post, path, new { });
        StringAssert.Contains(hidden.Body, Denied);
    }

    [TestMethod]
    public async Task ASeriesEpisodeTargetFollowsTheSeriesType()
    {
        await using var host = await VideoDetailPageTestHost.CreateAsync();
        var series = new Work { MediaType = WorkMediaType.Series, CanonicalTitle = "Dark Harbor" };
        host.Db.Add(series);
        await host.Db.SaveChangesAsync();
        var episode = await new LibraryCanonicalSeed(host.Db).AddEpisodeAsync(series, 1, 1);
        await host.Capabilities.SetRoleDefaultAsync(AccountRole.User, WorkMediaType.Series, MediaCapability.Hidden);

        var hidden = await host.SendAsync(HttpMethod.Put, $"{Api}/video/progress", new { target = new { workId = series.Id, workEpisodeId = episode.Id }, positionMs = 90_000, durationMs = 1_440_000, completed = false });

        Assert.AreEqual(HttpStatusCode.NotFound, hidden.Status);
        StringAssert.Contains(hidden.Body, Denied);
        var stored = await new Jularr.Web.Features.Progress.VideoProgressService(host.Db).GetAsync(VideoDetailPageTestHost.Profile, new Jularr.Web.Features.Progress.MediaProgressTarget(series.Id, episode.Id));
        Assert.IsNull(stored!.UpdatedAt, "A hidden type's progress is never written.");
    }

    // ---- Embedded subtitle cues of a canonical target ------------------------------------------------------------------

    [TestMethod]
    public async Task TheCueRouteRejectsBadTracksAndTargetsAndStreamsThatAreNotText()
    {
        await using var host = await VideoDetailPageTestHost.CreateAsync();
        var movie = await AddMovieAsync(host, MediaProbeFixtures.HevcTenBitHdrMultiAudio);
        string Cues(string track, Guid workId) => $"{Api}/video/subtitle-tracks/{track}/cues?workId={workId}";

        var badTrack = await host.SendAsync(HttpMethod.Get, Cues("not-a-track", movie.Id));
        var unknownTarget = await host.SendAsync(HttpMethod.Get, Cues("stream:3", Guid.NewGuid()));
        var pictureStream = await host.SendAsync(HttpMethod.Get, Cues("stream:4", movie.Id));
        var missingStream = await host.SendAsync(HttpMethod.Get, Cues("stream:9", movie.Id));

        Assert.AreEqual(HttpStatusCode.BadRequest, badTrack.Status);
        StringAssert.Contains(badTrack.Body, "invalid_track_id");
        Assert.AreEqual(HttpStatusCode.NotFound, unknownTarget.Status);
        StringAssert.Contains(unknownTarget.Body, Denied);
        Assert.AreEqual(HttpStatusCode.NotFound, pictureStream.Status, "A picture subtitle has no text cues.");
        StringAssert.Contains(pictureStream.Body, "subtitle_track_not_found");
        Assert.AreEqual(HttpStatusCode.NotFound, missingStream.Status);
        StringAssert.Contains(missingStream.Body, "subtitle_track_not_found");
    }
}
