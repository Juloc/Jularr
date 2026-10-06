using System.Net;
using System.Text.Json;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.Instance;
using Jularr.Web.Features.InstantPlay;
using Jularr.Web.Features.MediaCore;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.RateLimiting;

namespace Jularr.Tests;

/// <summary>The Instant Play client API (additive to version 2): authorization at the boundary, ownership, visibility and safe results.</summary>
[TestClass]
public sealed class InstantPlayApiTests
{
    private const string Intents = "/api/client/v1/video/playback-intents";

    private static async Task<Work> AddTitleAsync(VideoDetailPageTestHost host, WorkMediaType type, string title, string tmdbId)
    {
        var work = await new LibraryCanonicalSeed(host.Db).AddWorkAsync(type, title, 2024);
        host.Db.Add(new WorkExternalIdentity { WorkId = work.Id, MediaType = type, Provider = "tmdb", ExternalId = tmdbId, IsPrimary = true });
        await host.Db.SaveChangesAsync();
        return work;
    }

    private static object Target(Guid workId, Guid? episodeId = null) => new { target = new { workId, workEpisodeId = episodeId } };

    private static JsonElement Json(string body) => JsonDocument.Parse(body).RootElement.Clone();

    private static Task<AcquisitionRequest> CreateMovieRequestAsync(VideoDetailPageTestHost host, string requestedBy)
    {
        var draft = new AcquisitionRequestDraft(MediaAcquisitionKind.Movie, "tmdb", "603", "Moon Empire", null, null);
        return new AcquisitionAccessStore(host.Db).CreateAsync(draft, requestedBy, AcquisitionRequestStatus.Approved, "owner", CancellationToken.None);
    }

    private static async Task<VideoDetailPageTestHost> ReadyHostAsync()
    {
        var host = await VideoDetailPageTestHost.CreateAsync();
        await host.MakeAcquisitionReadyAsync();
        return host;
    }

    [TestMethod]
    public async Task TheIntentValidatesItsTargetBeforeAnythingHappens()
    {
        await using var host = await ReadyHostAsync();
        var movie = await AddTitleAsync(host, WorkMediaType.Movie, "Moon Empire", "603");
        var anime = await AddTitleAsync(host, WorkMediaType.Anime, "Starfall", "1");

        Assert.AreEqual(HttpStatusCode.BadRequest, (await host.SendAsync(HttpMethod.Post, Intents, new { }, asOwner: true)).Status);
        Assert.AreEqual(HttpStatusCode.BadRequest, (await host.SendAsync(HttpMethod.Post, Intents, Target(Guid.Empty), asOwner: true)).Status);
        Assert.AreEqual(HttpStatusCode.NotFound, (await host.SendAsync(HttpMethod.Post, Intents, Target(Guid.NewGuid()), asOwner: true)).Status);
        Assert.AreEqual(HttpStatusCode.NotFound, (await host.SendAsync(HttpMethod.Post, Intents, Target(movie.Id, Guid.NewGuid()), asOwner: true)).Status, "A Movie has no episode.");
        Assert.AreEqual(HttpStatusCode.NotFound, (await host.SendAsync(HttpMethod.Post, Intents, Target(anime.Id), asOwner: true)).Status, "Anime keeps its own player route.");
        Assert.IsEmpty(await new AcquisitionAccessStore(host.Db).ListAllAsync(10, CancellationToken.None));
    }

    [TestMethod]
    public async Task OnlyAJsonIntentFromThisApplicationIsAccepted()
    {
        await using var host = await ReadyHostAsync();
        var movie = await AddTitleAsync(host, WorkMediaType.Movie, "Moon Empire", "603");
        var body = JsonSerializer.Serialize(Target(movie.Id));

        var simple = await host.SendRawAsync(HttpMethod.Post, Intents, body, "text/plain", asOwner: true);
        var form = await host.SendRawAsync(HttpMethod.Post, Intents, "target=1", "application/x-www-form-urlencoded", asOwner: true);
        var crossSite = await host.SendRawAsync(HttpMethod.Post, Intents, body, "application/json", new Dictionary<string, string> { ["Sec-Fetch-Site"] = "cross-site" }, asOwner: true);
        var sameSite = await host.SendRawAsync(HttpMethod.Post, Intents, body, "application/json", new Dictionary<string, string> { ["Sec-Fetch-Site"] = "same-site" }, asOwner: true);
        Assert.IsEmpty(await new AcquisitionAccessStore(host.Db).ListAllAsync(10, CancellationToken.None), "Nothing is acquired for a request that was refused.");

        Assert.AreEqual(HttpStatusCode.UnsupportedMediaType, simple, "text/plain needs no preflight, so it is not an intent.");
        Assert.AreEqual(HttpStatusCode.UnsupportedMediaType, form);
        Assert.AreEqual(HttpStatusCode.Forbidden, crossSite);
        Assert.AreEqual(HttpStatusCode.Forbidden, sameSite, "Another origin of the same site is still another origin.");
        foreach (var origin in new[] { "same-origin", "none" })
        {
            var accepted = await host.SendRawAsync(HttpMethod.Post, Intents, body, "application/json; charset=utf-8", new Dictionary<string, string> { ["Sec-Fetch-Site"] = origin }, asOwner: true);
            Assert.AreEqual(HttpStatusCode.OK, accepted, origin);
        }

        Assert.AreEqual(HttpStatusCode.OK, await host.SendRawAsync(HttpMethod.Post, Intents, body, "application/json", asOwner: true), "A native client sends no Sec-Fetch header.");
    }

