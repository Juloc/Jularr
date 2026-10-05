using Jularr.Web.Data;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.ClientApi;
using Jularr.Web.Features.MediaSegments;
using Jularr.Web.Features.Pairing;
using Jularr.Web.Features.Playback;
using Jularr.Web.Features.PlaybackSessions;
using Jularr.Web.Features.Progress;
using Jularr.Web.Features.Shell;
using Jularr.Web.Features.Speech;
using Jularr.Web.Features.Storage;
using Jularr.Web.Features.Watchlist;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Jularr.Tests;

/// <summary>
/// Verifies the client-api watchlist route's routing/authorization wiring without a live host
/// (this repository has no WebApplicationFactory harness, see
/// OfflineLibraryReaderIntegrationTests): the route must require an authenticated session like
/// its `/continue-watching` and `/me/playback-history` siblings, and must never be anonymous.
/// </summary>
[TestClass]
public sealed class ClientApiWatchlistEndpointTests
{
    [TestMethod]
    public void WatchlistEndpointRequiresAuthenticationAndIsNeverAnonymous()
    {
        var builder = WebApplication.CreateSlimBuilder();
        // Only needed so minimal APIs recognize these as DI service parameters (not an inferred
        // request body) when building endpoint metadata; no handler runs in this test.
        builder.Services.AddSingleton<OwnerAuthService>(_ => null!);
        builder.Services.AddSingleton<CurrentAccountContext>(_ => null!);
        builder.Services.AddSingleton<ClientApiService>(_ => null!);
        builder.Services.AddSingleton<EpisodeProgressService>(_ => null!);
        builder.Services.AddSingleton<Jularr.Web.Features.Speech.TtsPreferencesService>(_ => null!);
        builder.Services.AddSingleton<SpeechModelManifestStore>(_ => null!);
        builder.Services.AddSingleton<MediaAvailabilityService>(_ => null!);
        builder.Services.AddSingleton<PlaybackService>(_ => null!);
        builder.Services.AddSingleton<Jularr.Web.Features.Playback.HlsPlaybackSessionManager>(_ => null!);
        builder.Services.AddSingleton<Jularr.Web.Features.Playback.Decision.PlaybackAdmissionService>(_ => null!);
        builder.Services.AddSingleton<LibraryRootAvailabilityService>(_ => null!);
        builder.Services.AddSingleton<WakeOnLanService>(_ => null!);
        builder.Services.AddSingleton<MediaSegmentService>(_ => null!);
        // The video access filter runs on every client route but only reads these for a request that names a video target.
        builder.Services.AddSingleton<IAppShellService>(_ => null!);
        builder.Services.AddSingleton<AppDbContext>(_ => null!);
        builder.Services.AddSingleton<WatchlistStore>(_ => null!);
        builder.Services.AddSingleton<WatchlistLibraryResolver>(_ => null!);
        builder.Services.AddSingleton<PlaybackSessionStore>(_ => null!);
        builder.Services.AddSingleton<PlaybackSessionCoordinator>(_ => null!);
        // MapClientApiV1 also maps the device-pairing group (#489); its "approve" handler needs
        // this service resolvable for endpoint metadata even though no handler runs here.
        builder.Services.AddSingleton<DevicePairingStore>(_ => null!);
        var app = builder.Build();
        app.MapClientApiV1();

        var endpoint = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Single(candidate => candidate.RoutePattern.RawText == ClientApiRoutes.Watchlist);

        Assert.IsNull(
            endpoint.Metadata.GetMetadata<IAllowAnonymous>(),
            "GET /watchlist must not allow anonymous access.");
        Assert.IsTrue(
            endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().Count > 0,
            "GET /watchlist must require authorization, the same as /continue-watching and /me/playback-history.");
    }

    [TestMethod]
    public void CapabilitiesAdvertiseTheWatchlistFlag()
    {
        Assert.IsTrue(ClientApiContract.Capabilities().Features.Watchlist);
    }
}
