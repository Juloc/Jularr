using System.Net;
using System.Text;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.ExternalPlayback.Plex;
using Jularr.Web.Features.MediaCore;
using Microsoft.AspNetCore.DataProtection;

namespace Jularr.Tests;

[TestClass]
public sealed class PlexItemAccessTests
{
    [TestMethod]
    public async Task ExactItemMustBelongToCurrentUserLibraryAndConfirmedJularrWork()
    {
        var root = Path.Combine(
            Path.GetTempPath(), $"jularr-plex-item-{Guid.NewGuid():N}");
        try
        {
            await using var db = await MediaCoreTestSupport.CreateDbAsync();
            var works = new WorkService(db);
            var movie = await works.CreateWorkAsync(
                WorkMediaType.Movie, "Dune", 2021, CancellationToken.None);
            await works.LinkExternalIdentityAsync(
                movie.Id, WorkMediaType.Movie, "tmdb", "438631",
                1.0, "confirmed", true, false,
                MappingReviewState.Confirmed, CancellationToken.None);

            var server = new Uri("https://plex.example:32400/");
            var tokenLog = new List<(string Path, string Token)>();
            using var http = new HttpClient(new Handler(request =>
            {
                var path = request.RequestUri!.AbsolutePath;
                var token = request.Headers.GetValues("X-Plex-Token").Single();
                tokenLog.Add((path, token));
                if (path == "/identity")
                {
                    return Json(
                        """{"MediaContainer":{"machineIdentifier":"machine-123456"}}""");
                }

                if (path == "/library/sections")
                {
                    return Json(
                        """{"MediaContainer":{"Directory":[{"key":"1","type":"movie","title":"Movies"},{"key":"2","type":"show","title":"Other"}]}}""");
                }

                if (token != "viewer-token")
                {
                    return new HttpResponseMessage(HttpStatusCode.Forbidden);
                }

                return path switch
                {
                    "/library/metadata/44" => Json(
                        """{"MediaContainer":{"Metadata":[{"ratingKey":"44","type":"movie","title":"Dune","librarySectionID":1,"Guid":[{"id":"tmdb://438631"}]}]}}"""),
                    "/library/metadata/45" => Json(
                        """{"MediaContainer":{"Metadata":[{"ratingKey":"45","type":"movie","title":"Dune","librarySectionID":2,"Guid":[{"id":"tmdb://438631"}]}]}}"""),
                    "/library/metadata/46" => Json(
                        """{"MediaContainer":{"Metadata":[{"ratingKey":"46","type":"movie","title":"Wrong identity","librarySectionID":1,"Guid":[{"id":"tmdb://999999"}]}]}}"""),
                    "/library/metadata/47" => Json(
                        """{"MediaContainer":{"Metadata":[{"ratingKey":"47","type":"movie","title":"Unscoped","Guid":[{"id":"tmdb://438631"}]}]}}"""),
                    _ => new HttpResponseMessage(HttpStatusCode.NotFound)
                };
            }));
            var protection = new EphemeralDataProtectionProvider();
            var grants = new PlexServerGrantStore(
                protection, TimeProvider.System, Path.Combine(root, "admin"));
            var client = new PlexLibraryClient(http);
            var candidate = new PlexServerCandidate(
                "machine-123456", "Home", true, "admin-token",
                [new PlexServerConnection(server, true, false)]);
            await new PlexServerSelectionService(
                client, grants).ApproveAsync(
                    Principal(AccountRole.Owner, "owner-profile"),
                    candidate, server, ["1"],
                    "jularr-instance", CancellationToken.None);

            var personal = new PlexProfileConnectionStore(
                protection, Path.Combine(root, "personal"));
            await personal.SaveVerifiedAsync(
                "viewer-profile", "500", "Viewer", "viewer-token");

            var check = new PlexItemAccessService(
                new PlexProfileConnectionService(personal, grants, client),
                personal, grants, client, new PlexWorkMatcher(db));
            var viewer = Principal(AccountRole.User, "viewer-profile");

            Assert.IsTrue(await check.IsAccessibleMatchAsync(
                viewer, "machine-123456", "44",
                movie.Id, "jularr-instance"));
            Assert.IsFalse(await check.IsAccessibleMatchAsync(
                viewer, "machine-123456", "44",
                movie.Id + 1, "jularr-instance"));
            Assert.IsFalse(await check.IsAccessibleMatchAsync(
                viewer, "machine-123456", "45",
                movie.Id, "jularr-instance"));
            Assert.IsFalse(await check.IsAccessibleMatchAsync(
                viewer, "machine-123456", "46",
                movie.Id, "jularr-instance"));
            Assert.IsFalse(await check.IsAccessibleMatchAsync(
                viewer, "machine-123456", "47",
                movie.Id, "jularr-instance"));

            Assert.IsFalse(await check.IsAccessibleMatchAsync(
                Principal(AccountRole.User, "another-profile"),
                "machine-123456", "44", movie.Id, "jularr-instance"));

            Assert.IsTrue(tokenLog
                .Where(x => x.Path.StartsWith("/library/metadata/",
                    StringComparison.Ordinal))
                .All(x => x.Token == "viewer-token"));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static System.Security.Claims.ClaimsPrincipal Principal(
        AccountRole role, string id) =>
        OwnerAuthService.CreatePrincipal(new OwnerAccount
        {
            Id = id,
            UserName = id,
            Role = role,
            IsEnabled = true
        });

    private static HttpResponseMessage Json(string value) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                value, Encoding.UTF8, "application/json")
        };

    private sealed class Handler(
        Func<HttpRequestMessage, HttpResponseMessage> response)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(response(request));
    }
}
