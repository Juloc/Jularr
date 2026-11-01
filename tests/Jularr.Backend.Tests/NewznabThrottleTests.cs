using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using Jularr.Integrations.Data.Newznab;
using Jularr.Integrations.Logic.Newznab;

namespace Jularr.Backend.Tests
{
    [TestClass]
    public sealed class NewznabThrottleTests
    {
        private const string Results = "<rss><channel><item><title>Release</title></item></channel></rss>";

        [TestMethod]
        public async Task Gate_ParallelCalls_KeepTheirSpacingPerHost()
        {
            var gate = new NewznabGate(TimeSpan.FromMilliseconds(400));
            var clock = Stopwatch.StartNew();

            // Three calls to one indexer go one after another; another indexer is not held back.
            var same = Enumerable.Range(0, 3).Select(_ => gate.WaitAsync("one:443", CancellationToken.None).ContinueWith(_ => clock.ElapsedMilliseconds)).ToArray();
            await gate.WaitAsync("two:443", CancellationToken.None);
            Assert.IsTrue(clock.ElapsedMilliseconds < 200, "Another host starts at once.");

            var turns = (await Task.WhenAll(same)).Order().ToArray();
            Assert.IsTrue(turns[1] - turns[0] >= 300 && turns[2] - turns[1] >= 300, string.Join(", ", turns));
        }

        [TestMethod]
        public async Task Search_RateLimited_ReturnsRetryAfterAndStopsEveryCallToThatHost()
        {
            var handler = new Indexer(request => request.RequestUri!.Host == "free.example.test" ? Xml(Results)
                : new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Headers = { RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMinutes(2)) } });
            var client = new NewznabClient(new HttpClient(handler), new NewznabGate(TimeSpan.Zero));
            var limited = new NewznabConnection("https://limited.example.test", "key");
            var query = new NewznabQuery(NewznabSearchMode.Search, "film", [], [], 100);

            var first = await Assert.ThrowsExactlyAsync<NewznabException>(() => client.SearchAsync(limited, query, CancellationToken.None));
            Assert.AreEqual((NewznabFailure.RateLimited, (TimeSpan?)TimeSpan.FromMinutes(2)), (first.Failure, first.RetryAfter));

            // Search, caps and NZB fetch of that host now refuse without asking; another indexer still answers.
            var second = await Assert.ThrowsExactlyAsync<NewznabException>(() => client.SearchAsync(limited, query, CancellationToken.None));
            var caps = await Assert.ThrowsExactlyAsync<NewznabException>(() => client.ReadCapabilitiesAsync(limited, CancellationToken.None));
            var nzb = await Assert.ThrowsExactlyAsync<NewznabException>(() => client.DownloadNzbAsync(new Uri("https://limited.example.test/getnzb/a"), Path.GetTempFileName(), CancellationToken.None));
            foreach (var refused in new[] { second, caps, nzb })
            {
                Assert.AreEqual(NewznabFailure.RateLimited, refused.Failure);
                Assert.IsTrue(refused.RetryAfter > TimeSpan.FromSeconds(100) && refused.RetryAfter <= TimeSpan.FromMinutes(2), refused.RetryAfter.ToString());
            }

            Assert.AreEqual(1, handler.Requests);
            Assert.AreEqual(1, (await client.SearchAsync(new NewznabConnection("https://free.example.test", "key"), query, CancellationToken.None)).Count);
        }

        [TestMethod]
        public async Task Download_RateLimited_StopsTheCallsWaitingBehindIt()
        {
            var handler = new Indexer(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Headers = { RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(30)) } });
            var client = new NewznabClient(new HttpClient(handler), new NewznabGate(TimeSpan.FromMilliseconds(100)));
            var destination = Path.GetTempFileName();

            // Three fetches start together; the first is refused and the other two never reach the indexer.
            var downloads = Enumerable.Range(0, 3).Select(index => Assert.ThrowsExactlyAsync<NewznabException>(
                () => client.DownloadNzbAsync(new Uri("https://limited.example.test/getnzb/" + index), destination, CancellationToken.None))).ToArray();
            var refused = await Task.WhenAll(downloads);

            Assert.AreEqual(1, handler.Requests);
            Assert.IsTrue(refused.All(exception => exception.Failure == NewznabFailure.RateLimited && exception.RetryAfter is { } wait && wait <= TimeSpan.FromSeconds(30)));
            Assert.AreEqual(1, refused.Count(exception => exception.StatusCode == HttpStatusCode.TooManyRequests));
        }

        [TestMethod]
        public async Task Search_RateLimitedByDateOrWithoutHeader_PausesTheHostAnyway()
        {
            var date = DateTimeOffset.UtcNow.AddSeconds(90);
            var handler = new Indexer(request => request.RequestUri!.Host == "dated.example.test"
                ? new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Headers = { RetryAfter = new RetryConditionHeaderValue(date) } }
                : new HttpResponseMessage(HttpStatusCode.TooManyRequests));
            var client = new NewznabClient(new HttpClient(handler), new NewznabGate(TimeSpan.Zero));
            var query = new NewznabQuery(NewznabSearchMode.Search, "film", [], [], 100);

            var dated = await Assert.ThrowsExactlyAsync<NewznabException>(() => client.SearchAsync(new NewznabConnection("https://dated.example.test", "key"), query, CancellationToken.None));
            Assert.IsTrue(dated.RetryAfter > TimeSpan.FromSeconds(60) && dated.RetryAfter <= TimeSpan.FromSeconds(90), dated.RetryAfter.ToString());

            // Without a header the usual backoff decides, but the host still rests so parallel callers do not hammer it.
            var bare = new NewznabConnection("https://bare.example.test", "key");
            var answered = await Assert.ThrowsExactlyAsync<NewznabException>(() => client.SearchAsync(bare, query, CancellationToken.None));
            Assert.IsNull(answered.RetryAfter);
            var refused = await Assert.ThrowsExactlyAsync<NewznabException>(() => client.SearchAsync(bare, query, CancellationToken.None));
            Assert.AreEqual(NewznabFailure.RateLimited, refused.Failure);
            Assert.AreEqual(2, handler.Requests);
        }

        private static HttpResponseMessage Xml(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/xml") };

        [TestMethod]
        public async Task DownloadNzb_ALinkThatIsGone_IsNotFoundAndNoOutage()
        {
            var client = new NewznabClient(new HttpClient(new Indexer(_ => new HttpResponseMessage(HttpStatusCode.NotFound))), new NewznabGate(TimeSpan.Zero));
            var gone = await Assert.ThrowsExactlyAsync<NewznabException>(() => client.DownloadNzbAsync(new Uri("https://nyaa.example.test/download/1.torrent"), Path.GetTempFileName(), CancellationToken.None));
            Assert.AreEqual(NewznabFailure.NotFound, gone.Failure);

            var broken = new NewznabClient(new HttpClient(new Indexer(_ => new HttpResponseMessage(HttpStatusCode.BadGateway))), new NewznabGate(TimeSpan.Zero));
            var down = await Assert.ThrowsExactlyAsync<NewznabException>(() => broken.DownloadNzbAsync(new Uri("https://nyaa.example.test/download/1.torrent"), Path.GetTempFileName(), CancellationToken.None));
            Assert.AreEqual(NewznabFailure.Unavailable, down.Failure);
        }

        private sealed class Indexer(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
        {
            private int _requests;

            public int Requests => _requests;

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref _requests);
                return Task.FromResult(answer(request));
            }
        }
    }
}
