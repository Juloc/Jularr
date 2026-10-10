using System.Net;
using System.Text;
using Jularr.Web.Features.ExternalPlayback.Jellyfin;

namespace Jularr.Tests;

[TestClass]
public sealed class JellyfinLibraryClientTests
{
    private static readonly Guid ServerId = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly Guid ViewerId = Guid.Parse("22222222-3333-4444-5555-666666666666");
    private static readonly Guid MoviesId = Guid.Parse("33333333-4444-5555-6666-777777777777");
    private static readonly Guid MovieId = Guid.Parse("44444444-5555-6666-7777-888888888888");
    private static readonly Guid SeriesId = Guid.Parse("55555555-6666-7777-8888-999999999999");

    [TestMethod]
    public async Task UserScopedCatalogUsesExplicitOrderAndNeverIncludesTokenInUrl()
    {
        var requested = new List<(string Path, string? Token)>();
        using var http = Client(request =>
        {
            var path = request.RequestUri!.PathAndQuery;
            var token = request.Headers.TryGetValues("X-Emby-Token", out var tokens)
                ? tokens.Single() : null;
            requested.Add((path, token));
            return request.RequestUri.AbsolutePath switch
            {
                "/jellyfin/System/Info/Public" => Json($$"""
                    {"Id":"{{ServerId:N}}","ServerName":"Local Jellyfin"}
                    """),
                "/jellyfin/Users/Me" => Json($$"""
                    {"Id":"{{ViewerId:N}}","Name":"Profile"}
                    """),
                var p when p.EndsWith("/Views", StringComparison.Ordinal) =>
                    Json($$"""
                    {"Items":[{"Id":"{{MoviesId:N}}","Name":"Movies","CollectionType":"movies"},
                              {"Id":"{{SeriesId:N}}","Name":"Series","CollectionType":"tvshows"}]}
                    """),
                var p when p.EndsWith("/Items", StringComparison.Ordinal) =>
                    Json($$"""
                    {"TotalRecordCount":1,"Items":[{"Id":"{{MovieId:N}}","Type":"Movie",
                      "Name":"Example","ProviderIds":{"Tmdb":"550","Imdb":"tt0137523"}}]}
                    """),
                var p when p.EndsWith($"/Items/{MovieId:N}", StringComparison.Ordinal) =>
                    Json($$"""
                    {"Id":"{{MovieId:N}}","Type":"Movie","Name":"Example",
                     "ProviderIds":{"Tmdb":"550","Imdb":"tt0137523"}}
                    """),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            };
        });
        var client = new JellyfinLibraryClient(http);
        var endpoint = new Uri("https://jellyfin.example/jellyfin/");

        var identity = await client.GetServerIdentityAsync(endpoint, CancellationToken.None);
        Assert.AreEqual(ServerId, identity.Id);
        Assert.AreEqual("Local Jellyfin", identity.Name);

        var viewer = await client.GetViewerAsync(endpoint, "private-viewer-token", CancellationToken.None);
        Assert.AreEqual(ViewerId, viewer.Id);

        var views = await client.GetViewsAsync(endpoint, "private-viewer-token", ViewerId, CancellationToken.None);
        Assert.AreEqual(2, views.Count);
        Assert.AreEqual(MoviesId, views[0].Id);

        var page = await client.GetItemsAsync(
            endpoint, "private-viewer-token", ViewerId, MoviesId, 10, 25, CancellationToken.None);
        Assert.AreEqual(1, page.TotalRecordCount);
        Assert.AreEqual(1, page.Items.Count);
        Assert.AreEqual("550", page.Items[0].ProviderIds["tmdb"]);

        var item = await client.GetItemAsync(
            endpoint, "private-viewer-token", ViewerId, MovieId, CancellationToken.None);
        Assert.IsNotNull(item);
        Assert.AreEqual(MovieId, item.Id);
        Assert.AreEqual("tt0137523", item.ProviderIds["imdb"]);

        Assert.AreEqual(5, requested.Count);
        Assert.IsNull(requested[0].Token);
        Assert.IsTrue(requested.Skip(1).All(x => x.Token == "private-viewer-token"));
        Assert.IsTrue(requested.All(x => !x.Path.Contains("private-viewer-token", StringComparison.Ordinal)));
        Assert.IsTrue(requested.Any(x => x.Path.Contains(
            "StartIndex=10&Limit=25&Recursive=true&IncludeItemTypes=Movie,Series"
                + "&Fields=ProviderIds&SortBy=SortName&SortOrder=Ascending",
            StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task UnverifiedEndpointAndInvalidTokenAreRejectedBeforeHttp()
    {
        var calls = 0;
        using var http = Client(_ =>
        {
            calls++;
            return Json("{}");
        });
        var client = new JellyfinLibraryClient(http);

        await Assert.ThrowsExactlyAsync<ArgumentException>(() =>
            client.GetViewerAsync(new Uri("http://localhost:8096"), "token", CancellationToken.None));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() =>
            client.GetViewerAsync(new Uri("https://viewer:password@jellyfin.example"),
                "token", CancellationToken.None));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() =>
            client.GetViewerAsync(new Uri("https://jellyfin.example/?secret=x"), "token", CancellationToken.None));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() =>
            client.GetViewerAsync(new Uri("https://jellyfin.example/"),
                "bad\r\nInjected: header", CancellationToken.None));
        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(() =>
            client.GetItemsAsync(new Uri("https://jellyfin.example/"),
                "token", ViewerId, MoviesId, 0, 101, CancellationToken.None));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() =>
            client.GetViewsAsync(new Uri("https://jellyfin.example/"),
                "token", Guid.Empty, CancellationToken.None));
        Assert.AreEqual(0, calls);
    }

    [TestMethod]
    public async Task RedirectsAndForgedItemIdentityCannotProduceAUserMatch()
    {
        using var redirectHttp = Client(_ => new HttpResponseMessage(HttpStatusCode.Redirect)
        {
            Headers = { Location = new Uri("https://attacker.example") }
        });
        await Assert.ThrowsExactlyAsync<HttpRequestException>(() =>
            new JellyfinLibraryClient(redirectHttp).GetViewerAsync(
                new Uri("https://jellyfin.example/"), "token", CancellationToken.None));

        using var mismatchedHttp = Client(_ => Json($$"""
            {"Id":"{{SeriesId:N}}","Type":"Movie","Name":"Wrong item"}
            """));
        var mismatched = await new JellyfinLibraryClient(mismatchedHttp).GetItemAsync(
            new Uri("https://jellyfin.example/"), "token",
            ViewerId, MovieId, CancellationToken.None);
        Assert.IsNull(mismatched);
    }

    private static HttpClient Client(Func<HttpRequestMessage, HttpResponseMessage> reply) =>
        new(new TestHandler(reply)) { Timeout = TimeSpan.FromSeconds(10) };

    private static HttpResponseMessage Json(string content) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(content, Encoding.UTF8, "application/json")
        };

    private sealed class TestHandler(Func<HttpRequestMessage, HttpResponseMessage> reply)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(reply(request));
    }
}
