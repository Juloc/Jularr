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
    public async Task ExactItemRejectsConflictingOrMalformedSectionClaims()
    {
        var responses = new[]
        {
            """
            {"MediaContainer":{"librarySectionID":2,"Metadata":[
              {"ratingKey":"734","title":"Dune","type":"movie","librarySectionID":1,
               "Guid":[{"id":"tmdb://438631"}]}
            ]}}
            """,
            """
            {"MediaContainer":{"librarySectionID":"invalid","Metadata":[
              {"ratingKey":"734","title":"Dune","type":"movie","librarySectionID":1,
               "Guid":[{"id":"tmdb://438631"}]}
            ]}}
            """,
            """
            {"MediaContainer":{"librarySectionID":1,"Metadata":[
              {"ratingKey":"734","title":"Dune","type":"movie","Guid":[{"id":"tmdb://438631"}]},
              {"ratingKey":"734","title":"Dune","type":"movie","Guid":[{"id":"tmdb://438631"}]}
            ]}}
            """,
            """
            {"MediaContainer":{"librarySectionID":29,"Metadata":[
              {"ratingKey":"734","title":"Dune","type":"movie","librarySectionID":"invalid",
               "Guid":[{"id":"tmdb://438631"}]}
            ]}}
            """,
            """
            {"MediaContainer":{"librarySectionID":1,"Metadata":[
              {"ratingKey":"734","title":"Dune","type":"movie","Guid":[{"id":"tmdb://438631"}]},
              {"ratingKey":"734","type":"unsupported"}
            ]}}
            """
        };

        foreach (var response in responses)
        {
            using var client = new HttpClient(new Handler(_ => Json(response)));
            var item = await new PlexLibraryClient(client).GetItemAsync(
                Server, "private-token", "instance-123", "734", CancellationToken.None);
            Assert.IsNull(item);
        }
    }

    [TestMethod]
    public async Task MalformedMetadataEntriesCannotCrashTheProviderParser()
    {
        foreach (var response in new[] { "[]", """{"MediaContainer":null}""" })
        {
            using var client = new HttpClient(new Handler(_ => Json(response)));
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
                new PlexLibraryClient(client).GetSectionsAsync(
                    Server, "private-token", "instance-123", CancellationToken.None));
        }

        using var metadataClient = new HttpClient(new Handler(_ => Json(
            """
            {"MediaContainer":{"totalSize":4,"Metadata":[
              null,42,"not-an-item",
              {"ratingKey":"734","title":"Dune","type":"movie",
               "Guid":[null,42,"not-a-guid",{"id":"tmdb://438631"}]}
            ]}}
            """)));
        var page = await new PlexLibraryClient(metadataClient).GetItemsAsync(
            Server, "private-token", "instance-123", "1", 0, 4,
            CancellationToken.None);
        Assert.AreEqual(1, page.Items.Count);
        Assert.AreEqual("tmdb", page.Items[0].ExternalIds.Single().Provider);
    }

    [TestMethod]
    public async Task OversizedGuidListCannotHideConflictingIdentity()
    {
        var guids = Enumerable.Range(1, 65)
            .Select(id => new { id = $"tmdb://{id}" })
            .ToArray();
        var payload = System.Text.Json.JsonSerializer.Serialize(new
        {
            MediaContainer = new
            {
                totalSize = 1,
                Metadata = new[] { new { ratingKey = "734", type = "movie", Guid = guids } }
            }
        });
        using var client = new HttpClient(new Handler(_ => Json(payload)));

        var page = await new PlexLibraryClient(client).GetItemsAsync(
            Server, "private-token", "instance-123", "1", 0, 10, CancellationToken.None);
        Assert.AreEqual(0, page.Items.Count);
        Assert.AreEqual(1, page.ReturnedSize);
    }

    [TestMethod]
    public async Task LibraryPageRejectsServerResponseLargerThanPageSize()
    {
        using var client = new HttpClient(new Handler(_ => Json(
            """
            {"MediaContainer":{"totalSize":3,"Metadata":[
              {"ratingKey":"1","type":"movie"},
              {"ratingKey":"2","type":"movie"},
              {"ratingKey":"3","type":"movie"}
            ]}}
            """)));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            new PlexLibraryClient(client).GetItemsAsync(
                Server, "private-token", "instance-123", "1", 0, 2, CancellationToken.None));
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
    public async Task ServerTimeoutAlsoCancelsResponseBody()
    {
        using var client = new HttpClient(new Handler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StallingContent()
            }))
        {
            Timeout = TimeSpan.FromMilliseconds(150)
        };
        using var outer = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var timedOut = false;
        try
        {
            await new PlexLibraryClient(client).GetSectionsAsync(
                Server, "private-token", "instance-123", outer.Token);
        }
        catch (OperationCanceledException)
        {
            timedOut = true;
        }

        Assert.IsTrue(timedOut);
        Assert.IsFalse(outer.IsCancellationRequested);
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

    private sealed class StallingContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(
            Stream stream, TransportContext? context) =>
            Task.Delay(TimeSpan.FromSeconds(10));

        protected override Task SerializeToStreamAsync(
            Stream stream, TransportContext? context,
            CancellationToken cancellationToken) =>
            Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

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
