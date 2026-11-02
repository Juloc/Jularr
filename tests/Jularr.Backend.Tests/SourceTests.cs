using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Account.V1;
using Jularr.Backend.Data.Admin.Acquisition.V1;
using Jularr.Backend.Data.Admin.Activity.V1;
using Jularr.Backend.Data.Admin.Browser.V1;
using Jularr.Backend.Data.Admin.Instance.V1;
using Jularr.Backend.Data.Admin.Media.V1;
using Jularr.Backend.Data.Admin.Storage.V1;
using Jularr.Backend.Data.Admin.Wanted.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Logic.Acquisition;
using Jularr.Backend.Data.System.Acquisition.V1;
using Jularr.Backend.Service.Admin.Wanted.V1;
using Jularr.Backend.Data.User.Request.V1;
using Jularr.Backend.Service.System.Acquisition.V1;
using Jularr.Integrations.Data.Browser;
using Jularr.Integrations.Data.Common;
using Jularr.Integrations.Logic.AnnasArchive;
using Jularr.Integrations.Logic.Browser;
using Jularr.Integrations.Logic.Common;
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
        public void DirectCatalogLanguage_UsesVerifiedIsoCodesOnly()
        {
            Assert.AreEqual("DE", DirectSources.TrustedLanguageCode("deu"));
            Assert.AreEqual("DE", DirectSources.TrustedLanguageCode("ger"));
            Assert.AreEqual("JA", DirectSources.TrustedLanguageCode("ja-JP"));
            Assert.AreEqual("EN", DirectSources.TrustedLanguageCode("eng"));
            Assert.IsNull(DirectSources.TrustedLanguageCode(null));
            Assert.IsNull(DirectSources.TrustedLanguageCode(""));
            Assert.IsNull(DirectSources.TrustedLanguageCode("pdf"));
            Assert.IsNull(DirectSources.TrustedLanguageCode("unknown-language"));
        }


        [TestMethod]
        public async Task Catalog_SetUpAddsKeylessSourcesAndAsksForAccounts()
        {
            using var storage = new TemporaryDirectory();
            await using var host = await LibraryTests.CreateHostAsync(storage.Path, razor: true);
            var before = await ReadCatalogAsync(host);
            Assert.IsFalse(before.Single(item => item.Key == "gutenberg").IsConfigured);
            Assert.IsTrue(before.Single(item => item.Key == "openlibrary").IsConfigured);
            Assert.AreEqual(SourceAvailability.ReferenceOnly, before.Single(item => item.Key == "bookwalker").SourceAvailabilityId);
            Assert.AreEqual("https://api.nzbgeek.info", before.Single(item => item.Key == "nzbgeek").DefaultBaseUrl);
            Assert.AreEqual("https://annas-archive.gl", before.Single(item => item.Key == "annasarchive").DefaultBaseUrl);
            Assert.AreEqual(SourceAvailability.Planned, before.Single(item => item.Key == "nyaa").SourceAvailabilityId);

            // Keys and accounts are asked for, never invented.
            using var setUp = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/acquisition/sources/setup");
            Assert.AreEqual(HttpStatusCode.OK, setUp.StatusCode, await setUp.Content.ReadAsStringAsync());
            var result = (await setUp.Content.ReadFromJsonAsync<SourceSetupResultV1>())!;
            CollectionAssert.AreEqual(new[] { "gutenberg", "narou" }, result.Added);
            CollectionAssert.IsSubsetOf(new[] { "annasarchive", "standardebooks", "newznab" }, result.NeedsAccount);
            Assert.IsTrue((await ReadCatalogAsync(host)).Single(item => item.Key == "gutenberg").IsConfigured);
            using var standard = await host.SendAsync(HttpMethod.Post, Connections, new SaveAcquisitionConnectionDataV1("Standard Ebooks", AcquisitionConnectionType.Opds,
                "https://standardebooks.org/feeds/opds", null, true, 50, [], null, null, null));
            Assert.AreEqual(HttpStatusCode.OK, standard.StatusCode, await standard.Content.ReadAsStringAsync());
            var catalog = await ReadCatalogAsync(host);
            Assert.IsTrue(catalog.Single(item => item.Key == "standardebooks").IsConfigured);
            Assert.IsFalse(catalog.Single(item => item.Key == "opds").IsConfigured);
            Assert.AreEqual(3L, await host.ScalarAsync("SELECT \"AcquisitionConnectionTypeId\"::bigint FROM \"AcquisitionConnections\" ORDER BY \"Id\" LIMIT 1"));
            // Bulk setup must not silently activate download sources before a workspace is confirmed.
            Assert.AreEqual(0L, await host.ScalarAsync("SELECT \"IsEnabled\"::int::bigint FROM \"AcquisitionConnections\" WHERE \"AcquisitionConnectionTypeId\" = 3 LIMIT 1"));
            Assert.AreEqual(0L, await host.ScalarAsync("SELECT \"IsEnabled\"::int::bigint FROM \"AcquisitionConnections\" WHERE \"AcquisitionConnectionTypeId\" = 11"));
            Assert.AreEqual(true, await host.ScalarAsync("SELECT \"IsEnabled\" FROM \"Providers\" WHERE \"Key\" = 'syosetu'"));

            using var page = await host.SendAsync(HttpMethod.Get, "/Admin/Acquisition/Sources");
            var html = await page.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, page.StatusCode, html);
            StringAssert.Contains(html, "Reference only");
            StringAssert.Contains(html, "/Admin/Acquisition?Type=Newznab&amp;name=NZBgeek&amp;url=https%3A%2F%2Fapi.nzbgeek.info");

            // A preset opens the indexer form filled in; categories may stay empty.
            using var preset = await host.SendAsync(HttpMethod.Get, "/Admin/Acquisition?Type=Newznab&name=NZBgeek&url=https%3A%2F%2Fapi.nzbgeek.info");
            var form = await preset.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, preset.StatusCode, form);
            StringAssert.Contains(form, "value=\"https://api.nzbgeek.info\"");
            var categories = System.Text.RegularExpressions.Regex.Match(form, "<input[^>]*name=\"Categories\"[^>]*>").Value;
            Assert.IsFalse(string.IsNullOrEmpty(categories) || categories.Contains("required"), categories);

            // An indexer without categories uses the media type ranges.
            using var bare = await host.SendAsync(HttpMethod.Post, Connections, new SaveAcquisitionConnectionDataV1("Bare", AcquisitionConnectionType.Newznab, "https://bare.example.test", null, true,
                25, null, null, null, null));
            Assert.AreEqual(HttpStatusCode.OK, bare.StatusCode, await bare.Content.ReadAsStringAsync());
            StringAssert.Contains(html, "/Admin/Acquisition?Type=AnnasArchive");

            using var browserPage = await host.SendAsync(HttpMethod.Get, "/Admin/Browser");
            var browserHtml = await browserPage.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, browserPage.StatusCode, browserHtml);
            StringAssert.Contains(browserHtml, "/Admin/Instance/Modules");
            StringAssert.Contains(browserHtml, "Browser package");
        }

        [TestMethod]
        public async Task Sources_ShowNativeStoragePrerequisitesBeforeActivation()
        {
            using var storage = new TemporaryDirectory();
            await using var host = await LibraryTests.CreateHostAsync(storage.Path, razor: true);

            using (var catalog = await host.SendAsync(HttpMethod.Get, "/Admin/Acquisition/Sources"))
            {
                var html = await catalog.Content.ReadAsStringAsync();
                Assert.AreEqual(HttpStatusCode.OK, catalog.StatusCode, html);
                StringAssert.Contains(html, "native-storage-notice");
                StringAssert.Contains(html, "Anna&#x27;s Archive");
                StringAssert.Contains(html, "/Admin/DownloadStorage");
            }

            using (var newAnna = await host.SendAsync(HttpMethod.Get, "/Admin/Acquisition?Type=AnnasArchive"))
            {
                var html = await newAnna.Content.ReadAsStringAsync();
                Assert.AreEqual(HttpStatusCode.OK, newAnna.StatusCode, html);
                StringAssert.Contains(html, "native-storage-notice");
                StringAssert.Contains(html, "Configure download storage");
            }

            using (var downloads = await host.SendAsync(HttpMethod.Get, "/Admin/Downloads/Servers?Type=Usenet"))
            {
                var html = await downloads.Content.ReadAsStringAsync();
                Assert.AreEqual(HttpStatusCode.OK, downloads.StatusCode, html);
                StringAssert.Contains(html, "Jularr&#x27;s own Usenet downloader needs");
            }

            // After confirming Direct storage, Anna's Archive may be configured without a warning.
            await DownloadStorageTests.ConfigureAsync(host, storage.Path, Jularr.Backend.Data.Enums.Storage.DownloadStorageArea.Direct);
            using (var configured = await host.SendAsync(HttpMethod.Get, "/Admin/Acquisition?Type=AnnasArchive"))
            {
                var html = await configured.Content.ReadAsStringAsync();
                Assert.AreEqual(HttpStatusCode.OK, configured.StatusCode, html);
                Assert.IsFalse(html.Contains("native-storage-notice", StringComparison.Ordinal));
            }
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
        [DataRow(true)]
        [DataRow(false)]
        public async Task AnnasArchive_DerivativeIsRejectedBeforeAutomaticGrab(bool includesOriginal)
        {
            using var storage = new TemporaryDirectory();
            using var downloads = new TemporaryDirectory();
            var sources = new FakeSources();
            sources.SearchPages[""] = """
                <html><body>
                <a href="/md5/11111111111111111111111111111111"><h3>The expert guide to Jane Austen's Pride and Prejudice -- in 30 minutes</h3>
                <div class="italic">Jane Austen</div><div>English [en], .epub, 1.2MB, guide.epub</div></a>
                """ + (includesOriginal ? """
                <a href="/md5/0123456789abcdef0123456789abcdef"><h3>Pride and Prejudice</h3>
                <div class="italic">Jane Austen</div><div>English [en], .epub, 1.2MB, original.epub</div></a>
                """ : "") + "</body></html>";
            await using var host = await CreateHostAsync(storage.Path, downloads.Path, sources);
            using var created = await host.SendAsync(HttpMethod.Post, Connections, new SaveAcquisitionConnectionDataV1("Anna", AcquisitionConnectionType.AnnasArchive,
                "https://annas.example.test", null, true, 50, [], null, null, null));
            Assert.AreEqual(HttpStatusCode.OK, created.StatusCode, await created.Content.ReadAsStringAsync());
            var wantedId = await CreateWantedBookAsync(host);

            using var searched = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/wanted/" + wantedId + "/search", new SearchWantedDataV1());
            Assert.AreEqual(HttpStatusCode.OK, searched.StatusCode, await searched.Content.ReadAsStringAsync());
            var candidates = (await searched.Content.ReadFromJsonAsync<BaseDataList<WantedReleaseCandidateV1>>())!;
            var derivative = candidates.Single(candidate => candidate.Title.Contains("expert guide", StringComparison.Ordinal));
            Assert.AreEqual(ReleaseDecisionType.Rejected, derivative.ReleaseDecisionTypeId);
            Assert.AreEqual("WrongTitle", derivative.ReasonCode);

            await RunAsync<SearchDueWantedV1>(host);
            Assert.AreEqual(includesOriginal ? 1L : 0L, await host.ScalarAsync("SELECT count(*) FROM \"Operations\" WHERE \"OperationTypeId\" = 2"));
            Assert.AreEqual(0L, await host.ScalarAsync("SELECT count(*) FROM \"AcquisitionReleases\" WHERE \"Title\" LIKE '%expert guide%'"));
            if (includesOriginal)
            {
                Assert.AreEqual("Jane Austen - Pride and Prejudice (EN) (epub)", await host.ScalarAsync("SELECT \"Title\" FROM \"AcquisitionReleases\""));
            }
        }

        [TestMethod]
        public async Task AnnasArchive_ExistingMirrorIsAlreadyConfigured()
        {
            using var storage = new TemporaryDirectory();
            await using var host = await LibraryTests.CreateHostAsync(storage.Path, razor: true);
            using var created = await host.SendAsync(HttpMethod.Post, Connections, new SaveAcquisitionConnectionDataV1("Anna", AcquisitionConnectionType.AnnasArchive,
                "https://annas-archive.pk", null, true, 50, [], null, null, null));
            Assert.AreEqual(HttpStatusCode.OK, created.StatusCode, await created.Content.ReadAsStringAsync());
            var catalog = await ReadCatalogAsync(host);
            Assert.IsTrue(catalog.Single(item => item.Key == "annasarchive").IsConfigured);
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

            // A browser confirmation is distinct from an unavailable source or an empty search.
            using var tested = await host.SendAsync(HttpMethod.Post, Connections + "/2/test");
            Assert.AreEqual("source_gate", (await tested.Content.ReadFromJsonAsync<AcquisitionConnectionTestV1>())!.ErrorKey);
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
        public async Task AnnasArchive_UnreadableSearch_ReportsLayoutFailureThroughProbeAndSearch()
        {
            using var storage = new TemporaryDirectory();
            using var downloads = new TemporaryDirectory();
            var sources = new FakeSources();
            sources.SearchPages["Austen"] = "<html><title>Domain for sale</title></html>";
            await using var host = await CreateHostAsync(storage.Path, downloads.Path, sources);
            using var created = await host.SendAsync(HttpMethod.Post, Connections, new SaveAcquisitionConnectionDataV1("Anna", AcquisitionConnectionType.AnnasArchive,
                "https://annas.example.test", null, true, 60, [], null, null, null));
            Assert.AreEqual(HttpStatusCode.OK, created.StatusCode);
            var wantedId = await CreateWantedBookAsync(host);
            using var tested = await host.SendAsync(HttpMethod.Post, Connections + "/2/test");
            Assert.AreEqual("source_markup_changed", (await tested.Content.ReadFromJsonAsync<AcquisitionConnectionTestV1>())!.ErrorKey);
            using var searched = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/wanted/" + wantedId + "/search", new SearchWantedDataV1());
            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, searched.StatusCode);
            StringAssert.Contains(await searched.Content.ReadAsStringAsync(), "source_markup_changed");
        }

        [TestMethod]
        public async Task AnnasArchive_KeyedMirrorFallbackDownloadsWithoutLeakingKey()
        {
            using var storage = new TemporaryDirectory();
            using var downloads = new TemporaryDirectory();
            var sources = new FakeSources { MirrorOnlyPk = true, OriginalMemberUnavailable = true, SlowWaitSeconds = 0 };
            await using var host = await CreateHostAsync(storage.Path, downloads.Path, sources);
            using var created = await host.SendAsync(HttpMethod.Post, Connections, new SaveAcquisitionConnectionDataV1("Anna", AcquisitionConnectionType.AnnasArchive,
                "https://annas-archive.org", FakeSources.MemberKey, true, 60, [], null, null, null));
            Assert.AreEqual(HttpStatusCode.OK, created.StatusCode, await created.Content.ReadAsStringAsync());
            var wantedId = await CreateWantedBookAsync(host);

            using var searched = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/wanted/" + wantedId + "/search", new SearchWantedDataV1());
            var candidate = (await searched.Content.ReadFromJsonAsync<BaseDataList<WantedReleaseCandidateV1>>())!.Single(item => item.ConnectionName == "Anna");
            using var grabbed = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/wanted/" + wantedId + "/grab", new GrabWantedReleaseDataV1(candidate.CandidateId));
            Assert.AreEqual(HttpStatusCode.OK, grabbed.StatusCode, await grabbed.Content.ReadAsStringAsync());
            await RunAsync<MonitorDownloadsV1>(host);
            Assert.IsTrue(sources.Requests.Any(request => request.StartsWith("annas-archive.org/dyn/api/fast_download.json", StringComparison.Ordinal)
                && request.Contains(FakeSources.MemberKey)));
            Assert.IsTrue(sources.Requests.Any(request => request.StartsWith("annas-archive.pk/slow_download/", StringComparison.Ordinal)));
            Assert.IsFalse(sources.Requests.Any(request => request.StartsWith("annas-archive.pk/", StringComparison.Ordinal) && request.Contains(FakeSources.MemberKey)));
            Assert.IsTrue(Directory.EnumerateFiles(storage.Path, "*.epub", SearchOption.AllDirectories).Any());
        }

        [TestMethod]
        public async Task AnnasArchive_MirrorSearchNeverMovesMemberKeyToAnotherHost()
        {
            using var storage = new TemporaryDirectory();
            using var downloads = new TemporaryDirectory();
            var sources = new FakeSources { MirrorOnlyPk = true };
            await using var host = await CreateHostAsync(storage.Path, downloads.Path, sources);
            using var created = await host.SendAsync(HttpMethod.Post, Connections, new SaveAcquisitionConnectionDataV1("Anna", AcquisitionConnectionType.AnnasArchive,
                "https://annas-archive.gl", FakeSources.MemberKey, true, 60, [], null, null, null));
            Assert.AreEqual(HttpStatusCode.OK, created.StatusCode, await created.Content.ReadAsStringAsync());
            var wantedId = await CreateWantedBookAsync(host);

            using var searched = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/wanted/" + wantedId + "/search", new SearchWantedDataV1());
            var candidate = (await searched.Content.ReadFromJsonAsync<BaseDataList<WantedReleaseCandidateV1>>())!.Single(item => item.ConnectionName == "Anna");
            using var grabbed = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/wanted/" + wantedId + "/grab", new GrabWantedReleaseDataV1(candidate.CandidateId));
            Assert.AreEqual(HttpStatusCode.OK, grabbed.StatusCode, await grabbed.Content.ReadAsStringAsync());
            await RunAsync<MonitorDownloadsV1>(host);

            Assert.IsTrue(sources.Requests.Any(request => request.StartsWith("annas-archive.pk/search?", StringComparison.Ordinal)));
            Assert.IsTrue(sources.Requests.Any(request => request.StartsWith("annas-archive.gl/dyn/api/fast_download.json", StringComparison.Ordinal)
                && request.Contains(FakeSources.MemberKey)));
            Assert.IsFalse(sources.Requests.Any(request => request.StartsWith("annas-archive.pk/dyn/api/fast_download.json", StringComparison.Ordinal)));
            Assert.IsTrue(Directory.EnumerateFiles(storage.Path, "*.epub", SearchOption.AllDirectories).Any());
        }

        [TestMethod]
        public async Task AnnasArchive_WithoutKeyWaitsBetweenJobsAndImports()
        {
            using var storage = new TemporaryDirectory();
            using var downloads = new TemporaryDirectory();
            var sources = new FakeSources();
            await using var host = await CreateHostAsync(storage.Path, downloads.Path, sources);
            using var plain = await host.SendAsync(HttpMethod.Post, Connections, new SaveAcquisitionConnectionDataV1("Anna", AcquisitionConnectionType.AnnasArchive,
                "http://annas.example.test", null, true, 60, [], null, null, null));
            Assert.AreEqual(HttpStatusCode.BadRequest, plain.StatusCode);
            using var created = await host.SendAsync(HttpMethod.Post, Connections, new SaveAcquisitionConnectionDataV1("Anna", AcquisitionConnectionType.AnnasArchive,
                "https://annas.example.test", null, true, 60, [], null, null, null));
            Assert.AreEqual(HttpStatusCode.OK, created.StatusCode, await created.Content.ReadAsStringAsync());
            var wantedId = await CreateWantedBookAsync(host);
            await GrabAnnaAsync(host, wantedId);

            // The page's own countdown is kept between jobs: nobody is asked and no slot stays blocked.
            await RunAsync<MonitorDownloadsV1>(host);
            var book = Path.Combine(storage.Path, "1", "Books", "Jane Austen", "Pride and Prejudice (1813)", "Pride and Prejudice.epub");
            Assert.IsFalse(File.Exists(book));
            Assert.AreEqual(DBNull.Value, await host.ScalarAsync("SELECT \"AttentionKey\" FROM \"Operations\" WHERE \"OperationTypeId\" = 2"));
            Assert.AreEqual(1, Convert.ToInt32(await host.ScalarAsync("SELECT \"EtaSeconds\" FROM \"Operations\" WHERE \"OperationTypeId\" = 2")));
            Assert.AreEqual(1, Convert.ToInt32(await host.ScalarAsync("SELECT \"DownloadStateTypeId\" FROM \"Operations\" WHERE \"OperationTypeId\" = 2")));
            var asked = sources.Requests.Count(request => request.StartsWith("annas.example.test/slow_download/", StringComparison.Ordinal));
            Assert.AreEqual(1, asked);

            // While the countdown runs, the source is not asked again.
            await RunAsync<MonitorDownloadsV1>(host);
            Assert.AreEqual(asked, sources.Requests.Count(request => request.StartsWith("annas.example.test/slow_download/", StringComparison.Ordinal)));

            // Once the countdown ended, the page's link is fetched and imported without a key.
            await WaitForSlowPageAsync(host);
            Assert.IsTrue(File.Exists(book));
            Assert.IsTrue(sources.Requests.Any(request => request.StartsWith("annas.example.test/slow_download/", StringComparison.Ordinal)));
            Assert.IsFalse(sources.Requests.Any(request => request.Contains("fast_download", StringComparison.Ordinal)));
        }

        [TestMethod]
        public async Task AnnasArchive_RequestedBookIsDownloadedImportedAndRead()
        {
            using var storage = new TemporaryDirectory();
            using var downloads = new TemporaryDirectory();
            var sources = new FakeSources();
            await using var host = await CreateHostAsync(storage.Path, downloads.Path, sources);
            using var created = await host.SendAsync(HttpMethod.Post, Connections, new SaveAcquisitionConnectionDataV1("Anna", AcquisitionConnectionType.AnnasArchive,
                "https://annas.example.test", null, true, 60, [], null, null, null));
            Assert.AreEqual(HttpStatusCode.OK, created.StatusCode, await created.Content.ReadAsStringAsync());

            // The book is requested like any title; the request's target is what Anna's Archive is searched for.
            using var work = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/works", new CreateWorkDataV1(MediaType.Book, "Pride and Prejudice", 1813));
            var workId = (await work.Content.ReadFromJsonAsync<WorkDetailsV1>())!.Id;
            await host.Fixture.RunSqlAsync($"INSERT INTO \"People\" (\"Id\", \"Name\") VALUES (9001, 'Jane Austen'); INSERT INTO \"WorkPeople\" (\"WorkId\", \"PersonId\", \"PersonRoleId\", \"Position\") VALUES ({workId}, 9001, 7, 1)");
            using var requested = await host.SendAsync(HttpMethod.Post, "/api/v1/works/" + workId + "/requests", new CreateRequestDataV1(1));
            Assert.AreEqual(HttpStatusCode.OK, requested.StatusCode, await requested.Content.ReadAsStringAsync());
            var wantedId = (long)(await host.ScalarAsync("SELECT \"Id\" FROM \"WantedItems\" WHERE \"WorkId\" = " + workId))!;
            await GrabAnnaAsync(host, wantedId);
            await RunAsync<MonitorDownloadsV1>(host);
            await WaitForSlowPageAsync(host);

            // The imported book opens in the reader, reads and continues where it was left.
            var assetId = (long)(await host.ScalarAsync("SELECT \"Id\" FROM \"MediaAssets\" WHERE \"WorkId\" = " + workId))!;
            using var opened = await host.SendAsync(HttpMethod.Post, "/api/v1/reading/assets/" + assetId + "/open");
            Assert.AreEqual(HttpStatusCode.OK, opened.StatusCode, await opened.Content.ReadAsStringAsync());
            var document = (await opened.Content.ReadFromJsonAsync<Data.User.Reading.V1.ReadingDocumentV1>())!;
            Assert.AreEqual("Pride and Prejudice", document.Title);
            using var section = await host.SendAsync(HttpMethod.Get, "/api/v1/reading/documents/" + document.DocumentId + "/sections/0");
            Assert.AreEqual(HttpStatusCode.OK, section.StatusCode, await section.Content.ReadAsStringAsync());
            using var saved = await host.SendAsync(HttpMethod.Put, "/api/v1/reading/assets/" + assetId + "/progress", new Data.User.Reading.V1.SaveReadingProgressDataV1("s1/b0", 0.5m, false));
            Assert.AreEqual(HttpStatusCode.OK, saved.StatusCode, await saved.Content.ReadAsStringAsync());
            using var again = await host.SendAsync(HttpMethod.Post, "/api/v1/reading/assets/" + assetId + "/open");
            Assert.AreEqual("s1/b0", (await again.Content.ReadFromJsonAsync<Data.User.Reading.V1.ReadingDocumentV1>())!.Locator);
            using var reader = await host.SendAsync(HttpMethod.Get, "/Read/" + assetId);
            Assert.AreEqual(HttpStatusCode.OK, reader.StatusCode, await reader.Content.ReadAsStringAsync());
        }

        [TestMethod]
        public async Task AnnasArchive_ClosingAssistanceResolvesWaitingDownloadsWithoutPastedLinks()
        {
            using var storage = new TemporaryDirectory();
            using var downloads = new TemporaryDirectory();
            var sources = new FakeSources { SlowGate = true };
            using var browserPages = new HttpClient(sources, disposeHandler: false);
            var browser = new BrowserAssistanceTests.Interaction
            {
                ReadHtml = async (address, ct) =>
                {
                    using var response = await browserPages.GetAsync(address, ct);
                    return response.IsSuccessStatusCode ? await response.Content.ReadAsStringAsync(ct) : null;
                }
            };
            await using var host = await CreateHostAsync(storage.Path, downloads.Path, sources, browser);
            await host.Fixture.RunSqlAsync("UPDATE \"InstanceModules\" SET \"IsEnabled\" = TRUE WHERE \"Key\" = 'Browser'");
            using var created = await host.SendAsync(HttpMethod.Post, Connections, new SaveAcquisitionConnectionDataV1("Anna", AcquisitionConnectionType.AnnasArchive,
                "https://annas.example.test", null, true, 60, [], null, null, null));
            Assert.AreEqual(HttpStatusCode.OK, created.StatusCode, await created.Content.ReadAsStringAsync());
            var sourceId = (long)(await host.ScalarAsync("SELECT \"Id\" FROM \"AcquisitionConnections\" WHERE \"Name\" = 'Anna'"))!;
            using var other = await host.SendAsync(HttpMethod.Post, Connections, new SaveAcquisitionConnectionDataV1("Other", AcquisitionConnectionType.AnnasArchive,
                "https://other.example.test", null, true, 60, [], null, null, null));
            Assert.AreEqual(HttpStatusCode.OK, other.StatusCode);
            var otherId = (long)(await host.ScalarAsync("SELECT \"Id\" FROM \"AcquisitionConnections\" WHERE \"Name\" = 'Other'"))!;
            await GrabAnnaAsync(host, await CreateWantedBookAsync(host));
            await RunAsync<MonitorDownloadsV1>(host);
            Assert.AreEqual("source_manual_link", await AttentionAsync());

            // Open the waiting download's page, not the configured homepage or a different mirror's search.
            var operationId = (long)(await host.ScalarAsync("SELECT \"Id\" FROM \"Operations\" WHERE \"OperationTypeId\" = 2"))!;
            var downloadBrowser = "/api/v1/admin/activity/" + operationId + "/browser";
            using (var openedDownload = await host.SendAsync(HttpMethod.Post, downloadBrowser))
            {
                Assert.AreEqual(HttpStatusCode.OK, openedDownload.StatusCode, await openedDownload.Content.ReadAsStringAsync());
                var view = (await openedDownload.Content.ReadFromJsonAsync<BrowserViewV1>())!;
                Assert.AreEqual(operationId, view.OperationId);
                Assert.AreEqual(sourceId, view.AcquisitionConnectionId);
                Assert.AreEqual("https://annas.example.test/slow_download/0123456789abcdef0123456789abcdef/0/0", browser.Address!.AbsoluteUri);
                Assert.IsTrue(browser.OpensPage);
            }
            using (var page = await host.SendAsync(HttpMethod.Get, "/Admin/Activity?Filter=Waiting"))
            {
                StringAssert.Contains(await page.Content.ReadAsStringAsync(), "handler=OpenDownloadView&amp;operationId=" + operationId);
            }
            using (var page = await host.SendAsync(HttpMethod.Get, "/Admin/Browser"))
            {
                var html = await page.Content.ReadAsStringAsync();
                StringAssert.Contains(html, "handler=OpenDownloadView&amp;operationId=" + operationId);
                using var reopened = await host.SendAsync(HttpMethod.Post, "/Admin/Browser?handler=OpenDownloadView&operationId=" + operationId, new Dictionary<string, string>
                {
                    ["__RequestVerificationToken"] = LibraryTests.Token(html)
                });
                Assert.AreEqual(HttpStatusCode.OK, reopened.StatusCode, await reopened.Content.ReadAsStringAsync());
            }

            var sourcePage = browser.Address!.AbsoluteUri;
            foreach (var rejected in new[] { "https://other.example.test/check", "https://127.0.0.1/check", "https://user:secret@annas.example.test/check" })
            {
                await host.Fixture.RunSqlAsync("UPDATE \"Operations\" SET \"AttentionUrl\" = '" + rejected + "' WHERE \"Id\" = " + operationId);
                var starts = browser.Starts;
                using var refused = await host.SendAsync(HttpMethod.Post, downloadBrowser);
                Assert.AreEqual(HttpStatusCode.BadRequest, refused.StatusCode);
                Assert.AreEqual(starts, browser.Starts);
            }
            await host.Fixture.RunSqlAsync("UPDATE \"Operations\" SET \"AttentionUrl\" = '" + sourcePage + "' WHERE \"Id\" = " + operationId);
            await host.Fixture.RunSqlAsync("UPDATE \"AcquisitionConnections\" SET \"IsEnabled\" = FALSE WHERE \"Id\" = " + sourceId);
            using (var disabled = await host.SendAsync(HttpMethod.Post, downloadBrowser)) Assert.AreEqual(HttpStatusCode.BadRequest, disabled.StatusCode);
            await host.Fixture.RunSqlAsync("UPDATE \"AcquisitionConnections\" SET \"IsEnabled\" = TRUE, \"BaseUrl\" = 'https://annas-archive.gl' WHERE \"Id\" = " + sourceId);
            await host.Fixture.RunSqlAsync("UPDATE \"Operations\" SET \"AttentionUrl\" = 'https://annas-archive.pk/slow_download/0123456789abcdef0123456789abcdef/0/0' WHERE \"Id\" = " + operationId);
            using (var mirror = await host.SendAsync(HttpMethod.Post, downloadBrowser))
            {
                Assert.AreEqual(HttpStatusCode.OK, mirror.StatusCode, await mirror.Content.ReadAsStringAsync());
                Assert.AreEqual("annas-archive.pk", browser.Address!.Host);
            }
            await host.Fixture.RunSqlAsync("UPDATE \"AcquisitionConnections\" SET \"BaseUrl\" = 'https://annas.example.test' WHERE \"Id\" = " + sourceId);
            await host.Fixture.RunSqlAsync("UPDATE \"Operations\" SET \"AttentionUrl\" = '" + sourcePage + "' WHERE \"Id\" = " + operationId);

            // Closing a different source does not release this source's waiting jobs.
            await CloseAsync(otherId);
            Assert.AreEqual("source_manual_link", await AttentionAsync());
            await CloseAsync(sourceId);
            Assert.AreEqual(DBNull.Value, await AttentionAsync());
            await RunAsync<MonitorDownloadsV1>(host);
            Assert.AreEqual("source_manual_link", await AttentionAsync());

            // Unrelated storage/import attention must not be retried by closing a browser.
            await host.Fixture.RunSqlAsync("UPDATE \"Operations\" SET \"AttentionKey\" = 'download_size_limit' WHERE \"OperationTypeId\" = 2");
            await CloseAsync(sourceId);
            Assert.AreEqual("download_size_limit", await AttentionAsync());
            using (var wrongState = await host.SendAsync(HttpMethod.Post, downloadBrowser)) Assert.AreEqual(HttpStatusCode.BadRequest, wrongState.StatusCode);
            await host.Fixture.RunSqlAsync("UPDATE \"Operations\" SET \"AttentionKey\" = 'source_manual_link' WHERE \"OperationTypeId\" = 2");

            // The person completes the source's check; Jularr reads its countdown and file link itself.
            sources.SlowGate = false;
            await CloseAsync(sourceId);
            await RunAsync<MonitorDownloadsV1>(host);
            await WaitForSlowPageAsync(host);
            Assert.IsTrue(File.Exists(Path.Combine(storage.Path, "1", "Books", "Jane Austen", "Pride and Prejudice (1813)", "Pride and Prejudice.epub")));
            Assert.AreEqual(1L, await host.ScalarAsync("SELECT count(*) FROM \"Operations\" WHERE \"OperationTypeId\" = 2"));
            Assert.AreEqual(DBNull.Value, await host.ScalarAsync("SELECT \"SourceLinkUrl\" FROM \"Operations\" WHERE \"OperationTypeId\" = 2"));
            var assetId = (long)(await host.ScalarAsync("SELECT \"Id\" FROM \"MediaAssets\" LIMIT 1"))!;
            using var opened = await host.SendAsync(HttpMethod.Post, "/api/v1/reading/assets/" + assetId + "/open");
            Assert.AreEqual(HttpStatusCode.OK, opened.StatusCode, await opened.Content.ReadAsStringAsync());

            Task<object?> AttentionAsync() => host.ScalarAsync("SELECT \"AttentionKey\" FROM \"Operations\" WHERE \"OperationTypeId\" = 2");
            async Task CloseAsync(long id)
            {
                using var openedView = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/browser/assistance/" + id);
                Assert.AreEqual(HttpStatusCode.OK, openedView.StatusCode, await openedView.Content.ReadAsStringAsync());
                var view = (await openedView.Content.ReadFromJsonAsync<BrowserViewV1>())!;
                using var closed = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/browser/assistance/" + view.LeaseId + "/close");
                Assert.AreEqual(HttpStatusCode.OK, closed.StatusCode, await closed.Content.ReadAsStringAsync());
            }
        }

        [TestMethod]
        public async Task AnnasArchive_ChecksWaitForAPermittedPersonAndThenContinue()
        {
            using var storage = new TemporaryDirectory();
            using var downloads = new TemporaryDirectory();
            var sources = new FakeSources { SlowGate = true };
            await using var host = await CreateHostAsync(storage.Path, downloads.Path, sources);
            using var created = await host.SendAsync(HttpMethod.Post, Connections, new SaveAcquisitionConnectionDataV1("Anna", AcquisitionConnectionType.AnnasArchive,
                "https://annas.example.test", null, true, 60, [], null, null, null));
            Assert.AreEqual(HttpStatusCode.OK, created.StatusCode, await created.Content.ReadAsStringAsync());
            var wantedId = await CreateWantedBookAsync(host);
            await GrabAnnaAsync(host, wantedId);

            // The check is never passed by Jularr; the download waits and the source is not asked again.
            await RunAsync<MonitorDownloadsV1>(host);
            Assert.AreEqual("source_manual_link", await host.ScalarAsync("SELECT \"AttentionKey\" FROM \"Operations\" WHERE \"OperationTypeId\" = 2"));
            var asked = sources.Requests.Count(request => request.StartsWith("annas.example.test/slow_download/", StringComparison.Ordinal));
            await RunAsync<MonitorDownloadsV1>(host);
            Assert.AreEqual(asked, sources.Requests.Count(request => request.StartsWith("annas.example.test/slow_download/", StringComparison.Ordinal)));
            var operationId = (long)(await host.ScalarAsync("SELECT \"Id\" FROM \"Operations\" WHERE \"OperationTypeId\" = 2"))!;
            using var todo = await host.SendAsync(HttpMethod.Get, "/api/v1/admin/activity?activityTabTypeId=Queue");
            Assert.AreEqual("https://annas.example.test/slow_download/0123456789abcdef0123456789abcdef/0/0",
                (await todo.Content.ReadFromJsonAsync<PageResult<ActivityItemV1>>())!.Items.Single(item => item.Id == operationId).ManualUrl);
            using var plain = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/activity/" + operationId + "/source-link", new SaveActivitySourceLinkDataV1("http://files.example.test/0123.epub"));
            Assert.AreEqual(HttpStatusCode.BadRequest, plain.StatusCode);

            // An expired link goes back to the person; the record stays known.
            using var expired = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/activity/" + operationId + "/source-link", new SaveActivitySourceLinkDataV1("https://files.example.test/expired.epub"));
            Assert.AreEqual(HttpStatusCode.OK, expired.StatusCode, await expired.Content.ReadAsStringAsync());
            await RunAsync<MonitorDownloadsV1>(host);
            Assert.AreEqual("https://annas.example.test/slow_download/0123456789abcdef0123456789abcdef/0/0",
                await host.ScalarAsync("SELECT \"AttentionUrl\" FROM \"Operations\" WHERE \"OperationTypeId\" = 2"));
            Assert.AreEqual(DBNull.Value, await host.ScalarAsync("SELECT \"SourceLinkUrl\" FROM \"Operations\" WHERE \"OperationTypeId\" = 2"));

            // Users see and confirm only while the instance and their account allow it.
            using var assigned = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/accounts/41/profiles/202");
            Assert.AreEqual(HttpStatusCode.OK, assigned.StatusCode, await assigned.Content.ReadAsStringAsync());
            using var switched = await host.SendAsync(HttpMethod.Put, "/api/v1/auth/profile/202");
            Assert.AreEqual(HttpStatusCode.OK, switched.StatusCode, await switched.Content.ReadAsStringAsync());
            Assert.IsNull((await ReadOwnRequestAsync(host)).ManualUrl);
            var link = new SaveRequestSourceLinkDataV1(operationId, "https://files.example.test/0123.epub");
            using (var refused = await host.SendAsync(HttpMethod.Post, "/api/v1/requests/710/source-link", link))
            {
                Assert.AreEqual(HttpStatusCode.NotFound, refused.StatusCode);
            }

            using var settings = await host.SendAsync(HttpMethod.Get, "/api/v1/admin/instance/settings");
            var instance = (await settings.Content.ReadFromJsonAsync<InstanceSettingsDetailsV1>())!;
            using var allowed = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/instance/settings", new UpdateInstanceSettingsDataV1(instance.Name, instance.UiLocaleId, instance.Version, usersConfirmSources: true));
            Assert.AreEqual(HttpStatusCode.OK, allowed.StatusCode, await allowed.Content.ReadAsStringAsync());
            Assert.IsNull((await ReadOwnRequestAsync(host)).ManualUrl);
            using var account = await host.SendAsync(HttpMethod.Get, "/api/v1/admin/accounts/41");
            var owner = (await account.Content.ReadFromJsonAsync<AccountDetailsV1>())!;
            Assert.IsFalse(owner.MayConfirmSources);
            using var permitted = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/accounts/41", new UpdateAccountDataV1(owner.DisplayName, owner.AccountRoleTypeId, owner.IsEnabled, true));
            Assert.AreEqual(HttpStatusCode.OK, permitted.StatusCode, await permitted.Content.ReadAsStringAsync());
            var request = await ReadOwnRequestAsync(host);
            Assert.AreEqual(operationId, request.OperationId);
            StringAssert.StartsWith(request.ManualUrl, "https://annas.example.test/slow_download/");
            using (var other = await host.SendAsync(HttpMethod.Post, "/api/v1/requests/999/source-link", link))
            {
                Assert.AreEqual(HttpStatusCode.NotFound, other.StatusCode);
            }

            using var page = await host.SendAsync(HttpMethod.Get, "/Requests");
            var html = await page.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, page.StatusCode, html);
            StringAssert.Contains(html, "handler=SourceLink");
            using var confirmed = await host.SendAsync(HttpMethod.Post, "/api/v1/requests/710/source-link", link);
            Assert.AreEqual(HttpStatusCode.OK, confirmed.StatusCode, await confirmed.Content.ReadAsStringAsync());

            // The pasted file link is fetched without any key; the import follows as usual.
            await RunAsync<MonitorDownloadsV1>(host);
            Assert.IsTrue(File.Exists(Path.Combine(storage.Path, "1", "Books", "Jane Austen", "Pride and Prejudice (1813)", "Pride and Prejudice.epub")));
            Assert.IsNull((await ReadOwnRequestAsync(host)).ManualUrl);
        }

        [TestMethod]
        public async Task AnnasArchive_LongCountdownIsHandedBackInsteadOfGuessed()
        {
            var sources = new FakeSources { SlowWaitSeconds = 600 };
            var client = new AnnasArchiveClient(new HttpClient(sources), new NoSourceGateResolver(), NoBrowser());
            var page = await client.ReadSlowPageAsync(new Uri("https://annas.example.test/"), "0123456789abcdef0123456789abcdef", CancellationToken.None);

            // The page names its wait and its file; the file host is not touched before the wait ended.
            Assert.AreEqual(TimeSpan.FromMinutes(10), page.Wait);
            Assert.AreEqual("https://files.example.test/0123.epub", page.File!.ToString());
            Assert.IsFalse(sources.Requests.Any(request => request.StartsWith("files.example.test", StringComparison.Ordinal)));
        }

        [TestMethod]
        public async Task AnnasArchive_CountdownPageWithoutAFileIsAWait()
        {
            // The site counts down first and names the file only afterwards.
            var sources = new FakeSources { SlowWaitSeconds = 27, SlowWithoutFile = true };
            var client = new AnnasArchiveClient(new HttpClient(sources), new NoSourceGateResolver(), NoBrowser());
            var page = await client.ReadSlowPageAsync(new Uri("https://annas.example.test/"), "0123456789abcdef0123456789abcdef", CancellationToken.None);

            Assert.AreEqual(TimeSpan.FromSeconds(27), page.Wait);
            Assert.IsNull(page.File);
        }

        [TestMethod]
        public async Task AnnasArchive_SearchReadsTheResultRowsOfTheSite()
        {
            // The site names a file, a title, an author and the file's facts in one row.
            var sources = new FakeSources { RealRows = true };
            var client = new AnnasArchiveClient(new HttpClient(sources), new NoSourceGateResolver(), NoBrowser());
            var records = await client.SearchAsync(new Uri("https://annas.example.test/"), "Pride and Prejudice Austen", CancellationToken.None);

            var record = records.Single();
            Assert.AreEqual("0123456789abcdef0123456789abcdef", record.Md5);
            Assert.AreEqual("Pride and Prejudice", record.Title);
            Assert.AreEqual("Jane Austen", record.Author);
            Assert.AreEqual("epub", record.Extension);
            Assert.AreEqual("en", record.Language);
            Assert.AreEqual(1258291L, record.SizeBytes);
        }

        [TestMethod]
        public async Task AnnasArchive_FormatBadgesAreNotLanguageEvidence()
        {
            // The real result-row shape can put a [pdf] format badge before a language badge.
            var sources = new FakeSources { RealRows = true, RowFacts = "PDF [pdf] · English [en] · 1.2MB" };
            var client = new AnnasArchiveClient(new HttpClient(sources), new NoSourceGateResolver(), NoBrowser());
            var record = (await client.SearchAsync(new Uri("https://annas.example.test/"), "Pride and Prejudice", CancellationToken.None)).Single();
            Assert.AreEqual("en", record.Language);

            sources.RowFacts = "PDF [pdf] · 1.2MB";
            record = (await client.SearchAsync(new Uri("https://annas.example.test/"), "Pride and Prejudice", CancellationToken.None)).Single();
            Assert.IsNull(record.Language, "A format badge alone must not be mistaken for a language.");
        }

        [TestMethod]
        public async Task AnnasArchive_HtmlCheckWithSuccessStatusIsNotAnEmptySearch()
        {
            var sources = new FakeSources { HtmlGate = true };
            var client = new AnnasArchiveClient(new HttpClient(sources), new NoSourceGateResolver(), NoBrowser());
            await Assert.ThrowsExactlyAsync<Integrations.Data.AnnasArchive.AnnasArchiveException>(() =>
                client.SearchAsync(new Uri("https://annas.example.test/"), "Pride and Prejudice", CancellationToken.None));
        }

        [TestMethod]
        public async Task AnnasArchive_LegacyDefaultSearchFallsBackToAvailableMirror()
        {
            var sources = new FakeSources { MirrorOnlyPk = true };
            var client = new AnnasArchiveClient(new HttpClient(sources), new NoSourceGateResolver(), NoBrowser());
            var record = (await client.SearchAsync(new Uri("https://annas-archive.org/"), "Pride and Prejudice", CancellationToken.None)).Single();
            Assert.AreEqual("annas-archive.pk", record.Site.Host);
            Assert.IsTrue(AnnasArchiveClient.IsTrustedSource(new Uri("https://annas-archive.org/"), record.Site));
        }

        [TestMethod]
        public async Task AnnasArchive_SearchKeepsTheMirrorThatResponded()
        {
            var sources = new FakeSources { MirrorOnlyPk = true };
            var client = new AnnasArchiveClient(new HttpClient(sources), new NoSourceGateResolver(), NoBrowser());
            var record = (await client.SearchAsync(new Uri("https://annas-archive.gl/"), "Pride and Prejudice", CancellationToken.None)).Single();
            Assert.AreEqual("annas-archive.pk", record.Site.Host);
            Assert.AreEqual("annas-archive.pk", AnnasArchiveClient.RecordUri(record.Site, record.Md5).Host);
        }

        [TestMethod]
        [DataRow("https://annas-archive.gl/", "https://annas-archive.pk/md5/0123456789abcdef0123456789abcdef", true)]
        [DataRow("https://annas-archive.gl/", "https://not-anna.example/md5/0123456789abcdef0123456789abcdef", false)]
        [DataRow("https://annas.example.test/", "https://annas.example.test/md5/0123456789abcdef0123456789abcdef", true)]
        [DataRow("https://annas.example.test/", "https://annas-archive.gl/md5/0123456789abcdef0123456789abcdef", false)]
        [DataRow("https://annas.example.test/", "http://annas.example.test/md5/0123456789abcdef0123456789abcdef", false)]
        [DataRow("https://annas.example.test/", "https://annas.example.test:9443/md5/0123456789abcdef0123456789abcdef", false)]
        public void AnnasArchive_KeyedDownloadsAcceptOnlyConfiguredOrKnownMirrorOrigins(string configured, string candidate, bool trusted) =>
            Assert.AreEqual(trusted, AnnasArchiveClient.IsTrustedSource(new Uri(configured), new Uri(candidate)));

        [TestMethod]
        public async Task DirectDownload_OversizedHtmlIsRejectedBeforeReading()
        {
            var sources = new FakeSources { OversizedSearch = true };
            using var http = new HttpClient(sources);
            var failure = await Assert.ThrowsExactlyAsync<DirectDownloadException>(() => DirectDownload.ReadPageAsync(http,
                new Uri("https://annas.example.test/search?q=book"), new NoSourceGateResolver(), NoBrowser(), CancellationToken.None));
            Assert.AreEqual(DirectDownloadFailure.TooLarge, failure.Failure);
        }

        [TestMethod]
        public async Task AnnasArchive_OversizedSearchNeverFallsBackToBrowser()
        {
            var sources = new FakeSources { OversizedSearch = true };
            var browser = new FakeBrowser();
            var client = new AnnasArchiveClient(new HttpClient(sources), new NoSourceGateResolver(), browser);
            var error = await Assert.ThrowsExactlyAsync<Integrations.Data.AnnasArchive.AnnasArchiveException>(() =>
                client.SearchAsync(new Uri("https://annas.example.test/"), "oversized", CancellationToken.None));
            Assert.AreEqual(Integrations.Data.AnnasArchive.AnnasArchiveFailure.InvalidResponse, error.Failure);
            Assert.AreEqual(0, browser.PagesRead);
        }

        [TestMethod]
        public async Task AnnasArchive_CaptchaWordInBookTitleIsNotAChallenge()
        {
            var sources = new FakeSources();
            sources.SearchPages["captcha"] = """
                <html><body><a href="/md5/0123456789abcdef0123456789abcdef">
                <h3>Solving the Captcha Mystery</h3><div class="italic">Example Author</div>
                <div>English [en], .epub, 1MB</div></a></body></html>
                """;
            using var http = new HttpClient(sources);
            var client = new AnnasArchiveClient(http, new NoSourceGateResolver(), NoBrowser());
            var results = await client.SearchAsync(new Uri("https://annas.example.test/"), "captcha", CancellationToken.None);
            Assert.AreEqual("Solving the Captcha Mystery", results.Single().Title);
        }

        [TestMethod]
        public async Task AnnasArchive_FileThatIsOnlyAPartIsMarked()
        {
            // A file that names a part of a work is only that part.
            var sources = new FakeSources { RealRows = true };
            var client = new AnnasArchiveClient(new HttpClient(sources), new NoSourceGateResolver(), NoBrowser());
            var record = (await client.SearchAsync(new Uri("https://annas.example.test/"), "Pride and Prejudice Austen", CancellationToken.None)).Single();
            Assert.IsFalse(record.IsPart);

            var parts = new FakeSources { RealRows = true, RowFileName = "lgli/Jane Austen - Pride and Prejudice (1813) Part 1 of 3.epub" };
            var other = new AnnasArchiveClient(new HttpClient(parts), new NoSourceGateResolver(), NoBrowser());
            Assert.IsTrue((await other.SearchAsync(new Uri("https://annas.example.test/"), "Pride and Prejudice Austen", CancellationToken.None)).Single().IsPart);
        }

        [TestMethod]
        public async Task AnnasArchive_SearchGathersTheFilesOfEveryWay()
        {
            // One way finds one file, the next another; the caller asks for them one after the other.
            var sources = new FakeSources { RealRows = true };
            sources.SearchPages["zweiterWeg"] = """
                <html><body>
                  <a href="/md5/11111111111111111111111111111111"><h3>Pride and Prejudice</h3><div class="italic">Jane Austen</div><div>English [en], .epub, 1.2MB, Pride and Prejudice.epub</div></a>
                </body></html>
                """;
            var client = new AnnasArchiveClient(new HttpClient(sources), new NoSourceGateResolver(), NoBrowser());
            var first = await client.SearchAsync(new Uri("https://annas.example.test/"), "Pride and Prejudice Austen", CancellationToken.None);
            var second = await client.SearchAsync(new Uri("https://annas.example.test/"), "zweiterWeg", CancellationToken.None);

            Assert.AreEqual(1, first.Count);
            Assert.AreEqual("0123456789abcdef0123456789abcdef", first[0].Md5);
            Assert.AreEqual(1, second.Count);
            Assert.AreEqual("11111111111111111111111111111111", second[0].Md5);
        }

        [TestMethod]
        public void AnnaWays_NameTheWorkInEveryWay_TheIsbnFirst()
        {
            // The work knows the ways: the ISBN names the edition exactly, then the title with the author,
            // the title alone and the titles of the editions in the other languages.
            var target = new WantedTargetData(1, 2, MediaType.Book, null, "Rich Dad, Poor Dad", null, ["Rich Dad, Poor Dad"], null, null, null, [], false, false, false, 0, [],
                ["de"], "Robert Kiyosaki", null, null, editionTitles: ["Rich Dad Poor Dad", "Padre Rico, Padre Pobre"], isbns: ["3959720106", "9781612681139"]);

            var ways = DirectSources.AnnaWays(target);

            CollectionAssert.AreEqual(new[]
            {
                "3959720106",
                "9781612681139",
                "Rich Dad, Poor Dad Robert Kiyosaki",
                "Rich Dad, Poor Dad",
                "Rich Dad Poor Dad",
                "Padre Rico, Padre Pobre"
            }, ways.ToArray());
        }

        [TestMethod]
        public async Task AnnasArchive_APageInsteadOfTheFile_IsFetchedByTheBrowser()
        {
            // The site may answer with a page in place of the file; the browser fetches the file then.
            var sources = new FakeSources { RealRows = true };
            var browser = new FakeBrowser();
            var client = new AnnasArchiveClient(new HttpClient(sources), new NoSourceGateResolver(), browser);
            var destination = Path.Combine(Path.GetTempPath(), "anna-browser-" + Guid.NewGuid().ToString("N")[..6] + ".epub");
            try
            {
                await client.DownloadFileAsync(new Uri("https://annas.example.test/book.epub"), destination, CancellationToken.None, null, _ => new MemoryStream());
                Assert.AreEqual(1, browser.Saved);
                Assert.IsTrue(File.Exists(destination));
            }
            finally
            {
                File.Delete(destination);
            }
        }

        [TestMethod]
        public async Task AnnasArchive_FailedDownloadPreservesAnExistingFile()
        {
            var sources = new FakeSources();
            var client = new AnnasArchiveClient(new HttpClient(sources), new NoSourceGateResolver(), NoBrowser());
            var destination = Path.Combine(Path.GetTempPath(), "anna-existing-" + Guid.NewGuid().ToString("N") + ".epub");
            var original = Encoding.UTF8.GetBytes("keep-this-existing-file");
            File.WriteAllBytes(destination, original);
            try
            {
                await Assert.ThrowsExactlyAsync<IOException>(() =>
                    client.DownloadFileAsync(new Uri("https://files.example.test/0123.epub"), destination, CancellationToken.None, null, _ => new MemoryStream()));
                CollectionAssert.AreEqual(original, File.ReadAllBytes(destination));
            }
            finally
            {
                File.Delete(destination);
            }
        }

        [TestMethod]
        public async Task AnnasArchive_BrowserHtmlIsRejectedAndIncompleteFileRemoved()
        {
            var sources = new FakeSources();
            var browser = new FakeBrowser { SaveHtml = true };
            var client = new AnnasArchiveClient(new HttpClient(sources), new NoSourceGateResolver(), browser);
            var destination = Path.Combine(Path.GetTempPath(), "anna-check-" + Guid.NewGuid().ToString("N") + ".epub");
            await Assert.ThrowsExactlyAsync<Integrations.Data.AnnasArchive.AnnasArchiveException>(() =>
                client.DownloadFileAsync(new Uri("https://annas.example.test/book.epub"), destination, CancellationToken.None, null, _ => new MemoryStream()));
            Assert.IsFalse(File.Exists(destination));
        }

        // A browser that writes the file it is asked for.
        private sealed class FakeBrowser : ISourceBrowser
        {
            public int Saved { get; private set; }
            public int PagesRead { get; private set; }
            public bool SaveHtml { get; set; }

            public bool IsAvailable => true;
            public SourceBrowserStatus Status => new(null, null, "", false, null, null, null, null, null, 0);
            public IReadOnlyList<SourceBrowserEvent> Events => [];

            public Task<string?> ReadPageAsync(Uri uri, CancellationToken ct) => Task.FromResult<string?>(null);

            public Task<SourceBrowserStatus> StartAsync(CancellationToken ct) => Task.FromResult(Status);

            public Task StopAsync() => Task.CompletedTask;

            public Task ResetAsync() => Task.CompletedTask;

            public Task<BrowserPage?> ReadPagesAsync(IReadOnlyList<Uri> addresses, CancellationToken ct)
            {
                PagesRead++;
                return Task.FromResult<BrowserPage?>(null);
            }

            public Task SaveFileAsync(Uri file, string destination, long? maximumBytes, Func<long, IDisposable>? reserveWrite, CancellationToken ct)
            {
                Saved++;
                File.WriteAllBytes(destination, SaveHtml ? Encoding.UTF8.GetBytes("<html>checking</html>") : [0x50, 0x4B, 0x03, 0x04, 0x00]);
                return Task.CompletedTask;
            }
        }

        [TestMethod]
        [DataRow("Please wait 2 minutes before downloading.", 120)]
        [DataRow("Please wait 01:30 before downloading.", 90)]
        [DataRow("Please wait 1:05:00 before downloading.", 3900)]
        public async Task AnnasArchive_CountdownSpeaksSecondsMinutesAndClocks(string text, int seconds)
        {
            var sources = new FakeSources { SlowWaitText = text };
            var client = new AnnasArchiveClient(new HttpClient(sources), new NoSourceGateResolver(), NoBrowser());
            var page = await client.ReadSlowPageAsync(new Uri("https://annas.example.test/"), "0123456789abcdef0123456789abcdef", CancellationToken.None);
            Assert.AreEqual(TimeSpan.FromSeconds(seconds), page.Wait);
        }

        [TestMethod]
        public async Task SimulatedGates_PassOnlyForAllowedTestTargets()
        {
            // The override lives in the tests; it knows only its own simulated challenge and allowed hosts.
            var sources = new FakeSources { Challenge = true };
            var resolver = new TestGateResolver(["annas.example.test"], FakeSources.ChallengeToken);
            var client = new AnnasArchiveClient(new HttpClient(sources), resolver, NoBrowser());
            Assert.AreEqual(1, (await client.SearchAsync(new Uri("https://annas.example.test/"), "Pride and Prejudice Austen", CancellationToken.None)).Count);

            var foreign = new AnnasArchiveClient(new HttpClient(sources), resolver, NoBrowser());
            await Assert.ThrowsExactlyAsync<Integrations.Data.AnnasArchive.AnnasArchiveException>(() =>
                foreign.SearchAsync(new Uri("https://other.example.test/"), "Pride and Prejudice Austen", CancellationToken.None));
            var production = new AnnasArchiveClient(new HttpClient(sources), new Integrations.Logic.Common.NoSourceGateResolver(), NoBrowser());
            await Assert.ThrowsExactlyAsync<Integrations.Data.AnnasArchive.AnnasArchiveException>(() =>
                production.SearchAsync(new Uri("https://annas.example.test/"), "Pride and Prejudice Austen", CancellationToken.None));

            // A wrong token is not accepted even for an allowed host.
            var wrong = new AnnasArchiveClient(new HttpClient(sources), new TestGateResolver(["annas.example.test"], "guess"), NoBrowser());
            await Assert.ThrowsExactlyAsync<Integrations.Data.AnnasArchive.AnnasArchiveException>(() =>
                wrong.SearchAsync(new Uri("https://annas.example.test/"), "Pride and Prejudice Austen", CancellationToken.None));
            Assert.IsFalse(new DirectCredentials(new Uri("https://opds.example.test/opds"), "Basic x").Covers(new Uri("https://cdn.example.test/file")));
        }

        [TestMethod]
        [DataRow("https://127.0.0.1/book", false)]
        [DataRow("https://[::1]/book", false)]
        [DataRow("https://192.168.1.3/book", false)]
        [DataRow("http://93.184.216.34/book", false)]
        [DataRow("https://93.184.216.34/book", true)]
        public async Task PublicNetwork_BrowserTargetsAreHttpsAndPublic(string address, bool allowed)
        {
            var action = () => PublicNetwork.EnsurePublicHttpsAsync(new Uri(address), CancellationToken.None);
            if (allowed)
            {
                await action();
            }
            else
            {
                await Assert.ThrowsExactlyAsync<HttpRequestException>(action);
            }
        }

        [TestMethod]
        [DataRow("127.0.0.1", false)]
        [DataRow("10.1.2.3", false)]
        [DataRow("172.20.0.1", false)]
        [DataRow("192.168.1.10", false)]
        [DataRow("169.254.1.1", false)]
        [DataRow("100.64.0.1", false)]
        [DataRow("::1", false)]
        [DataRow("fd00::1", false)]
        [DataRow("fe80::1", false)]
        [DataRow("::ffff:192.168.1.10", false)]
        [DataRow("93.184.216.34", true)]
        [DataRow("2606:4700::1", true)]
        public void PublicNetwork_ReachesPublicAddressesOnly(string address, bool expected) =>
            Assert.AreEqual(expected, PublicNetwork.IsPublic(System.Net.IPAddress.Parse(address)));

        // The source's countdown is moved to the past instead of sleeping in the test.
        private static async Task WaitForSlowPageAsync(Host host)
        {
            await host.Fixture.RunSqlAsync("UPDATE \"Operations\" SET \"DownloadWaitUntil\" = now() - interval '1 second' WHERE \"DownloadWaitUntil\" IS NOT NULL");
            await RunAsync<MonitorDownloadsV1>(host);
        }

        private static async Task GrabAnnaAsync(Host host, long wantedId)
        {
            using var searched = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/wanted/" + wantedId + "/search", new SearchWantedDataV1());
            Assert.AreEqual(HttpStatusCode.OK, searched.StatusCode, await searched.Content.ReadAsStringAsync());
            var anna = (await searched.Content.ReadFromJsonAsync<BaseDataList<WantedReleaseCandidateV1>>())!.Single(candidate => candidate.ConnectionName == "Anna");
            using var grabbed = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/wanted/" + wantedId + "/grab", new GrabWantedReleaseDataV1(anna.CandidateId));
            Assert.AreEqual(HttpStatusCode.OK, grabbed.StatusCode, await grabbed.Content.ReadAsStringAsync());
        }

        private static async Task<RequestDetailsV1> ReadOwnRequestAsync(Host host)
        {
            using var response = await host.SendAsync(HttpMethod.Get, "/api/v1/requests");
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
            return (await response.Content.ReadFromJsonAsync<PageResult<RequestDetailsV1>>())!.Items.Single(item => item.Id == 710);
        }

        private static async Task<IReadOnlyList<SourceCatalogItemV1>> ReadCatalogAsync(Host host)
        {
            using var response = await host.SendAsync(HttpMethod.Get, "/api/v1/admin/acquisition/sources");
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
            return (await response.Content.ReadFromJsonAsync<BaseDataList<SourceCatalogItemV1>>())!;
        }

        private static async Task<Host> CreateHostAsync(string storage, string downloads, FakeSources sources, BrowserAssistanceTests.Interaction? browser = null)
        {
            var host = await LibraryTests.CreateHostAsync(storage, razor: true, settings: new Dictionary<string, string>
            {
                ["Storage:LocalPaths:2"] = downloads
            }, services: services =>
            {
                if (browser is not null)
                {
                    services.AddSingleton<IBrowserInteraction>(browser);
                    services.AddSingleton<ISourceBrowser>(browser);
                }
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
            await DownloadStorageTests.ConfigureAsync(host, downloads, Jularr.Backend.Data.Enums.Storage.DownloadStorageArea.Direct);
            return host;
        }

        [TestMethod]
        public async Task DirectSizeLimit_WaitsAndImportsTheSameJobAfterTheLimitIsRemoved()
        {
            using var storage = new TemporaryDirectory();
            using var downloads = new TemporaryDirectory();
            await using var host = await CreateHostAsync(storage.Path, downloads.Path, new FakeSources());
            using var connection = await host.SendAsync(HttpMethod.Post, Connections, new SaveAcquisitionConnectionDataV1("Calibre", AcquisitionConnectionType.Opds,
                "https://opds.example.test/opds", FakeSources.Password, true, 40, [], null, null, null, FakeSources.User));
            Assert.AreEqual(HttpStatusCode.OK, connection.StatusCode);
            await LimitAsync(1);
            var wantedId = await CreateWantedBookAsync(host);
            using var searched = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/wanted/" + wantedId + "/search", new SearchWantedDataV1());
            var candidate = (await searched.Content.ReadFromJsonAsync<BaseDataList<WantedReleaseCandidateV1>>())!.Single(item => item.ConnectionName == "Calibre");
            using var grabbed = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/wanted/" + wantedId + "/grab", new GrabWantedReleaseDataV1(candidate.CandidateId));
            Assert.AreEqual(HttpStatusCode.OK, grabbed.StatusCode, await grabbed.Content.ReadAsStringAsync());
            await RunAsync<MonitorDownloadsV1>(host);
            Assert.AreEqual("download_size_limit", await host.ScalarAsync("SELECT \"AttentionKey\" FROM \"Operations\" WHERE \"OperationTypeId\" = 2"));
            Assert.AreEqual(0L, await host.ScalarAsync("SELECT count(*) FROM \"Operations\" WHERE \"OperationTypeId\" = 2 AND \"OperationStatusTypeId\" IN (3, 4)"));
            await LimitAsync(null);
            await RunAsync<MonitorDownloadsV1>(host);
            Assert.AreEqual(1L, await host.ScalarAsync("SELECT count(*) FROM \"Operations\" WHERE \"OperationTypeId\" = 2 AND \"OperationStatusTypeId\" = 3"));
            Assert.AreEqual(1L, await host.ScalarAsync("SELECT count(*) FROM \"Operations\" WHERE \"OperationTypeId\" = 2"));

            async Task LimitAsync(long? bytes)
            {
                using var read = await host.SendAsync(HttpMethod.Get, "/api/v1/admin/storage/downloads/2");
                var policy = (await read.Content.ReadFromJsonAsync<DownloadStorageSettingsV1>())!;
                using var saved = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/storage/downloads/2", new SaveDownloadStorageDataV1(policy.IncompleteStorageMountId,
                    policy.IncompletePath, policy.CompleteStorageMountId, policy.CompletePath, policy.FreeSpaceReserveBytes, bytes, policy.MaxWorkspaceBytes,
                    policy.MaxParallelJobs, policy.Version, true));
                Assert.AreEqual(HttpStatusCode.OK, saved.StatusCode, await saved.Content.ReadAsStringAsync());
            }
        }

        [TestMethod]
        public async Task DirectJobs_RunInParallelWithIndependentTransactionsAndImportBoth()
        {
            using var storage = new TemporaryDirectory();
            using var downloads = new TemporaryDirectory();
            var bothStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var started = 0;
            var sources = new FakeSources
            {
                BeforeDownloadAsync = async ct =>
                {
                    if (Interlocked.Increment(ref started) >= 2) bothStarted.TrySetResult();
                    await bothStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
                }
            };
            await using var host = await CreateHostAsync(storage.Path, downloads.Path, sources);
            using var connection = await host.SendAsync(HttpMethod.Post, Connections, new SaveAcquisitionConnectionDataV1("Calibre", AcquisitionConnectionType.Opds,
                "https://opds.example.test/opds", FakeSources.Password, true, 40, [], null, null, null, FakeSources.User));
            Assert.AreEqual(HttpStatusCode.OK, connection.StatusCode);
            foreach (var offset in new[] { 0L, 1L })
            {
                var wantedId = await CreateWantedBookAsync(host, offset);
                using var searched = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/wanted/" + wantedId + "/search", new SearchWantedDataV1());
                var candidate = (await searched.Content.ReadFromJsonAsync<BaseDataList<WantedReleaseCandidateV1>>())!.Single(item => item.ConnectionName == "Calibre");
                using var grabbed = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/wanted/" + wantedId + "/grab", new GrabWantedReleaseDataV1(candidate.CandidateId));
                Assert.AreEqual(HttpStatusCode.OK, grabbed.StatusCode, await grabbed.Content.ReadAsStringAsync());
            }

            await RunAsync<MonitorDownloadsV1>(host);
            Assert.IsTrue(bothStarted.Task.IsCompletedSuccessfully, "Both jobs must reach the HTTP downloader before either is released.");
            // Equal book names may collide; the waiting import must recover without another download.
            await RunAsync<MonitorDownloadsV1>(host);
            Assert.AreEqual(2L, await host.ScalarAsync("SELECT count(*) FROM \"Operations\" WHERE \"OperationTypeId\" = 2 AND \"OperationStatusTypeId\" = 3"));
            Assert.AreEqual(2, Directory.EnumerateFiles(storage.Path, "*.epub", SearchOption.AllDirectories).Count());
        }

        private static async Task<long> CreateWantedBookAsync(Host host, long offset = 0)
        {
            using var created = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/works", new CreateWorkDataV1(MediaType.Book, "Pride and Prejudice", 1813));
            var work = (await created.Content.ReadFromJsonAsync<WorkDetailsV1>())!;
            await host.Fixture.RunSqlAsync($"""
                INSERT INTO "People" ("Id", "Name") VALUES (9001, 'Jane Austen') ON CONFLICT DO NOTHING;
                INSERT INTO "WorkPeople" ("WorkId", "PersonId", "PersonRoleId", "Position") VALUES ({work.Id}, 9001, 7, 1);
                INSERT INTO "AcquisitionRequests" ("Id", "WorkId", "RequestedByProfileId", "RequestedByAccountId", "UiLocaleId", "AcquisitionRequestStatusTypeId", "DecidedByAccountId", "DecidedAt")
                VALUES ({710 + offset}, {work.Id}, 202, 42, 1, 2, 41, now());
                INSERT INTO "WantedItems" ("Id", "WorkId") VALUES ({810 + offset}, {work.Id});
                INSERT INTO "RequestTargets" ("AcquisitionRequestId", "WantedItemId") VALUES ({710 + offset}, {810 + offset});
                """);
            return 810 + offset;
        }

        private static async Task RunAsync<TService>(Host host) where TService : Service.Core.SystemService<AcquisitionCycleParametersV1, NoData>
        {
            await using var scope = host.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<TService>().ExecuteAsync<UpdateResult>(new AcquisitionCycleParametersV1(), default);
        }

        // Without a browser the source keeps its own way.
        private static SourceBrowser NoBrowser() => new(new BrowserOptions { Path = "" });

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
            public bool HtmlGate { get; set; }
            public bool OversizedSearch { get; set; }
            public bool MirrorOnlyPk { get; set; }
            public bool OriginalMemberUnavailable { get; set; }
            public bool SlowGate { get; set; }
            public bool SlowWithoutFile { get; set; }
            public bool RealRows { get; set; }
            public string RowFacts { get; set; } = "English [en] · EPUB · 1.2MB · 1813 · Book (fiction)";
            public string RowFileName { get; set; } = "lgli/Jane Austen - Pride and Prejudice (1813).epub";

            // The page of a search whose query contains the key.
            public Dictionary<string, string> SearchPages { get; } = new(StringComparer.OrdinalIgnoreCase);
            public int SlowWaitSeconds { get; set; } = 1;
            public string? SlowWaitText { get; set; }
            private int _signedHits;
            public int SignedHits => Volatile.Read(ref _signedHits);
            public ConcurrentBag<string> AuthorizationSeenBy { get; } = [];
            public ConcurrentBag<string> Requests { get; } = [];
            public Func<CancellationToken, Task>? BeforeDownloadAsync { get; set; }

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var uri = request.RequestUri!;
                Requests.Add(uri.Host + uri.PathAndQuery);
                if (request.Headers.Authorization is not null)
                {
                    AuthorizationSeenBy.Add(uri.Host);
                }

                if (uri.Host == "cdn.example.test" && BeforeDownloadAsync is { } before) await before(cancellationToken);
                return uri.Host switch
                {
                    "opds.example.test" => Opds(request),
                    "cdn.example.test" => Signed(uri),
                    "annas.example.test" or "other.example.test" or "annas-archive.gl" or "annas-archive.pk" or "annas-archive.org" => Annas(request),
                    "files.example.test" when uri.AbsolutePath == "/expired.epub" => new HttpResponseMessage(HttpStatusCode.Gone),
                    "files.example.test" => Bytes(ReadingTests.BuildEpub("Pride and Prejudice")),
                    _ => Text("""{"queue":{"paused":false,"slots":[]},"history":{"slots":[]},"version":"4.5.0"}""", "application/json")
                };
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
                return Interlocked.Increment(ref _signedHits) == 1 ? new HttpResponseMessage(HttpStatusCode.Gone) : Bytes(ReadingTests.BuildEpub("Pride and Prejudice"));
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

                if (OversizedSearch && uri.AbsolutePath == "/search")
                {
                    return Text(new string('A', 4 * 1024 * 1024 + 1), "text/html");
                }

                if (HtmlGate && uri.AbsolutePath == "/search")
                {
                    return Text("<html><head><title>Just a moment...</title></head><body>challenge-platform</body></html>", "text/html");
                }

                if (OriginalMemberUnavailable && uri.Host == "annas-archive.org" && uri.AbsolutePath == "/dyn/api/fast_download.json")
                {
                    return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                }

                if (MirrorOnlyPk && uri.AbsolutePath == "/search" && uri.Host != "annas-archive.pk")
                {
                    return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                }

                // The simulated slow download shows a check or a countdown with the file link.
                if (uri.AbsolutePath.StartsWith("/slow_download/0123456789abcdef0123456789abcdef/0/", StringComparison.Ordinal))
                {
                    if (!SlowGate)
                    {
                        var wait = SlowWaitText ?? $"Please wait {SlowWaitSeconds} seconds before downloading.";
                        var link = SlowWithoutFile ? "" : """<a href="https://files.example.test/0123.epub">Download now</a>""";
                        return Text($"""<html><body><p>{wait}</p><a href="/md5/0123456789abcdef0123456789abcdef">Back</a>{link}</body></html>""", "text/html");
                    }

                    var check = Text("<html><body>Checking your browser</body></html>", "text/html", HttpStatusCode.ServiceUnavailable);
                    check.Headers.Add("cf-mitigated", "challenge");
                    return check;
                }

                if (RealRows && uri.AbsolutePath == "/search" && !SearchPages.Keys.Any(key => uri.Query.Contains(key, StringComparison.OrdinalIgnoreCase)))
                {
                    return Text($"""
                        <html><body>
                          <div class="flex pt-3 pb-3 border-b">
                            <a href="/md5/0123456789abcdef0123456789abcdef" class="custom-a block"><img src="/cover.jpg"></a>
                            <div>
                              <div class="line-clamp-[2] text-[9px] font-mono">{RowFileName}</div>
                              <a href="/md5/0123456789abcdef0123456789abcdef" class="js-vim-focus">Pride and Prejudice</a>
                              <a href="/search?q=Jane Austen" class="custom-a text-sm"><span class="icon-[mdi--user-edit]"></span> Jane Austen</a>
                              <a href="/search?q=" class="custom-a text-sm"><span class="icon-[mdi--company]"></span> 1813</a>
                            </div>
                            <div>{RowFacts}</div>
                          </div>
                        </body></html>
                        """, "text/html");
                }

                return uri.AbsolutePath switch
                {
                    "/book.epub" => Text("<html><body>the site's page in place of the file</body></html>", "text/html"),
                    "/search" => Text(SearchPages.Where(entry => uri.Query.Contains(entry.Key, StringComparison.OrdinalIgnoreCase))
                        .Select(entry => entry.Value).FirstOrDefault() ?? """
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
