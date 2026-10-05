using Jularr.Web.Features.ClientApi;
using Jularr.Web.Features.Instance;
using Jularr.Web.Pages.Library;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Jularr.Tests;

/// <summary>
/// A manager-only instance (Playback off) has no way to play media. Every client API route and Library page is enumerated from the real
/// endpoint data source and must be classified here: a route that can return video or audio bytes, resolve a playable file or start
/// ffmpeg is gated by <see cref="InstanceModuleRoutes"/>, any other route is listed as not streaming. A new route that is in neither list
/// fails the test, so it cannot be forgotten.
/// </summary>
[TestClass]
public sealed class InstantPlayPlaybackGateTests
{
    private const string Api = ClientApiContract.BasePath;

    /// <summary>Routes that stream or locate media for playing. Exact route templates, relative to the client API base path unless they start with /Library.</summary>
    private static readonly string[] Streaming =
    [
        "/episodes/{episodeId:guid}/player", "/episodes/{episodeId:guid}/hls", "/episodes/{episodeId:guid}/fallback", "/episodes/{episodeId:guid}/playback-plan",
        "/episodes/{episodeId:guid}/offline-download", "/episodes/{episodeId:guid}/trickplay", "/episodes/{episodeId:guid}/trickplay/{fileName}",
        "/episodes/{episodeId:guid}/hls/{sessionId:guid}/{fileName}", "/episodes/{episodeId:guid}/cues", "/episodes/{episodeId:guid}/subtitle-tracks/{trackId}/cues", "/episodes/{episodeId:guid}/segments",
        "/episodes/{episodeId:guid}", "/episodes/{episodeId:guid}/flow", "/episodes/{episodeId:guid}/progress", "/episodes/{episodeId:guid}/watched",
        "/media/{mediaFileId:guid}/content", "/media/{mediaFileId:guid}/availability", "/media/{mediaFileId:guid}/trickplay", "/media/{mediaFileId:guid}/trickplay/{fileName}",
        "/video/player", "/video/playback-plan", "/video/playback-intents", "/video/progress", "/video/subtitle-tracks/{trackId}/cues",
        "/stream-sessions/{sessionId:guid}", "/stream-sessions/{sessionId:guid}/stream", "/stream-sessions/{sessionId:guid}/hls",
        "/stream-sessions/{sessionId:guid}/hls/{hlsSessionId:guid}/{fileName}", "/stream-sessions/{sessionId:guid}/telemetry",
        "/offline/media/{mediaFileId:guid}/content", "/offline/prefetch/policy", "/offline/prefetch/plan", "/offline/progress",
        "/Library/Watch/{workId:guid}/{episodeId:guid?}", "/Library/Episode/{id:guid}", "/Library/Episode/{id:guid}/segments"
    ];

    /// <summary>
    /// Routes that carry no playable bytes and no player: account, library listings, request state, books, manga and audiobooks, pairing and
    /// companion remote-control state (it carries commands and positions, never media). The offline-media package route serves video kinds too, so its service checks the Playback module itself.
    /// </summary>
    private static readonly string[] NotStreaming =
    [
        "/capabilities", "/session/login", "/session/logout", "/me", "/library", "/anime/{animeId:guid}", "/continue-watching", "/watchlist",
        "/me/playback-preferences", "/me/playback-history", "/me/tts-preferences", "/speech/models", "/terms/{termId:guid}", "/terms/{termId:guid}/state",
        "/library-roots/{rootId:guid}/availability", "/library-roots/{rootId:guid}/test", "/library-roots/{rootId:guid}/wake", "/requests/{requestId:guid}",
        "/offline-library/works/{workId:guid}/manifest", "/offline-library/chapters/{chapterId:guid}", "/offline-library/assets/{volumeId:guid}/{asset}", "/offline-library/sync",
        "/offline/packages/options", "/offline/packages/preview",
        "/offline-media/{kind}/{id:guid}/manifest", "/offline-media/{kind}/{id:guid}/resources/{resourceId}",
        "/Library", "/Library/Anime/{id:guid}", "/Library/AnimeRepair/{id:guid}", "/Library/Movie/{workId:guid}", "/Library/Series/{workId:guid}",
        "/Library/Index", "/Library/PresentationGroups/{id:guid}", "/Library/Rename/{id:guid}",
        "/pairing/start", "/pairing/poll", "/pairing/approve",
        "/playback-sessions/", "/playback-sessions/pair", "/playback-sessions/{sessionId:guid}", "/playback-sessions/{sessionId:guid}/commands", "/playback-sessions/{sessionId:guid}/pairing",
        "/playback-sessions/{sessionId:guid}/participant-state", "/playback-sessions/{sessionId:guid}/revoke", "/playback-sessions/{sessionId:guid}/state"
    ];

    private static async Task<string[]> EnumerateRoutesAsync()
    {
        using var host = await new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .UseContentRoot(AppContext.BaseDirectory)
                .ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddAuthorization();
                    // Only the route table is read: registering every application type lets handler parameters be inferred as services.
                    foreach (var type in typeof(MovieDetailModel).Assembly.GetExportedTypes().Where(x => (x.IsInterface || x.IsClass) && !x.IsAbstract && !x.IsGenericTypeDefinition || x.IsInterface && !x.IsGenericTypeDefinition))
                    {
                        services.AddSingleton(type, _ => null!);
                    }
                    services.AddRazorPages().AddApplicationPart(typeof(MovieDetailModel).Assembly);
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapRazorPages();
                        endpoints.MapClientApiV1();
                        endpoints.MapClientApiPlaybackPlanV1();
                        endpoints.MapClientApiPlaybackIntentsV1();
                        endpoints.MapClientApiOfflineV1();
                        endpoints.MapClientApiOfflineMediaPackageV1();
                        endpoints.MapClientApiOfflinePackagesV1();
                        endpoints.MapClientApiOfflineLibraryV1();
                        endpoints.MapClientApiOfflinePrefetchV1();
                    });
                }))
            .StartAsync();

        return
        [
            .. host.Services.GetRequiredService<EndpointDataSource>().Endpoints
                .OfType<RouteEndpoint>()
                .Select(endpoint => "/" + endpoint.RoutePattern.RawText!.TrimStart('/'))
                .Where(route => route.StartsWith(Api, StringComparison.Ordinal) || route.StartsWith("/Library", StringComparison.Ordinal))
                .Select(route => route.StartsWith(Api, StringComparison.Ordinal) ? route[Api.Length..] : route)
                .Distinct()
        ];
    }

    [TestMethod]
    public async Task EveryRouteThatCanStreamOrLocateMediaIsGatedByThePlaybackModule()
    {
        var routes = await EnumerateRoutesAsync();
        Assert.IsGreaterThan(40, routes.Length, "The enumeration must actually see the routes.");

        var unclassified = routes.Except(Streaming).Except(NotStreaming).Order().ToArray();
        Assert.IsEmpty(unclassified, "Classify each new route as streaming (gate it for the Playback module) or not streaming:\n" + string.Join("\n", unclassified));

        var ungated = Streaming.Where(route => !InstanceModuleRoutes.Resolve(new PathString(route.StartsWith("/Library", StringComparison.Ordinal) ? route : Api + route)).Contains(InstanceModule.Playback)).ToArray();
        Assert.IsEmpty(ungated, "These routes can serve media but stay reachable on a manager-only instance:\n" + string.Join("\n", ungated));

        var stale = Streaming.Concat(NotStreaming).Except(routes).ToArray();
        Assert.IsEmpty(stale, "Classified routes that no longer exist:\n" + string.Join("\n", stale));
    }
}
