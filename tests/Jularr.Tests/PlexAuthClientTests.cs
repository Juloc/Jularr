using System.Net;
using System.Text;
using Jularr.Web.Features.Plex;

namespace Jularr.Tests;

[TestClass]
public sealed class PlexAuthClientTests
{
    [TestMethod]
    public async Task PinAuthentication_UsesVerifiedStableUserId()
    {
        var observed = new List<(string Path, string? Token)>();
        using var client = new HttpClient(new Handler(request =>
        {
            observed.Add((
                request.RequestUri!.AbsolutePath,
                request.Headers.TryGetValues("X-Plex-Token", out var values)
                    ? values.Single()
                    : null));

            return request.RequestUri.AbsolutePath switch
            {
                "/api/v2/pins" => Json("""{"id":124,"code":"sample-code"}"""),
                "/api/v2/pins/124" => Json("""{"authToken":"synthetic-private-token"}"""),
                "/api/v2/user" => Json("""{"id":500,"username":"viewer"}"""),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            };
        }))
        {
            BaseAddress = new Uri("https://plex.tv/")
        };

        var plex = new PlexAuthClient(client);
        var pin = await plex.CreatePinAsync("jularr-test", CancellationToken.None);
        var accountId = await plex.ResolveAuthenticatedAccountIdAsync(
            pin.Id, "jularr-test", CancellationToken.None);

        Assert.AreEqual(124L, pin.Id);
        Assert.AreEqual("sample-code", pin.Code);
        Assert.AreEqual("500", accountId);
        Assert.AreEqual(3, observed.Count);
        Assert.IsNull(observed[0].Token);
        Assert.IsNull(observed[1].Token);
        Assert.AreEqual("synthetic-private-token", observed[2].Token);
    }

    [TestMethod]
    public async Task UnclaimedPin_DoesNotAuthenticateAnyAccount()
    {
        using var client = new HttpClient(new Handler(_ =>
            Json("""{"id":124,"authToken":null}""")))
        {
            BaseAddress = new Uri("https://plex.tv/")
        };

        var accountId = await new PlexAuthClient(client)
            .ResolveAuthenticatedAccountIdAsync(
                124, "jularr-test", CancellationToken.None);
        Assert.IsNull(accountId);
    }

    [TestMethod]
    public async Task VerifiedProfileGrantKeepsPlexTokenOutOfSerializedIdentity()
    {
        using var client = new HttpClient(new Handler(request =>
            request.RequestUri!.AbsolutePath switch
            {
                "/api/v2/pins/124" => Json(
                    """{"authToken":"verified-profile-secret"}"""),
                "/api/v2/user" => Json(
                    """{"id":500,"username":"viewer"}"""),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            }))
        {
            BaseAddress = new Uri("https://plex.tv/")
        };

        var verified = await new PlexAuthClient(client)
            .ResolveVerifiedIdentityAsync(
                124, "jularr-test", CancellationToken.None);
        Assert.IsNotNull(verified);
        Assert.AreEqual("500", verified.AccountId);
        var json = System.Text.Json.JsonSerializer.Serialize(verified);
        StringAssert.Contains(json, "500");
        Assert.IsFalse(json.Contains(
            "verified-profile-secret", StringComparison.Ordinal));
        Assert.IsFalse(verified.ToString().Contains(
            "verified-profile-secret", StringComparison.Ordinal));
    }

    private static HttpResponseMessage Json(string content) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(content, Encoding.UTF8, "application/json")
        };

    private sealed class Handler(
        Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
