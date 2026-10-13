using System.Net;
using System.Net.Http.Json;
using System.Text;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Acquisition.V1;
using Jularr.Backend.Data.Admin.Activity.V1;
using Jularr.Backend.Data.Admin.Media.V1;
using Jularr.Backend.Data.Admin.Wanted.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Enums.Operation;
using Jularr.Backend.Data.Logic.Acquisition;
using Jularr.Backend.Data.System.Acquisition.V1;
using Jularr.Backend.Logic.Acquisition;
using Jularr.Backend.Logic.Library;
using Jularr.Backend.Service.System.Acquisition.V1;
using Jularr.Integrations.Logic.Gutenberg;
using Jularr.Integrations.Logic.Newznab;
using Jularr.Integrations.Logic.OpenLibrary;
using Jularr.Integrations.Logic.Sabnzbd;
using Microsoft.Extensions.DependencyInjection;
using Host = Jularr.Backend.Tests.AuthenticationTests.Host;
using TemporaryDirectory = Jularr.Backend.Tests.LibraryTests.TemporaryDirectory;

namespace Jularr.Backend.Tests
{
    [TestClass]
    public sealed class BookTests
    {
        private const string Connections = "/api/v1/admin/acquisition/connections";

        [TestMethod]
        public async Task Book_OpenLibraryGutenbergAndNewznab_ImportOnlyTheRightVerifiedBook()
        {
            using var storage = new TemporaryDirectory();
            using var downloads = new TemporaryDirectory();
            var sources = new FakeBookSources();
            await using var host = await CreateHostAsync(storage.Path, downloads.Path, sources);

            // Open Library names the book and its author.
            using var found = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/metadata/search", new SearchMetadataDataV1(MediaType.Book, "Pride and Prejudice"));
            Assert.AreEqual(HttpStatusCode.OK, found.StatusCode, await found.Content.ReadAsStringAsync());
            var result = (await found.Content.ReadFromJsonAsync<BaseDataList<MetadataSearchResultV1>>())!.First();
            Assert.AreEqual("OL66554W", result.ExternalId);
            Assert.AreEqual("Jane Austen", result.Author);
            using var imported = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/works/metadata", new ImportMetadataWorkDataV1(MediaType.Book, result.ExternalId));
            Assert.AreEqual(HttpStatusCode.OK, imported.StatusCode, await imported.Content.ReadAsStringAsync());
            var work = (await imported.Content.ReadFromJsonAsync<WorkDetailsV1>())!;
            Assert.AreEqual(1813, work.ReleaseYear);
            Assert.AreEqual("Jane Austen", await host.ScalarAsync("SELECT \"Value\" FROM \"WorkMetadataFacts\" WHERE \"FactKey\" = 'Author'"));
            var wantedId = await CreateWantedAsync(host, work.Id);

            // Identity before format: other authors and extra titles wait for a person.
            using var searched = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/wanted/" + wantedId + "/search", new SearchWantedDataV1());
            Assert.AreEqual(HttpStatusCode.OK, searched.StatusCode, await searched.Content.ReadAsStringAsync());
            var candidates = (await searched.Content.ReadFromJsonAsync<BaseDataList<WantedReleaseCandidateV1>>())!;
            var gutenberg = candidates.Single(candidate => candidate.ConnectionName == "Project Gutenberg" && candidate.Title.Contains("Pride and Prejudice (EN)", StringComparison.Ordinal));
            Assert.AreEqual(ReleaseDecisionType.Eligible, gutenberg.ReleaseDecisionTypeId);
            Assert.AreEqual("EPUB", gutenberg.QualityKey);
            Assert.AreNotEqual(ReleaseDecisionType.Eligible, candidates.Single(candidate => candidate.Title.StartsWith("Seth Grahame", StringComparison.Ordinal)).ReleaseDecisionTypeId);
            Assert.AreEqual("AuthorMissing", candidates.Single(candidate => candidate.Title.StartsWith("Pride.and.Prejudice.Retold", StringComparison.Ordinal)).ReasonCode);
            Assert.AreEqual("PDF", candidates.Single(candidate => candidate.Title.StartsWith("Jane.Austen", StringComparison.Ordinal)).QualityKey);

            // Jularr downloads the catalog book itself and imports it like any job.
            using var grabbed = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/wanted/" + wantedId + "/grab", new GrabWantedReleaseDataV1(gutenberg.CandidateId));
            Assert.AreEqual(HttpStatusCode.OK, grabbed.StatusCode, await grabbed.Content.ReadAsStringAsync());
            Assert.AreEqual(0, sources.SabGrabs);
            await RunAsync<MonitorDownloadsV1>(host);
            var book = Path.Combine(storage.Path, "1", "Books", "Jane Austen", "Pride and Prejudice (1813)", "Pride and Prejudice.epub");
            Assert.IsTrue(File.Exists(book));
            Assert.AreEqual(1L, await host.ScalarAsync("SELECT count(*) FROM \"StoredFiles\" WHERE \"MediaAssetId\" IS NOT NULL"));
            Assert.AreEqual((short)3, await host.ScalarAsync("SELECT \"OperationStatusTypeId\" FROM \"Operations\" WHERE \"OperationTypeId\" = 2"));
            Assert.AreEqual(0, Directory.GetFileSystemEntries(downloads.Path).Length);
        }

