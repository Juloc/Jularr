using System.Net;
using System.Security.Claims;
using System.Text;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.ExternalPlayback.Plex;
using Microsoft.AspNetCore.DataProtection;

namespace Jularr.Tests;

[TestClass]
public sealed class PlexProfileAccessTests
{
    [TestMethod]
    public async Task UserCannotInheritAdminLibrariesOrUseAnotherProfileConnection()
    {
        var root = Path.Combine(
            Path.GetTempPath(), $"jularr-plex-access-{Guid.NewGuid():N}");
        try
        {
            var protection = new EphemeralDataProtectionProvider();
            var adminToken = "admin-server-grant";
            var seenTokens = new List<string>();
            var server = new Uri("https://server.plex.direct:32400/");
            using var http = new HttpClient(new Handler(request =>
            {
                var token = request.Headers.GetValues("X-Plex-Token").Single();
                seenTokens.Add(token);
                if (request.RequestUri!.AbsolutePath == "/identity")
                {
                    return Json(
                        """{"MediaContainer":{"machineIdentifier":"machine-123456"}}""");
                }

                if (token == adminToken)
                {
                    return Json(
                        """{"MediaContainer":{"Directory":[{"key":"1","type":"movie","title":"Movies"},{"key":"2","type":"show","title":"Series"}]}}""");
                }

                if (token == "user-one-token")
                {
                    return Json(
                        """{"MediaContainer":{"Directory":[{"key":"1","type":"movie","title":"Movies"}]}}""");
                }

                return new HttpResponseMessage(HttpStatusCode.Forbidden);
            }));
            var plex = new PlexLibraryClient(http);
            var adminGrants = new PlexServerGrantStore(
                protection, TimeProvider.System, root);
            var candidate = new PlexServerCandidate(
                "machine-123456",
                "Home",
                true,
                adminToken,
                [new PlexServerConnection(server, true, false)]);
            await new PlexServerSelectionService(
                plex, adminGrants).ApproveAsync(
                    Principal(AccountRole.Owner, "admin-profile"),
                    candidate,
                    server,
                    ["1", "2"],
                    "jularr-instance",
                    CancellationToken.None);

            var profiles = new PlexProfileConnectionStore(
                protection, Path.Combine(root, "profiles"));
            await profiles.SaveVerifiedAsync(
                "user-one", "123", "viewer",
                "user-one-token");
            await profiles.SaveVerifiedAsync(
                "user-two", "456", "viewer-two",
                "expired-user-token");

            var access = new PlexProfileConnectionService(
                profiles, adminGrants, plex);
            var visible = await access.GetAccessibleLibrariesAsync(
                Principal(AccountRole.User, "user-one"),
                "machine-123456",
                "jularr-instance");

            Assert.AreEqual(1, visible.Count);
            Assert.AreEqual("1", visible[0].Id);
            Assert.AreEqual("user-one-token", seenTokens.Last());
            Assert.IsFalse(visible.Any(section => section.Id == "2"));

            var other = await access.GetAccessibleLibrariesAsync(
                Principal(AccountRole.User, "user-two"),
                "machine-123456",
                "jularr-instance");
            Assert.AreEqual(0, other.Count);

            await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() =>
                access.GetAccessibleLibrariesAsync(
                    new ClaimsPrincipal(new ClaimsIdentity()),
                    "machine-123456", "jularr-instance"));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task RemovingCurrentConnectionCannotRemoveAnotherProfileOrAdminServer()
    {
        var root = Path.Combine(
            Path.GetTempPath(), $"jularr-plex-current-{Guid.NewGuid():N}");
        try
        {
            var protection = new EphemeralDataProtectionProvider();
            var profiles = new PlexProfileConnectionStore(protection, root);
            await profiles.SaveVerifiedAsync("user-one", "123", null, "first-token");
            await profiles.SaveVerifiedAsync("user-two", "456", null, "second-token");
            var access = new PlexProfileConnectionService(
                profiles,
                new PlexServerGrantStore(protection, TimeProvider.System,
                    Path.Combine(root, "admin")),
                new PlexLibraryClient(new HttpClient()));

            Assert.IsTrue(await access.DisconnectCurrentAsync(
                Principal(AccountRole.User, "user-one")));
            Assert.IsNull(await access.GetCurrentStatusAsync(
                Principal(AccountRole.User, "user-one")));
            Assert.IsNotNull(await access.GetCurrentStatusAsync(
                Principal(AccountRole.User, "user-two")));
            await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() =>
                access.DisconnectCurrentAsync(new ClaimsPrincipal()));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
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

    private static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                json, Encoding.UTF8, "application/json")
        };

    private sealed class Handler(
        Func<HttpRequestMessage, HttpResponseMessage> callback)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(callback(request));
    }
}
