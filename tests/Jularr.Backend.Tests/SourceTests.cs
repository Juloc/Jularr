using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Acquisition.V1;
using Jularr.Backend.Data.Admin.Media.V1;
using Jularr.Backend.Data.Admin.Wanted.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.System.Acquisition.V1;
using Jularr.Backend.Service.System.Acquisition.V1;
using Jularr.Integrations.Data.Common;
using Jularr.Integrations.Logic.AnnasArchive;
using Jularr.Integrations.Logic.Opds;
using Microsoft.Extensions.DependencyInjection;
using Host = Jularr.Backend.Tests.AuthenticationTests.Host;
using TemporaryDirectory = Jularr.Backend.Tests.LibraryTests.TemporaryDirectory;

namespace Jularr.Backend.Tests
{
    [TestClass]
    public sealed class SourceTests
    {
        private const string Connections = "/api/v1/admin/acquisition/connections";

        [TestMethod]
        public async Task Catalog_SetUpAddsKeylessSourcesAndAsksForAccounts()
        {
            using var storage = new TemporaryDirectory();
            await using var host = await LibraryTests.CreateHostAsync(storage.Path, razor: true);
            var before = await ReadCatalogAsync(host);
            Assert.IsFalse(before.Single(item => item.Key == "gutenberg").IsConfigured);
            Assert.IsTrue(before.Single(item => item.Key == "openlibrary").IsConfigured);
            Assert.AreEqual(SourceAvailability.ReferenceOnly, before.Single(item => item.Key == "bookwalker").SourceAvailabilityId);

            // Keys and accounts are asked for, never invented.
            using var setUp = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/acquisition/sources/setup");
            Assert.AreEqual(HttpStatusCode.OK, setUp.StatusCode, await setUp.Content.ReadAsStringAsync());
            var result = (await setUp.Content.ReadFromJsonAsync<SourceSetupResultV1>())!;
            CollectionAssert.AreEqual(new[] { "gutenberg" }, result.Added);
            CollectionAssert.IsSubsetOf(new[] { "annasarchive", "standardebooks", "newznab" }, result.NeedsAccount);
            Assert.IsTrue((await ReadCatalogAsync(host)).Single(item => item.Key == "gutenberg").IsConfigured);
            Assert.AreEqual(3L, await host.ScalarAsync("SELECT \"AcquisitionConnectionTypeId\"::bigint FROM \"AcquisitionConnections\""));

            using var page = await host.SendAsync(HttpMethod.Get, "/Admin/Acquisition/Sources");
            var html = await page.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, page.StatusCode, html);
            StringAssert.Contains(html, "Reference only");
            StringAssert.Contains(html, "/Admin/Acquisition?Type=AnnasArchive");
        }

        [TestMethod]
        public async Task Opds_LogsInOnlyAtItsOwnOriginRenewsSignedLinksAndImports()
        {
            using var storage = new TemporaryDirectory();
            using var downloads = new TemporaryDirectory();
            var sources = new FakeSources();
            await using var host = await CreateHostAsync(storage.Path, downloads.Path, sources);
            using var created = await host.SendAsync(HttpMethod.Post, Connections, new SaveAcquisitionConnectionDataV1("Calibre", AcquisitionConnectionType.Opds,
                "https://opds.example.test/opds", FakeSources.Password, true, 40, [], null, null, null, FakeSources.User));
            Assert.AreEqual(HttpStatusCode.OK, created.StatusCode, await created.Content.ReadAsStringAsync());
            using var listed = await host.SendAsync(HttpMethod.Get, Connections);
            Assert.IsFalse((await listed.Content.ReadAsStringAsync()).Contains(FakeSources.Password, StringComparison.Ordinal));
            var wantedId = await CreateWantedBookAsync(host);

            using var searched = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/wanted/" + wantedId + "/search", new SearchWantedDataV1());
            var candidates = (await searched.Content.ReadFromJsonAsync<BaseDataList<WantedReleaseCandidateV1>>())!;
            var opds = candidates.Single(candidate => candidate.ConnectionName == "Calibre" && candidate.QualityKey == "EPUB");
            Assert.AreEqual(ReleaseDecisionType.Eligible, opds.ReleaseDecisionTypeId);
            using var grabbed = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/wanted/" + wantedId + "/grab", new GrabWantedReleaseDataV1(opds.CandidateId));
            Assert.AreEqual(HttpStatusCode.OK, grabbed.StatusCode, await grabbed.Content.ReadAsStringAsync());
            await RunAsync<MonitorDownloadsV1>(host);

            // The expired signed link was renewed once; the download host never saw the login.
            Assert.IsTrue(File.Exists(Path.Combine(storage.Path, "1", "Books", "Jane Austen", "Pride and Prejudice (1813)", "Pride and Prejudice.epub")));
            Assert.AreEqual(2, sources.SignedHits);
            Assert.IsFalse(sources.AuthorizationSeenBy.Contains("cdn.example.test"));
            Assert.IsTrue(sources.AuthorizationSeenBy.Contains("opds.example.test"));
        }

