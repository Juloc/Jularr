using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Jularr.Integrations.Data.AnnasArchive;
using Jularr.Integrations.Data.Browser;
using Jularr.Integrations.Logic.AnnasArchive;
using Jularr.Integrations.Logic.Common;

namespace Jularr.Backend.Tests
{
    [TestClass]
    public sealed class AnnasArchiveSearchTests
    {
        [TestMethod]
        [DataRow("search-list.html")]
        [DataRow("search-table.html")]
        public async Task Search_ObservedLayouts_PreserveMetadata(string fixture)
        {
            using var http = new HttpClient(new Pages(_ => Html(Fixture(fixture))));
            var client = new AnnasArchiveClient(http, new NoSourceGateResolver(), new Browser());
            var record = (await client.SearchAsync(new Uri("https://anna.example.test/"), "Austen", CancellationToken.None)).Single();
            Assert.AreEqual("Pride and Prejudice", record.Title);
            Assert.AreEqual("Jane Austen", record.Author);
            Assert.AreEqual("epub", record.Extension);
            Assert.AreEqual("en", record.Language);
            Assert.AreEqual(524288L, record.SizeBytes);
            Assert.AreEqual("2bb80dfcf9d670598bd71910e1001ecc", record.Md5);
        }

        [TestMethod]
        public async Task Search_ConfirmedEmpty_DoesNotTryOtherMirrors()
        {
            var pages = new Pages(_ => Html(Fixture("search-empty.html")));
            using var http = new HttpClient(pages);
            var client = new AnnasArchiveClient(http, new NoSourceGateResolver(), new Browser());
            Assert.AreEqual(0, (await client.SearchAsync(new Uri("https://annas-archive.gl/"), "absent", CancellationToken.None)).Count);
            Assert.AreEqual(1, pages.Calls.Count);
        }

        [TestMethod]
        [DataRow("<html><title>Domain for sale</title><body>Buy this domain</body></html>")]
        [DataRow("<html><title>Search - Anna’s Archive</title><body>Loading results</body></html>")]
        [DataRow("<html><span>No files found.</span></html>")]
        public async Task Search_UnreadablePage_IsNotReportedAsEmpty(string html)
        {
            using var http = new HttpClient(new Pages(_ => Html(html)));
            var client = new AnnasArchiveClient(http, new NoSourceGateResolver(), new Browser());
            var error = await Assert.ThrowsExactlyAsync<AnnasArchiveException>(() => client.SearchAsync(new Uri("https://anna.example.test/"), "book", CancellationToken.None));
            Assert.AreEqual(AnnasArchiveFailure.InvalidResponse, error.Failure);
        }

        [TestMethod]
        public async Task Search_ProtectionMentionOnFinishedPage_DoesNotStartBrowser()
        {
            using var http = new HttpClient(new Pages(_ => Html(Fixture("search-list.html") + "<!-- DDOS-GUARD challenge-platform -->")));
            var browser = new Browser();
            var client = new AnnasArchiveClient(http, new NoSourceGateResolver(), browser);
            Assert.AreEqual(1, (await client.SearchAsync(new Uri("https://anna.example.test/"), "book", CancellationToken.None)).Count);
            Assert.AreEqual(0, browser.Reads);
        }

        [TestMethod]
        [DataRow(429, (int)AnnasArchiveFailure.RateLimited)]
        [DataRow(503, (int)AnnasArchiveFailure.Unavailable)]
        [DataRow(403, (int)AnnasArchiveFailure.Refused)]
        public async Task Search_HttpFailure_PreservesReason(int status, int expected)
        {
            using var http = new HttpClient(new Pages(_ => new HttpResponseMessage((HttpStatusCode)status)));
            var browser = new Browser();
            var client = new AnnasArchiveClient(http, new NoSourceGateResolver(), browser);
            var error = await Assert.ThrowsExactlyAsync<AnnasArchiveException>(() => client.SearchAsync(new Uri("https://anna.example.test/"), "book", CancellationToken.None));
            Assert.AreEqual((AnnasArchiveFailure)expected, error.Failure);
            Assert.AreEqual(0, browser.Reads);
        }

        [TestMethod]
        public async Task Search_Timeout_IsDistinctFromCallerCancellation()
        {
            using var http = new HttpClient(new Pages(_ => throw new TaskCanceledException()));
            var client = new AnnasArchiveClient(http, new NoSourceGateResolver(), new Browser());
            var error = await Assert.ThrowsExactlyAsync<AnnasArchiveException>(() => client.SearchAsync(new Uri("https://anna.example.test/"), "book", CancellationToken.None));
            Assert.AreEqual(AnnasArchiveFailure.TimedOut, error.Failure);
            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => client.SearchAsync(new Uri("https://anna.example.test/"), "book", canceled.Token));
        }

