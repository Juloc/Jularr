using System.Reflection;
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
        "/episodes/{episodeId:guid}/player", "/episodes/{episodeId:guid}/hls", "/episodes/{episodeId:guid}/hls/{sessionId:guid}/{fileName}",
        "/episodes/{episodeId:guid}/fallback", "/episodes/{episodeId:guid}/playback-plan", "/episodes/{episodeId:guid}/offline-download",
        "/episodes/{episodeId:guid}/trickplay", "/episodes/{episodeId:guid}/trickplay/{fileName}", "/episodes/{episodeId:guid}/subtitle-tracks/{trackId}/cues",
        "/media/{mediaFileId:guid}/content", "/media/{mediaFileId:guid}/trickplay", "/media/{mediaFileId:guid}/trickplay/{fileName}",
        "/video/player", "/video/playback-plan", "/video/playback-intents", "/video/progress", "/video/subtitle-tracks/{trackId}/cues",
        "/stream-sessions/{sessionId:guid}", "/stream-sessions/{sessionId:guid}/stream", "/stream-sessions/{sessionId:guid}/hls",
        "/stream-sessions/{sessionId:guid}/hls/{hlsSessionId:guid}/{fileName}", "/stream-sessions/{sessionId:guid}/telemetry",
        "/offline/media/{mediaFileId:guid}/content",
        "/Library/Watch/{workId:guid}/{episodeId:guid?}", "/Library/Episode/{id:guid}", "/Library/Episode/{id:guid}/segments"
    ];

    /// <summary>
    /// Routes that return file bytes but are not video playback and gate themselves per kind or are another media type's: the handler is
    /// found by <see cref="IlCallScanner"/>, so a route that serves files must be named in <see cref="Streaming"/> or here, with its reason.
    /// </summary>
    private static readonly Dictionary<string, string> FileServingNotGated = new()
    {
        ["/offline-media/{kind}/{id:guid}/resources/{resourceId}"] = "serves every offline kind; ClientApiOfflineMediaPackageService refuses the video kinds when Playback is off",
        ["/offline-library/assets/{volumeId:guid}/{asset}"] = "novel volume illustrations (books)"
    };

    /// <summary>
    /// Routes that carry no playable bytes and no player: account, library listings, request state, books, manga and audiobooks, pairing and
    /// companion remote-control state (it carries commands and positions, never media). The offline-media package route serves video kinds too, so its service checks the Playback module itself.
    /// </summary>
    private static readonly string[] NotStreaming =
    [
        "/episodes/{episodeId:guid}", "/episodes/{episodeId:guid}/cues", "/episodes/{episodeId:guid}/segments", "/episodes/{episodeId:guid}/flow",
        "/episodes/{episodeId:guid}/progress", "/episodes/{episodeId:guid}/watched", "/media/{mediaFileId:guid}/availability",
        "/offline/progress", "/offline/prefetch/policy", "/offline/prefetch/plan",
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

    private static async Task<(string Route, RouteEndpoint Endpoint)[]> EnumerateAsync()
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
                .Select(endpoint => (Route: "/" + endpoint.RoutePattern.RawText!.TrimStart('/'), Endpoint: endpoint))
                .Where(x => x.Route.StartsWith(Api, StringComparison.Ordinal) || x.Route.StartsWith("/Library", StringComparison.Ordinal))
                .Select(x => (x.Route.StartsWith(Api, StringComparison.Ordinal) ? x.Route[Api.Length..] : x.Route, x.Endpoint))
                .DistinctBy(x => x.Item1)
        ];
    }

    /// <summary>Whether a handler (or the helpers of its endpoint class) can write file bytes to the response.</summary>
    private static bool ReturnsFileBytes(RouteEndpoint endpoint)
    {
        if (endpoint.Metadata.GetMetadata<MethodInfo>() is not { } handler)
        {
            return false;
        }

        return IlCallScanner.Reach(handler).Any(called =>
            called.DeclaringType is { } type
            && (type.Name is "Results" or "TypedResults" && called.Name is "File" or "Stream" or "PhysicalFile" or "VirtualFile"
                || type.Name == "HttpResponseWritingExtensions"
                || type.Name == "HttpResponse" && called.Name == "get_Body"));
    }

    [TestMethod]
    public async Task EveryRouteThatCanStreamOrLocateMediaIsGatedByThePlaybackModule()
    {
        var endpoints = await EnumerateAsync();
        var routes = endpoints.Select(x => x.Route).ToArray();
        Assert.IsGreaterThan(40, routes.Length, "The enumeration must actually see the routes.");

        var unclassified = routes.Except(Streaming).Except(NotStreaming).Order().ToArray();
        Assert.IsEmpty(unclassified, "Classify each new route as streaming (gate it for the Playback module) or not streaming:\n" + string.Join("\n", unclassified));

        var ungated = Streaming.Where(route => !InstanceModuleRoutes.Resolve(new PathString(route.StartsWith("/Library", StringComparison.Ordinal) ? route : Api + route)).Contains(InstanceModule.Playback)).ToArray();
        Assert.IsEmpty(ungated, "These routes can serve media but stay reachable on a manager-only instance:\n" + string.Join("\n", ungated));

        // The hand lists are checked against the handlers themselves: a route that writes file bytes cannot sit in NotStreaming unnoticed.
        var undeclaredFileRoutes = endpoints.Where(x => ReturnsFileBytes(x.Endpoint) && !Streaming.Contains(x.Route) && !FileServingNotGated.ContainsKey(x.Route)).Select(x => x.Route).Order().ToArray();
        Assert.IsEmpty(undeclaredFileRoutes, "These handlers return file bytes but are neither gated streaming routes nor documented exceptions:\n" + string.Join("\n", undeclaredFileRoutes));
        var flagged = endpoints.Where(x => ReturnsFileBytes(x.Endpoint)).Select(x => x.Route).ToArray();
        foreach (var known in new[] { "/media/{mediaFileId:guid}/content", "/offline-library/assets/{volumeId:guid}/{asset}", "/offline-media/{kind}/{id:guid}/resources/{resourceId}" })
        {
            CollectionAssert.Contains(flagged, known, "The IL scanner must recognise a handler that returns a file.");
        }

        var stale = Streaming.Concat(NotStreaming).Concat(FileServingNotGated.Keys).Except(routes).ToArray();
        Assert.IsEmpty(stale, "Classified routes that no longer exist:\n" + string.Join("\n", stale));
    }
}
