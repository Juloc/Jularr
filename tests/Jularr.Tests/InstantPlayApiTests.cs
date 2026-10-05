using System.Net;
using System.Text.Json;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.Instance;
using Jularr.Web.Features.InstantPlay;
using Jularr.Web.Features.MediaCore;
using Microsoft.AspNetCore.Http.Metadata;
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
    public async Task BothRoutesAreAuthenticatedRateLimitedAndTheIntentBodyIsBounded()
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

        Assert.IsTrue(intent.Metadata.GetMetadata<IRequestSizeLimitMetadata>()?.MaxRequestBodySize is <= 4096, "The intent body is a target id pair, not an open upload.");
    }
}