        [TestMethod]
        public async Task AnnasArchive_KeyStaysWithTheSiteAndGatesWaitForAPerson()
        {
            using var storage = new TemporaryDirectory();
            using var downloads = new TemporaryDirectory();
            var sources = new FakeSources { Challenge = true };
            await using var host = await CreateHostAsync(storage.Path, downloads.Path, sources);
            using var created = await host.SendAsync(HttpMethod.Post, Connections, new SaveAcquisitionConnectionDataV1("Anna", AcquisitionConnectionType.AnnasArchive,
                "https://annas.example.test", FakeSources.MemberKey, true, 60, [], null, null, null));
            Assert.AreEqual(HttpStatusCode.OK, created.StatusCode, await created.Content.ReadAsStringAsync());
            var wantedId = await CreateWantedBookAsync(host);

            // A challenge is never passed in production; the source simply has no results.
            using var tested = await host.SendAsync(HttpMethod.Post, Connections + "/2/test");
            Assert.AreEqual("acquisition_unavailable", (await tested.Content.ReadFromJsonAsync<AcquisitionConnectionTestV1>())!.ErrorKey);
            using var blocked = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/wanted/" + wantedId + "/search", new SearchWantedDataV1());
            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, blocked.StatusCode);

            // Without the challenge the member key goes to the API only, not to the file host.
            sources.Challenge = false;
            using var searched = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/wanted/" + wantedId + "/search", new SearchWantedDataV1());
            var anna = (await searched.Content.ReadFromJsonAsync<BaseDataList<WantedReleaseCandidateV1>>())!.Single(candidate => candidate.ConnectionName == "Anna");
            Assert.AreEqual(ReleaseDecisionType.Eligible, anna.ReleaseDecisionTypeId);
            using var grabbed = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/wanted/" + wantedId + "/grab", new GrabWantedReleaseDataV1(anna.CandidateId));
            Assert.AreEqual(HttpStatusCode.OK, grabbed.StatusCode, await grabbed.Content.ReadAsStringAsync());
            await RunAsync<MonitorDownloadsV1>(host);
            Assert.IsTrue(File.Exists(Path.Combine(storage.Path, "1", "Books", "Jane Austen", "Pride and Prejudice (1813)", "Pride and Prejudice.epub")));
            Assert.IsTrue(sources.Requests.Any(request => request.StartsWith("annas.example.test/dyn/api/fast_download.json", StringComparison.Ordinal) && request.Contains(FakeSources.MemberKey)));
            Assert.IsFalse(sources.Requests.Any(request => request.StartsWith("files.example.test", StringComparison.Ordinal) && request.Contains(FakeSources.MemberKey)));
        }

        [TestMethod]
        public async Task SimulatedGates_PassOnlyForAllowedTestTargets()
        {
            // The override lives in the tests; it knows only its own simulated challenge and allowed hosts.
            var sources = new FakeSources { Challenge = true };
            var resolver = new TestGateResolver(["annas.example.test"], FakeSources.ChallengeToken);
            var client = new AnnasArchiveClient(new HttpClient(sources), resolver);
            Assert.AreEqual(1, (await client.SearchAsync(new Uri("https://annas.example.test/"), "Pride and Prejudice Austen", CancellationToken.None)).Count);

            var foreign = new AnnasArchiveClient(new HttpClient(sources), resolver);
            await Assert.ThrowsExactlyAsync<Integrations.Data.AnnasArchive.AnnasArchiveException>(() =>
                foreign.SearchAsync(new Uri("https://other.example.test/"), "Pride and Prejudice Austen", CancellationToken.None));
            var production = new AnnasArchiveClient(new HttpClient(sources), new Integrations.Logic.Common.NoSourceGateResolver());
            await Assert.ThrowsExactlyAsync<Integrations.Data.AnnasArchive.AnnasArchiveException>(() =>
                production.SearchAsync(new Uri("https://annas.example.test/"), "Pride and Prejudice Austen", CancellationToken.None));

            // A wrong token is not accepted even for an allowed host.
            var wrong = new AnnasArchiveClient(new HttpClient(sources), new TestGateResolver(["annas.example.test"], "guess"));
            await Assert.ThrowsExactlyAsync<Integrations.Data.AnnasArchive.AnnasArchiveException>(() =>
                wrong.SearchAsync(new Uri("https://annas.example.test/"), "Pride and Prejudice Austen", CancellationToken.None));
            Assert.IsFalse(new DirectCredentials(new Uri("https://opds.example.test/opds"), "Basic x").Covers(new Uri("https://cdn.example.test/file")));
        }

        private static async Task<IReadOnlyList<SourceCatalogItemV1>> ReadCatalogAsync(Host host)
        {
            using var response = await host.SendAsync(HttpMethod.Get, "/api/v1/admin/acquisition/sources");
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
            return (await response.Content.ReadFromJsonAsync<BaseDataList<SourceCatalogItemV1>>())!;
        }

        private static async Task<Host> CreateHostAsync(string storage, string downloads, FakeSources sources)
        {
            var host = await LibraryTests.CreateHostAsync(storage, settings: new Dictionary<string, string> { ["Storage:DirectDownloadPath"] = downloads }, services: services =>
            {
                services.AddHttpClient<OpdsClient>().ConfigurePrimaryHttpMessageHandler(() => sources);
                services.AddHttpClient<AnnasArchiveClient>().ConfigurePrimaryHttpMessageHandler(() => sources);
            });
            var mountId = await LibraryTests.CreateMountAsync(host);
            await host.Fixture.RunSqlAsync("UPDATE \"StorageMounts\" SET \"IsReadOnly\" = FALSE");
            Directory.CreateDirectory(Path.Combine(storage, mountId.ToString(System.Globalization.CultureInfo.InvariantCulture), "Books"));
            await LibraryTests.CreateRootAsync(host, mountId, "Books", MediaType.Book);
            using var client = await host.SendAsync(HttpMethod.Post, Connections, new SaveAcquisitionConnectionDataV1("SAB", AcquisitionConnectionType.Sabnzbd, "https://sab.example.test", "client-key",
                true, 10, [], "books", "/downloads", downloads));
            Assert.AreEqual(HttpStatusCode.OK, client.StatusCode, await client.Content.ReadAsStringAsync());
            return host;
        }

        private static async Task<long> CreateWantedBookAsync(Host host)
        {
            using var created = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/works", new CreateWorkDataV1(MediaType.Book, "Pride and Prejudice", 1813));
            var work = (await created.Content.ReadFromJsonAsync<WorkDetailsV1>())!;
            await host.Fixture.RunSqlAsync($"""
                INSERT INTO "WorkMetadataFacts" ("WorkId", "FactKey", "Value", "IsManual") VALUES ({work.Id}, 'Author', 'Jane Austen', TRUE);
                INSERT INTO "AcquisitionRequests" ("Id", "WorkId", "RequestedByProfileId", "UiLocaleId", "AcquisitionRequestStatusTypeId", "DecidedByAccountId", "DecidedAt")
                VALUES (710, {work.Id}, 202, 1, 2, 41, now());
                INSERT INTO "WantedItems" ("Id", "WorkId") VALUES (810, {work.Id});
                INSERT INTO "RequestTargets" ("AcquisitionRequestId", "WantedItemId") VALUES (710, 810);
                """);
            return 810;
        }

        private static async Task RunAsync<TService>(Host host) where TService : Service.Core.SystemService<AcquisitionCycleParametersV1, NoData>
        {
            await using var scope = host.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<TService>().ExecuteAsync<UpdateResult>(new AcquisitionCycleParametersV1(), default);
        }

        // Test-only override: passes the simulated challenge of explicitly allowed test hosts.
        private sealed class TestGateResolver(string[] allowedHosts, string expectedToken) : ISourceGateResolver
        {
            public Task<string?> ResolveAsync(SourceGate gate, CancellationToken ct) => Task.FromResult(
                gate.Uri.Host.EndsWith(".test", StringComparison.Ordinal) && allowedHosts.Contains(gate.Uri.Host) && gate.ChallengeToken == FakeSources.ChallengeToken
                    ? "gate=" + expectedToken
                    : null);
        }

        private sealed class FakeSources : HttpMessageHandler
        {
            public const string User = "reader";
            public const string Password = "opds-secret-password";
            public const string MemberKey = "member-secret-key";
            public const string ChallengeToken = "simulated-challenge";

            public bool Challenge { get; set; }
            public int SignedHits { get; private set; }
            public ConcurrentBag<string> AuthorizationSeenBy { get; } = [];
            public ConcurrentBag<string> Requests { get; } = [];

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var uri = request.RequestUri!;
                Requests.Add(uri.Host + uri.PathAndQuery);
                if (request.Headers.Authorization is not null)
                {
                    AuthorizationSeenBy.Add(uri.Host);
                }

                return Task.FromResult(uri.Host switch
                {
                    "opds.example.test" => Opds(request),
                    "cdn.example.test" => Signed(uri),
                    "annas.example.test" or "other.example.test" => Annas(request),
                    "files.example.test" => Bytes(ReadingTests.BuildEpub("Pride and Prejudice")),
                    _ => Text("""{"queue":{"paused":false,"slots":[]},"history":{"slots":[]},"version":"4.5.0"}""", "application/json")
                });
            }

            private static HttpResponseMessage Opds(HttpRequestMessage request)
            {
                var expected = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(User + ":" + Password));
                if (request.Headers.Authorization?.ToString() != expected)
                {
                    return new HttpResponseMessage(HttpStatusCode.Unauthorized);
                }

                return request.RequestUri!.AbsolutePath switch
                {
                    "/opds" => Text("""<feed xmlns="http://www.w3.org/2005/Atom"><title>Calibre</title><link rel="search" type="application/opensearchdescription+xml" href="/opds/osd"/></feed>""", "application/atom+xml"),
                    "/opds/osd" => Text("""<OpenSearchDescription xmlns="http://a9.com/-/spec/opensearch/1.1/"><Url type="application/atom+xml;profile=opds-catalog" template="https://opds.example.test/opds/search?q={searchTerms}"/></OpenSearchDescription>""", "application/opensearchdescription+xml"),
                    "/opds/search" => Text("""
                        <feed xmlns="http://www.w3.org/2005/Atom" xmlns:dc="http://purl.org/dc/terms/">
                          <entry><id>urn:calibre:1</id><title>Pride and Prejudice</title><author><name>Jane Austen</name></author><dc:language>en</dc:language>
                            <link rel="http://opds-spec.org/acquisition" type="application/epub+zip" href="/download/1.epub"/>
                          </entry>
                        </feed>
                        """, "application/atom+xml"),
                    "/download/1.epub" => new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("https://cdn.example.test/signed/1.epub?sig=" + Guid.NewGuid().ToString("N")) } },
                    _ => new HttpResponseMessage(HttpStatusCode.NotFound)
                };
            }

            // The first signed link has expired by the time it is used.
            private HttpResponseMessage Signed(Uri uri)
            {
                SignedHits++;
                return SignedHits == 1 ? new HttpResponseMessage(HttpStatusCode.Gone) : Bytes(ReadingTests.BuildEpub("Pride and Prejudice"));
            }

            private HttpResponseMessage Annas(HttpRequestMessage request)
            {
                var uri = request.RequestUri!;
                if (Challenge && !(request.Headers.TryGetValues("Cookie", out var cookies) && cookies.Contains("gate=" + ChallengeToken)))
                {
                    var gate = Text("<html><body>Checking your browser</body></html>", "text/html", HttpStatusCode.ServiceUnavailable);
                    gate.Headers.Add("cf-mitigated", "challenge");
                    gate.Headers.Add("X-Challenge-Token", ChallengeToken);
                    return gate;
                }

                return uri.AbsolutePath switch
                {
                    "/search" => Text("""
                        <html><body>
                          <a href="/md5/0123456789abcdef0123456789abcdef"><h3>Pride and Prejudice</h3><div class="italic">Jane Austen</div><div>English [en], .epub, 1.2MB, Pride and Prejudice.epub</div></a>
                          <a href="/md5/0123456789abcdef0123456789abcdef">duplicate</a>
                          <a href="/md5/not-a-hash"><h3>Broken</h3><div>.epub</div></a>
                        </body></html>
                        """, "text/html"),
                    "/dyn/api/fast_download.json" when uri.Query.Contains("key=" + MemberKey, StringComparison.Ordinal)
                        => Text("""{"download_url":"https://files.example.test/0123.epub"}""", "application/json"),
                    "/dyn/api/fast_download.json" => Text("""{"error":"Invalid key"}""", "application/json", HttpStatusCode.Forbidden),
                    _ => new HttpResponseMessage(HttpStatusCode.NotFound)
                };
            }

            private static HttpResponseMessage Text(string text, string mediaType, HttpStatusCode status = HttpStatusCode.OK) =>
                new(status) { Content = new StringContent(text, Encoding.UTF8, mediaType) };

            private static HttpResponseMessage Bytes(byte[] bytes) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        }
    }
}
