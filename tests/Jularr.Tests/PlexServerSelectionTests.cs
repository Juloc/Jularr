using System.Net;
using System.Text;
using Jularr.Web.Features.ExternalPlayback.Plex;
using Jularr.Web.Features.Auth;
using System.Security.Claims;
using Microsoft.AspNetCore.DataProtection;

namespace Jularr.Tests;

[TestClass]
public sealed class PlexServerSelectionTests
{
    private static readonly Uri HttpsPlexServer =
        new("https://server.plex.direct:32400/");

    [TestMethod]
    public async Task OwnerSelection_EncryptsGrantAndPreservesOnlyVerifiedLibraries()
    {
        var root = Path.Combine(
            Path.GetTempPath(), $"jularr-plex-grants-{Guid.NewGuid():N}");
        try
        {
            var protector = new EphemeralDataProtectionProvider();
            var store = new PlexServerGrantStore(
                protector, TimeProvider.System, root);
            using var http = CreateClient();
            var picker = new PlexServerSelectionService(
                new PlexLibraryClient(http), store);
            var candidate = Candidate();

            var available = await picker.GetAvailableLibrariesAsync(
                Admin(), candidate, HttpsPlexServer, "jularr-client", CancellationToken.None);
            Assert.AreEqual(2, available.Count);

            var approved = await picker.ApproveAsync(
                Admin(), candidate, HttpsPlexServer, ["1"],
                "jularr-client", CancellationToken.None);

            Assert.AreEqual("machine-123456", approved.MachineIdentifier);
            CollectionAssert.AreEqual(new[] { "1" },
                approved.LibrarySectionIds.ToArray());
            Assert.AreEqual(HttpsPlexServer, approved.Endpoint);

            var text = await File.ReadAllTextAsync(
                Path.Combine(root, "plex-servers.json"));
            Assert.IsFalse(text.Contains("super-private-server-token",
                StringComparison.Ordinal));

            var reloaded = new PlexServerGrantStore(
                protector, TimeProvider.System, root);
            Assert.AreEqual(1, (await reloaded.ListAsync()).Count);

            await reloaded.RemoveAsync("machine-123456");
            Assert.AreEqual(0, (await store.ListAsync()).Count);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [TestMethod]
    public async Task RefusesUnsharedLibraryAndUnverifiedServerBeforeWriting()
    {
        var root = Path.Combine(
            Path.GetTempPath(), $"jularr-plex-grants-{Guid.NewGuid():N}");
        try
        {
            var store = new PlexServerGrantStore(
                new EphemeralDataProtectionProvider(), TimeProvider.System, root);
            using var http = CreateClient();
            var picker = new PlexServerSelectionService(
                new PlexLibraryClient(http), store);

            await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() =>
                picker.ApproveAsync(
                    new ClaimsPrincipal(new ClaimsIdentity()),
                    Candidate(), HttpsPlexServer, ["1"],
                    "jularr-client", CancellationToken.None));

            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                picker.ApproveAsync(
                    Admin(), Candidate(), HttpsPlexServer, ["300"],
                    "jularr-client", CancellationToken.None));
            await Assert.ThrowsExactlyAsync<ArgumentException>(() =>
                picker.ApproveAsync(
                    Admin(), Candidate(), new Uri("https://other.example:32400/"),
                    ["1"], "jularr-client", CancellationToken.None));
            await Assert.ThrowsExactlyAsync<ArgumentException>(() =>
                picker.ApproveAsync(
                    Admin(), Candidate(), HttpsPlexServer, ["1", "1"],
                    "jularr-client", CancellationToken.None));

            Assert.AreEqual(0, (await store.ListAsync()).Count);
            Assert.IsFalse(File.Exists(Path.Combine(root, "plex-servers.json")));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [TestMethod]
    public async Task ServerSelection_MachineIdentityMismatchCannotPersistAnyGrant()
    {
        var directory = Path.Combine(
            Path.GetTempPath(), $"jularr-plex-mismatch-{Guid.NewGuid():N}");
        try
        {
            var store = new PlexServerGrantStore(
                new EphemeralDataProtectionProvider(), TimeProvider.System,
                directory);
            using var http = CreateClient("different-machine-999");
            var picker = new PlexServerSelectionService(
                new PlexLibraryClient(http), store);

            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                picker.ApproveAsync(
                    Admin(), Candidate(), HttpsPlexServer, ["1"],
                    "jularr-client", CancellationToken.None));

            Assert.AreEqual(0, (await store.ListAsync()).Count);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private static ClaimsPrincipal Admin() =>
        OwnerAuthService.CreatePrincipal(new OwnerAccount
        {
            Id = Guid.NewGuid().ToString("N"),
            UserName = "owner",
            Role = AccountRole.Owner,
            IsEnabled = true
        });

    private static PlexServerCandidate Candidate() =>
        new("machine-123456", "Home server", true,
            "super-private-server-token",
            [new PlexServerConnection(HttpsPlexServer, true, false)]);

    private static HttpClient CreateClient(string machineId = "machine-123456") => new(new Handler(request =>
    {
        Assert.AreEqual(HttpsPlexServer.Host, request.RequestUri!.Host);
        Assert.AreEqual("super-private-server-token",
            request.Headers.GetValues("X-Plex-Token").Single());
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                request.RequestUri.AbsolutePath.EndsWith("/identity", StringComparison.Ordinal)
                    ? "{\\\"MediaContainer\\\":{\\\"machineIdentifier\\\":\\\"" + machineId + "\\\"}}"
                    : """
                {"MediaContainer":{"Directory":[
                  {"key":"1","type":"movie","title":"Movies"},
                  {"key":"2","type":"show","title":"Series"},
                  {"key":"3","type":"artist","title":"Music"}
                ]}}
                """,
                Encoding.UTF8, "application/json")
        };
    }));

    private sealed class Handler(
        Func<HttpRequestMessage, HttpResponseMessage> callback)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(callback(request));
    }
}