    [TestMethod]
    public async Task TheStatusSentToAClientCarriesNoValidityFlag()
    {
        await using var host = await ReadyHostAsync();
        var movie = await AddTitleAsync(host, WorkMediaType.Movie, "Moon Empire", "603");

        var (_, body) = await host.SendAsync(HttpMethod.Post, Intents, Target(movie.Id), asOwner: true);

        Assert.IsFalse(Json(body).GetProperty("target").TryGetProperty("isValid", out _));
    }

    [TestMethod]
    public async Task AHiddenMediaTypeHasNoIntentSurfaceAndBrowseOnlyCannotAcquire()
    {
        await using var host = await ReadyHostAsync();
        var movie = await AddTitleAsync(host, WorkMediaType.Movie, "Moon Empire", "603");

        await host.Capabilities.SetRoleDefaultAsync(AccountRole.User, WorkMediaType.Movie, MediaCapability.Hidden);
        Assert.AreEqual(HttpStatusCode.NotFound, (await host.SendAsync(HttpMethod.Post, Intents, Target(movie.Id))).Status, "Hidden like a media type without pages.");

        await host.Capabilities.SetRoleDefaultAsync(AccountRole.User, WorkMediaType.Movie, MediaCapability.Browse);
        var (status, body) = await host.SendAsync(HttpMethod.Post, Intents, Target(movie.Id));
        Assert.AreEqual(HttpStatusCode.OK, status);
        Assert.AreEqual("not_available", Json(body).GetProperty("outcome").GetString());

        await host.Capabilities.SetRoleDefaultAsync(AccountRole.User, WorkMediaType.Movie, MediaCapability.Request);
        Assert.AreEqual("request_required", Json((await host.SendAsync(HttpMethod.Post, Intents, Target(movie.Id))).Body).GetProperty("outcome").GetString(), "Approval is never bypassed.");
        Assert.IsEmpty(await new AcquisitionAccessStore(host.Db).ListAllAsync(10, CancellationToken.None));
    }

    [TestMethod]
    public async Task AnAutoApprovingProfileStartsOneRequestAndReadsItsConsumerState()
    {
        await using var host = await ReadyHostAsync();
        var movie = await AddTitleAsync(host, WorkMediaType.Movie, "Moon Empire", "603");

        var (status, body) = await host.SendAsync(HttpMethod.Post, Intents, Target(movie.Id), asOwner: true);
        var again = await host.SendAsync(HttpMethod.Post, Intents, Target(movie.Id), asOwner: true);

        Assert.AreEqual(HttpStatusCode.OK, status);
        var intent = Json(body);
        Assert.AreEqual("acquiring", intent.GetProperty("outcome").GetString());
        Assert.AreEqual(movie.Id, intent.GetProperty("target").GetProperty("workId").GetGuid());
        var requestId = intent.GetProperty("requestId").GetGuid();
        Assert.AreEqual(requestId, Json(again.Body).GetProperty("requestId").GetGuid(), "A repeat attaches to the same request.");
        Assert.AreEqual(1, (await new AcquisitionAccessStore(host.Db).ListAllAsync(10, CancellationToken.None)).Count);
        Assert.AreEqual("looking_for_media", intent.GetProperty("acquisition").GetProperty("state").GetString());

        var read = await host.SendAsync(HttpMethod.Get, $"/api/client/v1/requests/{requestId}", asOwner: true);
        Assert.AreEqual(HttpStatusCode.OK, read.Status);
        var status2 = Json(read.Body);
        Assert.AreEqual(requestId, status2.GetProperty("requestId").GetGuid());
        Assert.AreEqual("looking_for_media", status2.GetProperty("acquisition").GetProperty("state").GetString());
        Assert.AreEqual("movie", status2.GetProperty("acquisition").GetProperty("mediaUnit").GetString());
    }

