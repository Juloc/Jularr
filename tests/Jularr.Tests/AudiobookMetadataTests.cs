using System.Net;
using System.Text;
using Jularr.Web.Features.Audiobooks;
using Jularr.Web.Features.MediaCore;
using Jularr.Web.Features.Providers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jularr.Tests;

[TestClass]
public sealed class AudiobookMetadataTests
{
    private const string Feed = """
        {"books":[{"id":"52","title":"Frieren","language":"English","totaltimesecs":"3725","url_librivox":"https://librivox.org/frieren/","coverart_jpg":"https://archive.org/download/frieren/frieren.jpg",
          "authors":[{"first_name":"Kanehito","last_name":"Yamada"}],
          "sections":[{"readers":[{"reader_id":"1","display_name":"Reader One"}]},{"readers":[{"reader_id":"1","display_name":"Reader One"},{"reader_id":"2","display_name":"Reader Two"}]}]}]}
        """;

    private static LibriVoxClient Client(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var clock = TimeProvider.System;
        var executor = new ProviderExecutor(new ProviderRateLimiter(), new ProviderHealthTracker(clock), clock, NullLogger<ProviderExecutor>.Instance);
        return new LibriVoxClient(new HttpClient(new StubHandler(responder)), executor);
    }

    [TestMethod]
    public async Task TheFeedGivesNarratorsDurationAndCoverAndNoMatchIsEmptyNotAFailure()
    {
        var queries = new List<string>();
        var client = Client(request =>
        {
            queries.Add(request.RequestUri!.Query);
            return request.RequestUri.Query.Contains("Nothing", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("""{"error":"Audiobooks could not be found"}""", Encoding.UTF8, "application/json") }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Feed, Encoding.UTF8, "application/json") };
        });

        var found = (await client.SearchAsync("Frieren", CancellationToken.None)).Single();

        Assert.AreEqual("52", found.ExternalId);
        CollectionAssert.AreEqual(new[] { "Reader One", "Reader Two" }, found.Narrators.ToArray());
        Assert.AreEqual(3725, found.DurationSeconds);
        Assert.AreEqual("Kanehito Yamada", found.Authors.Single());
        Assert.StartsWith("?title=%5EFrieren", queries[0], "The title search is anchored to the start of the title.");
        Assert.IsEmpty(await client.SearchAsync("Nothing here", CancellationToken.None));
        Assert.IsNull(await client.GetAsync("not-a-number", CancellationToken.None), "Only a numeric project id is looked up.");
    }

    [TestMethod]
    public async Task AConfirmedRecordingIsStoredOnTheAudioEditionOnceAndKeepsAKnownLanguage()
    {
        await using var db = await MediaCoreTestSupport.CreateDbAsync();
        var work = await new WorkService(db).CreateWorkAsync(WorkMediaType.Book, "Frieren", 2020, CancellationToken.None);
        var service = new AudiobookMetadataService(db, [Client(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Feed, Encoding.UTF8, "application/json") })]);

        Assert.IsNotNull(await service.AttachAsync(work.Id, LibriVoxClient.ProviderKey, "52", CancellationToken.None));
        Assert.IsNotNull(await service.AttachAsync(work.Id, LibriVoxClient.ProviderKey, "52", CancellationToken.None));
        Assert.IsNull(await service.AttachAsync(work.Id, "other", "52", CancellationToken.None), "Only a known provider answers.");

        var stored = (await service.GetAsync(work.Id, CancellationToken.None))!;
        Assert.AreEqual(1, await db.AudiobookEditionMetadata.CountAsync());
        Assert.AreEqual(2, stored.Narrators.Length);
        Assert.AreEqual("en", (await db.WorkEditions.AsNoTracking().SingleAsync(edition => edition.WorkId == work.Id)).Language);
        Assert.IsFalse(await db.WorkEditions.AnyAsync(edition => edition.WorkId == work.Id && edition.Format != "audiobook"), "No Book edition is created or touched.");
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(responder(request));
    }
}
