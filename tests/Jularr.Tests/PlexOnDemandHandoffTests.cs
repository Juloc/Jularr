using System.Net;
using System.Security.Claims;
using System.Text;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.ExternalPlayback.Plex;
using Jularr.Web.Features.MediaCore;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;

namespace Jularr.Tests;

[TestClass]
public sealed class PlexOnDemandHandoffTests
{
    [TestMethod]
    public async Task OnDemandMatchUsesBoundedUserSearchAndVerifiesExactCanonicalItem()
    {
        var root = Path.Combine(
            Path.GetTempPath(), $"jularr-plex-on-demand-{Guid.NewGuid():N}");
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

            var calls = new List<(string Path, string Token, string Query)>();
            var failingUserPath = string.Empty;
            var failWithTimeout = false;
            var invalidJson = false;
            using var http = new HttpClient(new Handler(request =>
            {
                var uri = request.RequestUri!;
                var token = request.Headers.GetValues("X-Plex-Token").Single();
                calls.Add((uri.AbsolutePath, token, uri.Query));
                if (token == "user-token" && uri.AbsolutePath == failingUserPath)
                {
                    if (failWithTimeout)
                    {
                        throw new TaskCanceledException("Plex server timed out.");
                    }

                    if (invalidJson)
                    {
                        return Json("{invalid");
                    }

                    return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                }

                if (uri.AbsolutePath == "/identity")
                {
                    return Json(
                        """{"MediaContainer":{"machineIdentifier":"machine-123456"}}""");
                }

                if (uri.AbsolutePath == "/library/sections")
                {
                    return Json(
                        """{"MediaContainer":{"Directory":[{"key":"1","type":"movie","title":"Movies"}]}}""");
                }

                if (uri.AbsolutePath == "/library/sections/1/all")
                {
                    if (token != "user-token")
                    {
                        return new HttpResponseMessage(HttpStatusCode.Forbidden);
                    }
                    return Json(
                        """{"MediaContainer":{"totalSize":1,"Metadata":[{"ratingKey":"44","type":"movie","title":"Dune","librarySectionID":1,"Guid":[{"id":"tmdb://438631"}]}]}}""");
                }

                if (uri.AbsolutePath == "/library/metadata/44")
                {
                    return Json(
                        """{"MediaContainer":{"Metadata":[{"ratingKey":"44","type":"movie","title":"Dune","librarySectionID":1,"Guid":[{"id":"tmdb://438631"}]}]}}""");
                }

                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }));
            var plex = new PlexLibraryClient(http);
            var dp = new EphemeralDataProtectionProvider();
            var grants = new PlexServerGrantStore(
                dp, TimeProvider.System, Path.Combine(root, "admin"));
            var serverUrl = new Uri("https://server.plex.direct:32400/");
            await new PlexServerSelectionService(plex, grants).ApproveAsync(
                Principal(AccountRole.Owner, "owner"),
                new PlexServerCandidate(
                    "machine-123456", "Home", true, "admin-token",
                    [new PlexServerConnection(serverUrl, true, false)]),
                serverUrl, ["1"], "instance-id", CancellationToken.None);

            var personal = new PlexProfileConnectionStore(
                dp, Path.Combine(root, "profiles"));
            await personal.SaveVerifiedAsync(
                "viewer", "12345", null, "user-token");
            var scope = new PlexProfileConnectionService(
                personal, grants, plex);
            var matcher = new PlexWorkMatcher(db);
            var exact = new PlexItemAccessService(
                scope, personal, grants, plex, matcher);
            var browser = new PlexWebDestinationService(exact);
            var lookup = new PlexOnDemandTargetResolver(
                personal, grants, scope, plex, matcher, browser);

            var destination = await lookup.ResolveAsync(
                Principal(AccountRole.User, "viewer"),
                movie.Id, "Dune", "instance-id");

            Assert.IsNotNull(destination);
            Assert.AreEqual("app.plex.tv", destination.Host);
            StringAssert.Contains(
                destination.AbsoluteUri, "machine-123456");
            Assert.IsFalse(destination.AbsoluteUri.Contains(
                "user-token", StringComparison.Ordinal));
            Assert.IsTrue(calls
                .Where(x => x.Path == "/library/sections/1/all")
                .All(x => x.Token == "user-token" &&
                    x.Query.Contains("title=Dune", StringComparison.Ordinal) &&
                    x.Query.Contains("Container-Size=24", StringComparison.Ordinal)));
            Assert.IsTrue(calls.Count <= 8);
            Assert.IsNull(await lookup.ResolveAsync(
                Principal(AccountRole.User, "different-profile"),
                movie.Id, "Dune", "instance-id"));

            foreach (var path in new[]
            {
                "/library/sections",
                "/library/sections/1/all",
                "/library/metadata/44"
            })
            {
                failingUserPath = path;
                Assert.IsNull(await lookup.ResolveAsync(
                    Principal(AccountRole.User, "viewer"),
                    movie.Id, "Dune", "instance-id"));
            }

            failingUserPath = "/library/sections";
            failWithTimeout = true;
            Assert.IsNull(await lookup.ResolveAsync(
                Principal(AccountRole.User, "viewer"),
                movie.Id, "Dune", "instance-id"));

            failWithTimeout = false;
            invalidJson = true;
            foreach (var path in new[]
            {
                "/library/sections",
                "/library/sections/1/all",
                "/library/metadata/44"
            })
            {
                failingUserPath = path;
                Assert.IsNull(await lookup.ResolveAsync(
                    Principal(AccountRole.User, "viewer"),
                    movie.Id, "Dune", "instance-id"));
            }
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    private static ClaimsPrincipal Principal(AccountRole role, string id) =>
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
        Func<HttpRequestMessage, HttpResponseMessage> respond)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