        [TestMethod]
        public async Task Search_ParkedMirror_UsesAndRemembersHealthyMirror()
        {
            var pages = new Pages(uri => Html(uri.Host == "annas-archive.gl" ? "<title>Domain for sale</title>" : Fixture("search-list.html")));
            using var http = new HttpClient(pages);
            var client = new AnnasArchiveClient(http, new NoSourceGateResolver(), new Browser());
            var site = new Uri("https://annas-archive.gl/");
            Assert.AreEqual("annas-archive.pk", (await client.SearchAsync(site, "first", CancellationToken.None)).Single().Site.Host);
            await client.SearchAsync(site, "second", CancellationToken.None);
            Assert.AreEqual(1, pages.Calls.Count(uri => uri.Host == "annas-archive.gl"));
            Assert.AreEqual(2, pages.Calls.Count(uri => uri.Host == "annas-archive.pk"));
        }

        [TestMethod]
        public async Task Search_CooldownExpires_ProbeCanRecover()
        {
            var time = new Clock();
            var succeeds = false;
            var pages = new Pages(_ => Html(succeeds ? Fixture("search-list.html") : "<title>Domain for sale</title>"));
            using var http = new HttpClient(pages);
            var client = new AnnasArchiveClient(http, new NoSourceGateResolver(), new Browser(), new AnnasArchiveMirrors(time));
            var site = new Uri("https://anna.example.test/");
            await Assert.ThrowsExactlyAsync<AnnasArchiveException>(() => client.SearchAsync(site, "book", CancellationToken.None));
            succeeds = true;
            await Assert.ThrowsExactlyAsync<AnnasArchiveException>(() => client.SearchAsync(site, "book", CancellationToken.None));
            Assert.AreEqual(1, pages.Calls.Count);
            time.Now = time.Now.AddMinutes(6);
            Assert.AreEqual(1, (await client.SearchAsync(site, "book", CancellationToken.None)).Count);
        }

        [TestMethod]
        public async Task Search_SameOperationSharesAnswers_NextOperationFetchesAgain()
        {
            var pages = new Pages(_ => Html(Fixture("search-list.html")));
            using var http = new HttpClient(pages);
            var client = new AnnasArchiveClient(http, new NoSourceGateResolver(), new Browser());
            using var context = new AnnasArchiveSearchContext();
            await Task.WhenAll(client.SearchAsync(new Uri("https://annas-archive.gl/"), "book", CancellationToken.None, context),
                client.SearchAsync(new Uri("https://annas-archive.pk/"), "book", CancellationToken.None, context));
            Assert.AreEqual(1, pages.Calls.Count);
            await client.SearchAsync(new Uri("https://annas-archive.gl/"), "book", CancellationToken.None);
            Assert.AreEqual(2, pages.Calls.Count);
        }

        [TestMethod]
        public async Task Search_CommonChallenge_UsesOneBrowserPassPerOperation()
        {
            var pages = new Pages(_ => Html("<title>DDoS-Guard</title>Checking your browser"));
            using var http = new HttpClient(pages);
            var browser = new Browser();
            var client = new AnnasArchiveClient(http, new NoSourceGateResolver(), browser);
            using var context = new AnnasArchiveSearchContext();
            var site = new Uri("https://annas-archive.gl/");
            var error = await Assert.ThrowsExactlyAsync<AnnasArchiveException>(() => client.SearchAsync(site, "first", CancellationToken.None, context));
            Assert.AreEqual(AnnasArchiveFailure.Gate, error.Failure);
            await Assert.ThrowsExactlyAsync<AnnasArchiveException>(() => client.SearchAsync(site, "second", CancellationToken.None, context));
            Assert.AreEqual(3, pages.Calls.Count);
            Assert.AreEqual(1, browser.Reads);
            await Assert.ThrowsExactlyAsync<AnnasArchiveException>(() => client.SearchAsync(site, "retry", CancellationToken.None));
            Assert.AreEqual(2, browser.Reads);
        }

        [TestMethod]
        public async Task Search_RunningBrowserSession_SkipsSeparateHttpRequestsAcrossQueries()
        {
            var pages = new Pages(_ => throw new AssertFailedException("An active source session must stay in Chrome."));
            using var http = new HttpClient(pages);
            var browser = new Browser { IsRunning = true, Page = Fixture("search-list.html") };
            var client = new AnnasArchiveClient(http, new NoSourceGateResolver(), browser);
            using var context = new AnnasArchiveSearchContext();
            var site = new Uri("https://annas-archive.gl/");

            Assert.AreEqual(1, (await client.SearchAsync(site, "first", default, context)).Count);
            Assert.AreEqual(1, (await client.SearchAsync(site, "first", default, context)).Count);
            Assert.AreEqual(1, (await client.SearchAsync(site, "second", default, context)).Count);
            Assert.AreEqual(1, (await client.SearchAsync(site, "third", default)).Count);
            Assert.AreEqual(3, browser.Reads);
            Assert.AreEqual(0, pages.Calls.Count);
        }