        [TestMethod]
        public async Task Book_ContentThatIsNoEbook_FailsTheReleaseAndStaysOutOfTheLibrary()
        {
            using var storage = new TemporaryDirectory();
            using var downloads = new TemporaryDirectory();
            var sources = new FakeBookSources { EpubBytes = Encoding.UTF8.GetBytes("<html>not a book</html>") };
            await using var host = await CreateHostAsync(storage.Path, downloads.Path, sources);
            using var imported = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/works/metadata", new ImportMetadataWorkDataV1(MediaType.Book, "OL66554W"));
            var work = (await imported.Content.ReadFromJsonAsync<WorkDetailsV1>())!;
            var wantedId = await CreateWantedAsync(host, work.Id);

            await RunAsync<SearchDueWantedV1>(host);
            Assert.AreEqual(0, sources.SabGrabs);
            await RunAsync<MonitorDownloadsV1>(host);
            Assert.AreEqual("download_failed", await host.ScalarAsync("SELECT \"ReasonKey\" FROM \"AcquisitionReleases\""));
            Assert.IsFalse(Directory.Exists(Path.Combine(storage.Path, "1", "Books", "Jane Austen")));
            using var activity = await host.SendAsync(HttpMethod.Get, "/api/v1/admin/activity?activityTabTypeId=ToDo");
            Assert.AreEqual(OperationType.Download, (await activity.Content.ReadFromJsonAsync<PageResult<ActivityItemV1>>())!.Items.Single().OperationTypeId);
            Assert.AreEqual(wantedId, (long)(await host.ScalarAsync("SELECT \"WantedItemId\" FROM \"AcquisitionReleases\""))!);
        }

        [TestMethod]
        public void Book_RulesForNamesCategoriesAndContent()
        {
            Assert.AreEqual("AZW3", BookReleaseJudge.Parse("Author - Title (2019) [azw3]").DocumentFormat);
            Assert.AreEqual(BookReleaseJudge.UnknownFormat, BookReleaseJudge.Parse("Author - Title (2019) retail").DocumentFormat);
            CollectionAssert.AreEqual(new[] { 7020 }, AcquisitionSelectionLogic.ReadCategories(AcquisitionKind.Book, [2040, 5040, 7020], null));
            CollectionAssert.AreEqual(new[] { 7000 }, AcquisitionSelectionLogic.ReadCategories(AcquisitionKind.Book, [2040], null));
            CollectionAssert.AreEqual(new[] { 2040 }, AcquisitionSelectionLogic.ReadCategories(AcquisitionKind.Movie, [2040, 7020], null));
            Assert.IsTrue(LibraryFileTypes.IsEbookContent(".epub", ReadingTests.BuildEpub("x").AsSpan(0, 128)));
            Assert.IsTrue(LibraryFileTypes.IsEbookContent(".pdf", "%PDF-1.7"u8));
            Assert.IsFalse(LibraryFileTypes.IsEbookContent(".epub", "PK\u0003\u0004 something else entirely here..........................."u8));
            Assert.IsFalse(LibraryFileTypes.IsEbookContent(".pdf", "<html>"u8));
        }

        private static async Task<Host> CreateHostAsync(string storage, string downloads, FakeBookSources sources)
        {
            var host = await LibraryTests.CreateHostAsync(storage, settings: new Dictionary<string, string> { ["Storage:DirectDownloadPath"] = downloads }, services: services =>
            {
                services.AddHttpClient<NewznabClient>().ConfigurePrimaryHttpMessageHandler(() => sources);
                services.AddHttpClient<SabnzbdClient>().ConfigurePrimaryHttpMessageHandler(() => sources);
                services.AddHttpClient<OpenLibraryClient>().ConfigurePrimaryHttpMessageHandler(() => sources);
                services.AddHttpClient<GutenbergClient>().ConfigurePrimaryHttpMessageHandler(() => sources);
            });

            // A writable book library, one indexer, one client and Project Gutenberg.
            var mountId = await LibraryTests.CreateMountAsync(host);
            await host.Fixture.RunSqlAsync("UPDATE \"StorageMounts\" SET \"IsReadOnly\" = FALSE");
            Directory.CreateDirectory(Path.Combine(storage, mountId.ToString(System.Globalization.CultureInfo.InvariantCulture), "Books"));
            await LibraryTests.CreateRootAsync(host, mountId, "Books", MediaType.Book);
            foreach (var data in new[]
            {
                new SaveAcquisitionConnectionDataV1("Indexer", AcquisitionConnectionType.Newznab, "https://indexer.example.test/", "indexer-key", true, 10, [7020], null, null, null),
                new SaveAcquisitionConnectionDataV1("SAB", AcquisitionConnectionType.Sabnzbd, "https://sab.example.test", "client-key", true, 10, [], "books", "/downloads", downloads),
                new SaveAcquisitionConnectionDataV1("Project Gutenberg", AcquisitionConnectionType.Gutenberg, "https://gutendex.example.test", null, true, 50, [], null, null, null)
            })
            {
                using var created = await host.SendAsync(HttpMethod.Post, Connections, data);
                Assert.AreEqual(HttpStatusCode.OK, created.StatusCode, await created.Content.ReadAsStringAsync());
            }

            using var tested = await host.SendAsync(HttpMethod.Post, Connections + "/3/test");
            Assert.IsTrue((await tested.Content.ReadFromJsonAsync<AcquisitionConnectionTestV1>())!.IsAvailable);
            return host;
        }

