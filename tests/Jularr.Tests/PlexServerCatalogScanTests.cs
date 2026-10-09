using System.Net;
using System.Security.Claims;
using System.Text;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.ExternalPlayback.Plex;
using Jularr.Web.Features.MediaCore;
using Microsoft.AspNetCore.DataProtection;

namespace Jularr.Tests;

[TestClass]
public sealed class PlexServerCatalogScanTests
{
    [TestMethod]
    public async Task AdminScan_ReturnsOnlyApprovedPageWithConfirmedMatches()
    {
        var root = Path.Combine(Path.GetTempPath(),
            $"jularr-plex-scan-{Guid.NewGuid():N}");
        try
        {
            await using var db = await MediaCoreTestSupport.CreateDbAsync();
            var works = new WorkService(db);
            var movie = await works.CreateWorkAsync(
                WorkMediaType.Movie, "Dune", 2021, CancellationToken.None);
            await works.LinkExternalIdentityAsync(
                movie.Id, WorkMediaType.Movie, "tmdb", "438631",
                1, "confirmed", true, false,
                MappingReviewState.Confirmed, CancellationToken.None);

            using var http = new HttpClient(new Handler(request =>
            {
                Assert.AreEqual("secret-server-grant",
                    request.Headers.GetValues("X-Plex-Token").Single());
                var isSections = request.RequestUri!.AbsolutePath
                    .EndsWith("/library/sections", StringComparison.Ordinal);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        request.RequestUri.AbsolutePath.EndsWith("/identity",
                            StringComparison.Ordinal)
                            ? """{"MediaContainer":{"machineIdentifier":"plex-machine-1234"}}"""
                            : isSections
                                ? """{"MediaContainer":{"Directory":[{"key":"1","type":"movie","title":"Films"}]}}"""
                                : """{"MediaContainer":{"totalSize":3,"Metadata":[{"ratingKey":"not-valid","type":"movie","title":"Ignored"},{"ratingKey":"44","type":"movie","title":"Dune","Guid":[{"id":"tmdb://438631"}]}]}}""",
                        Encoding.UTF8, "application/json")
                };
            }));

            var grants = new PlexServerGrantStore(
                new EphemeralDataProtectionProvider(), TimeProvider.System, root);
            var client = new PlexLibraryClient(http);
            var server = new PlexServerCandidate(
                "plex-machine-1234", "Plex server", true,
                "secret-server-grant",
                [new PlexServerConnection(new Uri("https://server.plex.direct:32400/"),
                    true, false)]);
            await new PlexServerSelectionService(client, grants).ApproveAsync(
                Principal(AccountRole.Owner), server, server.Connections[0].Url,
                ["1"], "instance-id", CancellationToken.None);

            var scan = new PlexServerCatalogScanService(
                grants, client, new PlexWorkMatcher(db));
            var owner = Principal(AccountRole.Owner);
            var page = await scan.ScanApprovedPageAsync(
                owner, server.MachineIdentifier, "1", 0, 1,
                "instance-id", CancellationToken.None);

            Assert.AreEqual(3, page.TotalSize);
            Assert.AreEqual(2, page.NextStart);
            Assert.AreEqual(1, page.Matches.Count);
            Assert.AreEqual(movie.Id, page.Matches[0].WorkId);

            await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() =>
                scan.ScanApprovedPageAsync(
                    Principal(AccountRole.User), server.MachineIdentifier, "1",
                    0, 1, "instance-id", CancellationToken.None));
            await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() =>
                scan.ScanApprovedPageAsync(
                    owner, server.MachineIdentifier, "2",
                    0, 1, "instance-id", CancellationToken.None));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static ClaimsPrincipal Principal(AccountRole role) =>
        OwnerAuthService.CreatePrincipal(new OwnerAccount
        {
            Id = Guid.NewGuid().ToString("N"),
            UserName = role.ToString(),
            Role = role,
            IsEnabled = true
        });

    private sealed class Handler(
        Func<HttpRequestMessage, HttpResponseMessage> respond)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
