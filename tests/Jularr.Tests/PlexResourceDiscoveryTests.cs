using System.Net;
using System.Text;
using System.Text.Json;
using Jularr.Web.Features.ExternalPlayback.Plex;

namespace Jularr.Tests;

[TestClass]
public sealed class PlexResourceDiscoveryTests
{
    [TestMethod]
    public async Task DiscoversOwnedAndSharedServersWithoutExposingTokens()
    {
        using var http = new HttpClient(new Handler(request =>
        {
            Assert.AreEqual("clients.plex.tv", request.RequestUri!.Host);
            Assert.AreEqual("account-token",
                request.Headers.GetValues("X-Plex-Token").Single());
            return Json(
                """
                [
                  {
                    "provides":"server","name":"Home","owned":true,
                    "clientIdentifier":"machine-123456","accessToken":"private-server-token",
                    "connections":[
                      {"uri":"https://relay.example:32400","local":false,"relay":true},
                      {"uri":"http://192.168.1.25:32400","local":true,"relay":false},
                      {"uri":"https://lan.plex.direct:32400","local":true,"relay":false}
                    ]
                  },
                  {
                    "provides":"server","name":"Shared","owned":false,
                    "clientIdentifier":"machine-987654","accessToken":"shared-server-token",
                    "connections":[
                      {"uri":"https://shared.plex.direct:32400","local":false,"relay":false}
                    ]
                  },
                  {"provides":"player","clientIdentifier":"player-123456","accessToken":"secret"}
                ]
                """);
        }))
        {
            BaseAddress = new Uri("https://clients.plex.tv/")
        };
        var found = await new PlexResourceDiscoveryClient(http)
            .DiscoverAsync("account-token", "jularr-server", CancellationToken.None);

        Assert.AreEqual(2, found.Count);
        Assert.IsTrue(found[0].Owned);
        Assert.IsFalse(found[1].Owned);
        Assert.AreEqual("Home", found[0].Name);
        Assert.AreEqual(2, found[0].Connections.Count);
        Assert.IsTrue(found[0].Connections[0].IsLocal);
        Assert.IsFalse(found[0].Connections[0].IsRelay);

        var json = JsonSerializer.Serialize(found);
        Assert.IsFalse(json.Contains("private-server-token", StringComparison.Ordinal));
        Assert.IsFalse(json.Contains("shared-server-token", StringComparison.Ordinal));
        Assert.IsFalse(json.Contains("account-token", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task UnsafeTokenRejectedBeforeMakingARequest()
    {
        var count = 0;
        using var http = new HttpClient(new Handler(_ =>
        {
            count++;
            return Json("[]");
        }))
        {
            BaseAddress = new Uri("https://clients.plex.tv/")
        };
        await Assert.ThrowsExactlyAsync<ArgumentException>(() =>
            new PlexResourceDiscoveryClient(http).DiscoverAsync(
                "bad\r\nHeader: token", "client", CancellationToken.None));
        Assert.AreEqual(0, count);
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
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