        private static async Task<long> CreateWantedAsync(Host host, long workId)
        {
            await host.Fixture.RunSqlAsync($"""
                INSERT INTO "AcquisitionRequests" ("Id", "WorkId", "RequestedByProfileId", "UiLocaleId", "AcquisitionRequestStatusTypeId", "DecidedByAccountId", "DecidedAt")
                VALUES (700, {workId}, 202, 1, 2, 41, now());
                INSERT INTO "WantedItems" ("Id", "WorkId") VALUES (800, {workId});
                INSERT INTO "RequestTargets" ("AcquisitionRequestId", "WantedItemId") VALUES (700, 800);
                """);
            return 800;
        }

        private static async Task RunAsync<TService>(Host host) where TService : Service.Core.SystemService<AcquisitionCycleParametersV1, NoData>
        {
            await using var scope = host.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<TService>().ExecuteAsync<UpdateResult>(new AcquisitionCycleParametersV1(), default);
        }

        private sealed class FakeBookSources : HttpMessageHandler
        {
            public int SabGrabs { get; private set; }
            public byte[] EpubBytes { get; set; } = ReadingTests.BuildEpub("Pride and Prejudice");

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var uri = request.RequestUri!;
                switch (uri.Host)
                {
                    case "openlibrary.org":
                        return uri.AbsolutePath switch
                        {
                            "/search.json" => Json("""{"docs":[{"key":"/works/OL66554W","title":"Pride and Prejudice","author_name":["Jane Austen"],"first_publish_year":1813,"cover_i":14348537,"isbn":["9780141439518"]}]}"""),
                            "/works/OL66554W.json" => Json("""{"title":"Pride and Prejudice","first_publish_date":"1813","description":{"value":"A novel of manners."},"covers":[14348537],"authors":[{"author":{"key":"/authors/OL21594A"}}]}"""),
                            "/authors/OL21594A.json" => Json("""{"name":"Jane Austen"}"""),
                            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
                        };
                    case "gutendex.example.test":
                        return Json("""{"results":[{"id":1342,"title":"Pride and Prejudice","authors":[{"name":"Austen, Jane"}],"languages":["en"],"formats":{"application/epub+zip":"https://www.gutenberg.org/ebooks/1342.epub3.images"}},{"id":7,"title":"Without Epub","authors":[],"languages":["en"],"formats":{"text/plain":"x"}}]}""");
                    case "www.gutenberg.org" when uri.AbsolutePath == "/ebooks/1342.epub3.images":
                        return new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("https://www.gutenberg.org/cache/epub/1342/pg1342-images-3.epub") } };
                    case "www.gutenberg.org":
                        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(EpubBytes) };
                    case "indexer.example.test":
                        return Xml("""
                            <rss><channel>
                              <item><title>Seth Grahame-Smith - Pride and Prejudice and Zombies (epub)</title><guid>zombies</guid><link>https://indexer.example.test/getnzb/zombies</link><size>1000000</size></item>
                              <item><title>Pride.and.Prejudice.Retold.2019.EPUB</title><guid>retold</guid><link>https://indexer.example.test/getnzb/retold</link><size>1000000</size></item>
                              <item><title>Jane.Austen.Pride.and.Prejudice.1813.PDF</title><guid>pdf</guid><link>https://indexer.example.test/getnzb/pdf</link><size>2000000</size></item>
                            </channel></rss>
                            """);
                    default:
                        var form = await request.Content!.ReadAsStringAsync(cancellationToken);
                        if (form.Contains("mode=addurl", StringComparison.Ordinal)) SabGrabs++;
                        return form.Contains("mode=queue", StringComparison.Ordinal) ? Json("""{"queue":{"paused":false,"slots":[]}}""")
                            : form.Contains("mode=history", StringComparison.Ordinal) ? Json("""{"history":{"slots":[]}}""")
                            : Json("""{"version":"4.5.0"}""");
                }
            }

            private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

            private static HttpResponseMessage Xml(string xml) => new(HttpStatusCode.OK) { Content = new StringContent(xml, Encoding.UTF8, "application/rss+xml") };
        }
    }
}