        [TestMethod]
        public async Task SlowDownload_RunningBrowserSession_KeepsCountdownAndFileLinkWithoutHttpProbe()
        {
            var pages = new Pages(_ => throw new AssertFailedException("A slow page must reuse the active Chrome session."));
            using var http = new HttpClient(pages);
            var browser = new Browser { IsRunning = true, Page = "<html><body>Please wait 90 seconds before downloading.</body></html>" };
            var client = new AnnasArchiveClient(http, new NoSourceGateResolver(), browser);
            var site = new Uri("https://annas-archive.gl/");
            var waiting = await client.ReadSlowPageAsync(site, "2bb80dfcf9d670598bd71910e1001ecc", default);
            Assert.AreEqual(TimeSpan.FromSeconds(90), waiting.Wait);
            Assert.IsNull(waiting.File);

            browser.Page = "<html><a href='https://files.example.test/book.epub'>Download</a></html>";
            var ready = await client.ReadSlowPageAsync(site, "2bb80dfcf9d670598bd71910e1001ecc", default);
            Assert.AreEqual(new Uri("https://files.example.test/book.epub"), ready.File);
            Assert.AreEqual(2, browser.SingleReads);
            Assert.AreEqual(0, pages.Calls.Count);
        }

        [TestMethod]
        public async Task SlowDownload_RunningBrowserCannotRead_DoesNotRepeatRequestsOutsideItsSession()
        {
            var pages = new Pages(_ => throw new AssertFailedException("A browser check must not trigger separate HTTP retries."));
            using var http = new HttpClient(pages);
            var browser = new Browser { IsRunning = true };
            var client = new AnnasArchiveClient(http, new NoSourceGateResolver(), browser);
            var error = await Assert.ThrowsExactlyAsync<AnnasArchiveException>(() =>
                client.ReadSlowPageAsync(new Uri("https://annas-archive.gl/"), "2bb80dfcf9d670598bd71910e1001ecc", default));
            Assert.AreEqual(AnnasArchiveFailure.Gate, error.Failure);
            Assert.AreEqual(1, browser.SingleReads);
            Assert.AreEqual(0, pages.Calls.Count);
        }

        [TestMethod]
        public async Task Search_UntrustedFinalOrigin_IsRejected()
        {
            var pages = new Pages(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://untrusted.example.test/search"),
                Content = new StringContent(Fixture("search-list.html"), Encoding.UTF8, "text/html")
            });
            using var http = new HttpClient(pages);
            var client = new AnnasArchiveClient(http, new NoSourceGateResolver(), new Browser());
            var error = await Assert.ThrowsExactlyAsync<AnnasArchiveException>(() => client.SearchAsync(new Uri("https://anna.example.test/"), "book", CancellationToken.None));
            Assert.AreEqual(AnnasArchiveFailure.UntrustedOrigin, error.Failure);
        }

        private static string Fixture(string name)
        {
            using var stream = typeof(AnnasArchiveSearchTests).Assembly.GetManifestResourceStream("Jularr.Backend.Tests.Fixtures.Anna." + name)
                ?? throw new InvalidOperationException("Missing Anna fixture: " + name);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }

        private static HttpResponseMessage Html(string page) => new(HttpStatusCode.OK) { Content = new StringContent(page, Encoding.UTF8, "text/html") };

        private sealed class Pages(Func<Uri, HttpResponseMessage> reply) : HttpMessageHandler
        {
            public ConcurrentBag<Uri> Calls { get; } = [];

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Calls.Add(request.RequestUri!);
                return Task.FromResult(reply(request.RequestUri!));
            }
        }

        private sealed class Clock : TimeProvider
        {
            public DateTimeOffset Now { get; set; } = new(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);
            public override DateTimeOffset GetUtcNow() => Now;
        }

        private sealed class Browser : ISourceBrowser
        {
            public int Reads { get; private set; }
            public int SingleReads { get; private set; }
            public bool IsRunning { get; init; }
            public string? Page { get; set; }
            public bool IsAvailable => true;
            public SourceBrowserStatus Status => new(null, null, "", IsRunning, null, null, null, null, null, 0);
            public IReadOnlyList<SourceBrowserEvent> Events => [];
            public Task<string?> ReadPageAsync(Uri uri, CancellationToken ct)
            {
                SingleReads++;
                return Task.FromResult(Page);
            }
            public Task<BrowserPage?> ReadPagesAsync(IReadOnlyList<Uri> addresses, CancellationToken ct)
            {
                Reads++;
                return Task.FromResult(Page is { } html ? new BrowserPage(addresses[0], html) : null);
            }
            public Task SaveFileAsync(Uri file, string destination, long? maximumBytes, Func<long, IDisposable>? reserveWrite, CancellationToken ct) => throw new NotSupportedException();
            public Task<SourceBrowserStatus> StartAsync(CancellationToken ct) => Task.FromResult(Status);
            public Task StopAsync() => Task.CompletedTask;
            public Task ResetAsync() => Task.CompletedTask;
        }
    }
}
