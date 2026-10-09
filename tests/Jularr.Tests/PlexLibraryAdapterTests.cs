using System.Net;
using System.Text;
using Jularr.Web.Features.ExternalPlayback.Plex;

namespace Jularr.Tests;

[TestClass]
public sealed class PlexLibraryAdapterTests
{
    private static readonly Uri Server =
        new("https://example.plex.direct:32400/");

    [TestMethod]
    public async Task ReadsOnlySupportedSectionsWithScopedToken()
    {
        HttpRequestMessage? observed = null;
        using var client = new HttpClient(new Handler(request =>
        {
            observed = request;
            return Json(
                """
                {"MediaContainer":{"Directory":[
                  {"key":"1","type":"movie","title":"Films"},
                  {"key":"2","type":"show","title":"Shows"},
                  {"key":"3","type":"artist","title":"Music"},
                  {"key":"../secret","type":"movie","title":"Bad"}
                ]}}
                """);
        }));
        var adapter = new PlexLibraryClient(client);

        var sections = await adapter.GetSectionsAsync(
            Server, "private-token", "instance-123", CancellationToken.None);

        Assert.AreEqual(2, sections.Count);
        CollectionAssert.AreEquivalent(
            new[] { "1", "2" }, sections.Select(x => x.Id).ToArray());
        Assert.AreEqual("example.plex.direct", observed!.RequestUri!.Host);
        Assert.AreEqual("private-token",
            observed.Headers.GetValues("X-Plex-Token").Single());
        Assert.AreEqual("instance-123",
            observed.Headers.GetValues("X-Plex-Client-Identifier").Single());
    }

    [TestMethod]
    public async Task ParsesConfirmedExternalIdsWithoutGuessingFromTitle()
    {
        using var client = new HttpClient(new Handler(request =>
        {
            StringAssert.Contains(
                request.RequestUri!.Query,
                "includeGuids=1");
            return Json(
                """
                {"MediaContainer":{"totalSize":1,"Metadata":[
                  {"ratingKey":"734","title":"Dune","type":"movie","year":2021,
                   "Guid":[{"id":"imdb://tt1160419"},{"id":"tmdb://438631"},
                           {"id":"plex://movie/no-match"}]}
                ]}}
                """);
        }));
        var page = await new PlexLibraryClient(client).GetItemsAsync(
            Server, "private-token", "instance-123",
            "1", 0, 100, CancellationToken.None);

        Assert.AreEqual(1, page.TotalSize);
        Assert.AreEqual(1, page.ReturnedSize);
        Assert.AreEqual(1, page.Items.Count);
        Assert.AreEqual("734", page.Items[0].RatingKey);
        Assert.AreEqual("Dune", page.Items[0].Title);
        CollectionAssert.AreEquivalent(
            new[] { "imdb:tt1160419", "tmdb:438631" },
            page.Items[0].ExternalIds.Select(x => $"{x.Provider}:{x.Id}").ToArray());
    }

    [TestMethod]
    public async Task ExactItemReadsPlexSectionFromContainerWhenEntryOmitsIt()
    {
        using var client = new HttpClient(new Handler(request =>
        {
            Assert.AreEqual(
                "/library/metadata/734",
                request.RequestUri!.AbsolutePath);
            return Json(
                """
                {"MediaContainer":{"librarySectionID":29,"Metadata":[
                    {"ratingKey":"734","title":"Dune","type":"movie",
                     "Guid":[{"id":"tmdb://438631"}]}
                ]}}
                """);
        }));

        var item = await new PlexLibraryClient(client).GetItemAsync(
            Server, "profile-private-token", "instance-123", "734",
            CancellationToken.None);

        Assert.IsNotNull(item);
        Assert.AreEqual("29", item.LibrarySectionId);
        Assert.AreEqual("tmdb", item.ExternalIds.Single().Provider);
    }

    [TestMethod]
    public async Task RejectsUnsafePathsAndCredentialsBeforeNetwork()
    {
        var calls = 0;
        using var client = new HttpClient(new Handler(_ =>
        {
            calls++;
            return Json("{}");
        }));
        var adapter = new PlexLibraryClient(client);
        await Assert.ThrowsExactlyAsync<ArgumentException>(() =>
            adapter.GetItemsAsync(
                Server, "private-token", "instance-123",
                "../library", 0, 100, CancellationToken.None));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() =>
            adapter.GetSectionsAsync(
                new Uri("http://127.0.0.1:32400/"),
                "private-token", "instance-123", CancellationToken.None));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() =>
            adapter.GetSectionsAsync(
                Server, "private-token\r\nX-Fake: yes",
                "instance-123", CancellationToken.None));
        Assert.AreEqual(0, calls);
    }

    [TestMethod]
    public async Task RejectsRedirectedPlexResponses()
    {
        using var client = new HttpClient(new Handler(_ =>
            new HttpResponseMessage(HttpStatusCode.TemporaryRedirect)
            {
                Headers = { Location = new Uri("https://evil.example/") }
            }));
        await Assert.ThrowsExactlyAsync<HttpRequestException>(() =>
            new PlexLibraryClient(client).GetSectionsAsync(
                Server, "private-token", "instance-123", CancellationToken.None));
    }

    private static HttpResponseMessage Json(string content) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(content, Encoding.UTF8, "application/json")
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