    [TestMethod]
    public async Task RequestStateIsOnlyReadableWithTheRequestOrTheCapabilityForItsMediaType()
    {
        await using var host = await ReadyHostAsync();
        await AddTitleAsync(host, WorkMediaType.Movie, "Moon Empire", "603");
        var series = await AddTitleAsync(host, WorkMediaType.Series, "Dark Harbor", "1399");
        var episode = await new LibraryCanonicalSeed(host.Db).AddEpisodeAsync(series, 1, 1);
        var request = await CreateMovieRequestAsync(host, "someone-else");
        var path = $"/api/client/v1/requests/{request.Id}";

        await host.Capabilities.SetRoleDefaultAsync(AccountRole.User, WorkMediaType.Movie, MediaCapability.Browse);
        Assert.AreEqual(HttpStatusCode.NotFound, (await host.SendAsync(HttpMethod.Get, path)).Status, "Browse only: someone else's request is not theirs to read.");

        await host.Capabilities.SetRoleDefaultAsync(AccountRole.User, WorkMediaType.Movie, MediaCapability.Request);
        Assert.AreEqual(HttpStatusCode.OK, (await host.SendAsync(HttpMethod.Get, path)).Status, "Title-level state a profile that may request it already sees on the detail page.");
        Assert.AreEqual(HttpStatusCode.OK, (await host.SendAsync(HttpMethod.Get, path, profile: "someone-else")).Status, "The requester always may.");

        Assert.AreEqual(HttpStatusCode.NotFound, (await host.SendAsync(HttpMethod.Get, $"/api/client/v1/requests/{Guid.NewGuid()}", asOwner: true)).Status);
        Assert.AreEqual(HttpStatusCode.NotFound, (await host.SendAsync(HttpMethod.Get, $"{path}?workEpisodeId={episode.Id}", asOwner: true)).Status, "A Movie request has no episode, and an episode of another Work is no target.");

        await host.Capabilities.SetRoleDefaultAsync(AccountRole.User, WorkMediaType.Movie, MediaCapability.Hidden);
        Assert.AreEqual(HttpStatusCode.NotFound, (await host.SendAsync(HttpMethod.Get, path, profile: "someone-else")).Status, "A hidden media type has no API surface, even for the requester.");
    }

    [TestMethod]
    public async Task ManagerOnlyKeepsRequestStateAndClosesTheIntentRoute()
    {
        await using var host = await ReadyHostAsync();
        var movie = await AddTitleAsync(host, WorkMediaType.Movie, "Moon Empire", "603");
        var request = await CreateMovieRequestAsync(host, "owner");
        await host.Modules.SetAsync(InstanceModule.Playback, false);

        Assert.AreEqual(HttpStatusCode.NotFound, (await host.SendAsync(HttpMethod.Post, Intents, Target(movie.Id), asOwner: true)).Status);
        var read = await host.SendAsync(HttpMethod.Get, $"/api/client/v1/requests/{request.Id}", asOwner: true);
        Assert.AreEqual(HttpStatusCode.OK, read.Status);
        Assert.AreEqual("looking_for_media", Json(read.Body).GetProperty("acquisition").GetProperty("state").GetString());
    }

    [TestMethod]
    public async Task AnOversizedIntentBodyIsRefusedBeforeItIsRead()
    {
        await using var host = await VideoDetailPageTestHost.CreateAsync();
        var big = new { target = new { workId = Guid.NewGuid(), workEpisodeId = (Guid?)null }, padding = new string('x', 100_000) };

        var (status, _) = await host.SendAsync(HttpMethod.Post, Intents, big, asOwner: true);

        Assert.AreEqual(HttpStatusCode.RequestEntityTooLarge, status);
    }

    [TestMethod]
    public async Task BothRoutesAreAuthenticatedAndRateLimitedAndNeitherOffersACancelOrStopWaitingRoute()
    {
        await using var host = await VideoDetailPageTestHost.CreateAsync();
        var endpoints = host.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>().ToArray();
        var intent = endpoints.Single(x => x.RoutePattern.RawText == "/api/client/v1/video/playback-intents");
        var status = endpoints.Single(x => x.RoutePattern.RawText == "/api/client/v1/requests/{requestId:guid}");

        foreach (var (endpoint, policy) in new[] { (intent, InstantPlayRegistration.IntentRateLimitPolicy), (status, InstantPlayRegistration.StatusRateLimitPolicy) })
        {
            Assert.IsTrue(endpoint.Metadata.GetOrderedMetadata<Microsoft.AspNetCore.Authorization.IAuthorizeData>().Any(), $"{endpoint.RoutePattern.RawText} requires authorization.");
            Assert.AreEqual(policy, endpoint.Metadata.GetMetadata<EnableRateLimitingAttribute>()?.PolicyName);
        }

        // Stop waiting is a client-side wait: the only server actions around an intent are starting one and reading a request.
        Assert.AreEqual("POST", string.Join(",", intent.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods));
        Assert.AreEqual("GET", string.Join(",", status.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods));
        var related = endpoints.Select(x => x.RoutePattern.RawText!).Where(x => x.Contains("playback-intent", StringComparison.Ordinal) || x.Contains("/requests", StringComparison.Ordinal)).ToArray();
        CollectionAssert.AreEquivalent(new[] { intent.RoutePattern.RawText, status.RoutePattern.RawText }, related);
        Assert.IsFalse(endpoints.Any(x => x.RoutePattern.RawText!.Contains("cancel", StringComparison.OrdinalIgnoreCase) || x.RoutePattern.RawText.Contains("stop", StringComparison.OrdinalIgnoreCase)));
    }
}
