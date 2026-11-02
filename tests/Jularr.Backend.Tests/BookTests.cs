using System.Net;
using System.Net.Http.Json;
using System.Collections.Concurrent;
using System.Text;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Acquisition.V1;
using Jularr.Backend.Data.Admin.Activity.V1;
using Jularr.Backend.Data.Admin.Media.V1;
using Jularr.Backend.Data.Admin.Wanted.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.User.Discover.V1;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Enums.Operation;
using Jularr.Backend.Data.Logic.Acquisition;
using Jularr.Backend.Data.System.Acquisition.V1;
using Jularr.Backend.Logic.Acquisition;
using Jularr.Backend.Logic.Library;
using Jularr.Backend.Service.System.Acquisition.V1;
using Jularr.Integrations.Logic.Gutenberg;
using Jularr.Integrations.Logic.Hardcover;
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
        [TestMethod]
        public void BookDocumentLanguages_VerifiesExplicitEpubMetadataWithoutGuessing()
        {
            var bytes = ReadingTests.BuildEpub("Pride and Prejudice");
            using var data = new MemoryStream(bytes);
            CollectionAssert.AreEqual(new[] { "EN" }, BookDocumentLanguages.ReadEpub(data));

            var strict = new QualityProfile("strict", "Strict", ["EPUB"], ["EPUB"], false, null, null, null)
            {
                IsBook = true, LanguageOrder = ["DE"], LanguageFallback = LanguageFallbackType.None
            };
            Assert.IsTrue(BookDocumentLanguages.ContradictsStrictProfile(strict, ["EN"]));
            Assert.IsFalse(BookDocumentLanguages.ContradictsStrictProfile(strict, ["DE", "EN"]));
            Assert.IsFalse(BookDocumentLanguages.ContradictsStrictProfile(strict, []));
            Assert.AreEqual("import_book_language_unknown", BookDocumentLanguages.StrictEpubAttentionReason(strict, []));
            Assert.AreEqual("import_book_language_mismatch", BookDocumentLanguages.StrictEpubAttentionReason(strict, ["EN"]));
            Assert.IsNull(BookDocumentLanguages.StrictEpubAttentionReason(strict, ["DE"]));
            Assert.IsNull(BookDocumentLanguages.StrictEpubAttentionReason(strict, ["DE", "EN"]));
            var flexible = new QualityProfile("flexible", "Flexible", ["EPUB"], ["EPUB"], false, null, null, null)
            {
                IsBook = true, LanguageOrder = ["DE"], LanguageFallback = LanguageFallbackType.Any
            };
            Assert.IsNull(BookDocumentLanguages.StrictEpubAttentionReason(flexible, []));
            Assert.IsNull(BookDocumentLanguages.StrictEpubAttentionReason(flexible, ["EN"]));
            using var unknown = new MemoryStream("not an epub"u8.ToArray());
            CollectionAssert.AreEqual(Array.Empty<string>(), BookDocumentLanguages.ReadEpub(unknown));
        }

        [TestMethod]
        public void StrictEpubWithoutLanguageMetadata_RequiresManualReview()
        {
            using var stream = new MemoryStream(ReadingTests.BuildEpub("Unlabelled document"));
            using (var archive = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Update, leaveOpen: true))
            {
                var opf = archive.GetEntry("OEBPS/content.opf")!;
                string xml;
                using (var reader = new StreamReader(opf.Open(), leaveOpen: false))
                {
                    xml = reader.ReadToEnd();
                }
                opf.Delete();
                using var writer = new StreamWriter(archive.CreateEntry("OEBPS/content.opf").Open());
                writer.Write(xml.Replace("<dc:language>en</dc:language>", "", StringComparison.Ordinal));
            }

            stream.Position = 0;
            var actual = BookDocumentLanguages.ReadEpub(stream);
            CollectionAssert.AreEqual(Array.Empty<string>(), actual);
            var strict = new QualityProfile("strict", "Strict", ["EPUB"], ["EPUB"], false, null, null, null)
            {
                IsBook = true, LanguageOrder = ["DE"], LanguageFallback = LanguageFallbackType.None
            };
            Assert.AreEqual("import_book_language_unknown", BookDocumentLanguages.StrictEpubAttentionReason(strict, actual));
        }

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
            Assert.AreEqual("Jane Austen", await host.ScalarAsync("SELECT person.\"Name\" FROM \"WorkPeople\" AS credit JOIN \"People\" AS person ON person.\"Id\" = credit.\"PersonId\" WHERE credit.\"PersonRoleId\" = 7"));
            Assert.AreEqual("OL21594A", await host.ScalarAsync("SELECT \"Value\" FROM \"PersonExternalIdentities\" WHERE \"Namespace\" = 'openlibrary'"));
            var wantedId = await CreateWantedAsync(host, work.Id);

            // Identity before format: other authors and extra titles wait for a person.
            using var searched = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/wanted/" + wantedId + "/search", new SearchWantedDataV1());
            Assert.AreEqual(HttpStatusCode.OK, searched.StatusCode, await searched.Content.ReadAsStringAsync());
            var candidates = (await searched.Content.ReadFromJsonAsync<BaseDataList<WantedReleaseCandidateV1>>())!;
            var gutenberg = candidates.Single(candidate => candidate.ConnectionName == "Project Gutenberg" && candidate.Title.Contains("Pride and Prejudice (EN)", StringComparison.Ordinal));
            Assert.AreEqual(ReleaseDecisionType.Eligible, gutenberg.ReleaseDecisionTypeId);
            Assert.AreEqual("EPUB", gutenberg.QualityKey);
            Assert.AreNotEqual(ReleaseDecisionType.Eligible, candidates.Single(candidate => candidate.Title.StartsWith("Seth Grahame", StringComparison.Ordinal)).ReleaseDecisionTypeId);
            var retold = candidates.Single(candidate => candidate.Title.StartsWith("Pride.and.Prejudice.Retold", StringComparison.Ordinal));
            Assert.AreEqual(("ExtraTitleWords", "retold"), (retold.ReasonCode, retold.ReasonValue));
            Assert.AreEqual(BookReleaseKind.Ebook, gutenberg.BookReleaseKindId);
            Assert.IsTrue(gutenberg.ScorePoints.Any(point => point.Code == "Format" && point.Value == "EPUB" && point.Points == 40));
            Assert.AreEqual("PDF", candidates.Single(candidate => candidate.Title.StartsWith("Jane.Austen", StringComparison.Ordinal)).QualityKey);

            // Jularr downloads the catalog book itself and imports it like any job.
            using var grabbed = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/wanted/" + wantedId + "/grab", new GrabWantedReleaseDataV1(gutenberg.CandidateId));
            Assert.AreEqual(HttpStatusCode.OK, grabbed.StatusCode, await grabbed.Content.ReadAsStringAsync());
            Assert.AreEqual(0, sources.SabGrabs);
            await RunAsync<MonitorDownloadsV1>(host);
            var book = Path.Combine(storage.Path, "1", "Books", "Jane Austen", "Pride and Prejudice (1813) {olid-OL66554W}", "Pride and Prejudice {olid-OL66554W} [en].epub");
            Assert.IsTrue(File.Exists(book));
            Assert.AreEqual(1L, await host.ScalarAsync("SELECT count(*) FROM \"StoredFiles\" WHERE \"MediaAssetId\" IS NOT NULL"));
            Assert.AreEqual((short)3, await host.ScalarAsync("SELECT \"OperationStatusTypeId\" FROM \"Operations\" WHERE \"OperationTypeId\" = 2"));
            Assert.AreEqual(0, Directory.EnumerateFiles(downloads.Path, "*", SearchOption.AllDirectories).Count());
        }

        [TestMethod]
        public async Task Scan_BookFolders_TakeTheWorkOfTheirTitle_FromTheCatalogOrFromHere()
        {
            using var storage = new TemporaryDirectory();
            var sources = new FakeBookSources();
            await using var host = await LibraryTests.CreateHostAsync(storage.Path, services: services =>
            {
                services.AddHttpClient<OpenLibraryClient>().ConfigurePrimaryHttpMessageHandler(() => sources);
                services.AddHttpClient<HardcoverClient>().ConfigurePrimaryHttpMessageHandler(() => sources);
            });
            var mountId = await LibraryTests.CreateMountAsync(host);
            var books = Path.Combine(storage.Path, mountId.ToString(System.Globalization.CultureInfo.InvariantCulture), "Books");
            Directory.CreateDirectory(Path.Combine(books, "Pride and Prejudice"));
            File.WriteAllText(Path.Combine(books, "Pride and Prejudice", "Jane Austen - Pride and Prejudice.epub"), "book");
            // Folders that name the author first, as downloads do, still name their book.
            Directory.CreateDirectory(Path.Combine(books, "Austen, Jane - Pride and Prejudice (epub)"));
            File.WriteAllText(Path.Combine(books, "Austen, Jane - Pride and Prejudice (epub)", "Austen, Jane - Pride and Prejudice.epub"), "book");
            // A download's release name carries the title of a Work that is already here.
            await host.Fixture.RunSqlAsync("""INSERT INTO "Works" ("MediaTypeId", "CanonicalTitle") VALUES (3, 'Das Kind in dir muss Heimat finden');""");
            const string release = "Stefanie.Stahl.Das.Kind.in.dir.muss.Heimat.finden.GERMAN.2017.RETAiL.ePub.eBook-VENTOLiN";
            Directory.CreateDirectory(Path.Combine(books, release));
            File.WriteAllText(Path.Combine(books, release, release + ".epub"), "book");
            Directory.CreateDirectory(Path.Combine(books, "Unknown Title Nobody Lists"));
            File.WriteAllText(Path.Combine(books, "Unknown Title Nobody Lists", "Somebody - Unknown Title Nobody Lists.epub"), "book");
            var rootId = await LibraryTests.CreateRootAsync(host, mountId, "Books", MediaType.Book);

            // The catalog names exactly one entry with the folder's title; a title it does not list stays for a person.
            await LibraryTests.ScanAsync(host, rootId);
            Assert.AreEqual(3L, await host.ScalarAsync("""SELECT count(*) FROM "StoredFiles" WHERE "MediaAssetId" IS NOT NULL """));
            Assert.AreEqual(1L, await host.ScalarAsync("""SELECT count(*) FROM "Works" WHERE "CanonicalTitle" = 'Pride and Prejudice' """));
            Assert.AreEqual(1L, await host.ScalarAsync("""
                SELECT count(*) FROM "StoredFiles" AS file JOIN "MediaAssets" AS asset ON asset."Id" = file."MediaAssetId" JOIN "Works" AS work ON work."Id" = asset."WorkId"
                WHERE work."CanonicalTitle" = 'Das Kind in dir muss Heimat finden'
                """));

            // A second copy of a title that is now here is the same Work, without asking the catalog again.
            File.WriteAllText(Path.Combine(books, "Pride and Prejudice", "Pride and Prejudice (Illustrated).epub"), "book");
            await LibraryTests.ScanAsync(host, rootId);
            Assert.AreEqual(4L, await host.ScalarAsync("""SELECT count(*) FROM "StoredFiles" WHERE "MediaAssetId" IS NOT NULL """));
            Assert.AreEqual(2L, await host.ScalarAsync("""SELECT count(*) FROM "Works" """));
        }

        [TestMethod]
        public async Task Unmatched_FileIsIdentifiedAsACatalogEntry_ThroughTheWorkSearch()
        {
            using var storage = new TemporaryDirectory();
            var sources = new FakeBookSources();
            await using var host = await LibraryTests.CreateHostAsync(storage.Path, razor: true, services: services =>
            {
                services.AddHttpClient<OpenLibraryClient>().ConfigurePrimaryHttpMessageHandler(() => sources);
                services.AddHttpClient<HardcoverClient>().ConfigurePrimaryHttpMessageHandler(() => sources);
            });
            var mountId = await LibraryTests.CreateMountAsync(host);
            var folder = Path.Combine(storage.Path, mountId.ToString(System.Globalization.CultureInfo.InvariantCulture), "Books", "Mystery Folder");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "Mystery.epub"), "book");
            var rootId = await LibraryTests.CreateRootAsync(host, mountId, "Books", MediaType.Book);
            await LibraryTests.ScanAsync(host, rootId);
            var fileId = (long)(await host.ScalarAsync("""SELECT "Id" FROM "StoredFiles" """))!;
            Assert.AreEqual(0L, await host.ScalarAsync("""SELECT count(*) FROM "Works" """));

            // The row offers the search; the search form starts with the folder's title.
            using var page = await host.SendAsync(HttpMethod.Get, "/Admin/Library/Unmatched?find=" + fileId);
            var html = await page.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, page.StatusCode, html);
            StringAssert.Contains(html, "handler=FindWork");
            StringAssert.Contains(html, "value=\"Mystery Folder\"");

            // The catalog's entries are listed; choosing one makes it the Work.
            using var found = await host.SendAsync(HttpMethod.Post, "/Admin/Library/Unmatched?PageNumber=1&handler=FindWork&storedFileId=" + fileId + "&find=" + fileId, new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = LibraryTests.Token(html), ["MediaTypeId"] = "3", ["Query"] = "Pride and Prejudice"
            });
            html = await found.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, found.StatusCode, html);
            StringAssert.Contains(html, "Jane Austen");
            StringAssert.Contains(html, "handler=AssignCatalog");
            using var assigned = await host.SendAsync(HttpMethod.Post, "/Admin/Library/Unmatched?PageNumber=1&handler=AssignCatalog&storedFileId=" + fileId, new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = LibraryTests.Token(html), ["Namespace"] = "openlibrary", ["ExternalId"] = "OL66554W"
            });
            Assert.AreEqual(HttpStatusCode.Redirect, assigned.StatusCode, await assigned.Content.ReadAsStringAsync());
            Assert.AreEqual(1L, await host.ScalarAsync("""SELECT count(*) FROM "Works" WHERE "CanonicalTitle" = 'Pride and Prejudice' AND "MediaTypeId" = 3 """));
            Assert.AreEqual(1L, await host.ScalarAsync("""
                SELECT count(*) FROM "StoredFiles" AS file JOIN "MediaAssets" AS asset ON asset."Id" = file."MediaAssetId" JOIN "WorkExternalIdentities" AS identity ON identity."WorkId" = asset."WorkId"
                WHERE identity."Namespace" = 'openlibrary' AND identity."Value" = 'OL66554W'
                """));
        }

        [TestMethod]
        [DataRow(true)]
        [DataRow(false)]
        [Timeout(300_000)]
        public async Task Book_SlowDirectSource_RespectsSearchBudgetAndRetainsOtherResults(bool withIndexer)
        {
            using var storage = new TemporaryDirectory();
            using var downloads = new TemporaryDirectory();
            var sources = new FakeBookSources();
            await using var host = await CreateHostAsync(storage.Path, downloads.Path, sources);
            using var imported = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/works/metadata", new ImportMetadataWorkDataV1(MediaType.Book, "OL66554W"));
            var work = (await imported.Content.ReadFromJsonAsync<WorkDetailsV1>())!;
            var wantedId = await CreateWantedAsync(host, work.Id);
            if (!withIndexer)
            {
                await host.Fixture.RunSqlAsync("UPDATE \"AcquisitionConnections\" SET \"IsEnabled\" = FALSE WHERE \"AcquisitionConnectionTypeId\" = 1");
            }

            sources.BlockDirectSearch = true;
            var pending = host.SendAsync(HttpMethod.Post, "/api/v1/admin/wanted/" + wantedId + "/search", new SearchWantedDataV1());
            HttpResponseMessage response;
            try { response = await pending.WaitAsync(TimeSpan.FromSeconds(35)); }
            catch
            {
                host.Client.CancelPendingRequests();
                try { using var ignored = await pending; }
                catch (OperationCanceledException) { }
                throw;
            }
            using var finished = response;
            Assert.IsTrue(sources.DirectSearchCancelled, "The direct source must receive the search budget's cancellation.");
            if (withIndexer)
            {
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
                var candidates = (await response.Content.ReadFromJsonAsync<BaseDataList<WantedReleaseCandidateV1>>())!;
                Assert.IsTrue(candidates.Any(candidate => candidate.ConnectionName == "Indexer"));
                Assert.IsFalse(candidates.Any(candidate => candidate.ConnectionName == "Project Gutenberg"));
            }
            else
            {
                Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode);
                StringAssert.Contains(await response.Content.ReadAsStringAsync(), "source_timeout");
            }
        }

        [TestMethod]
        public async Task Book_TheIndexerAndTheDirectSource_AreAskedAtTheSameTime()
        {
            using var storage = new TemporaryDirectory();
            using var downloads = new TemporaryDirectory();
            var sources = new FakeBookSources();
            await using var host = await CreateHostAsync(storage.Path, downloads.Path, sources);
            using var imported = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/works/metadata", new ImportMetadataWorkDataV1(MediaType.Book, "OL66554W"));
            var work = (await imported.Content.ReadFromJsonAsync<WorkDetailsV1>())!;
            await CreateWantedAsync(host, work.Id);

            // Each source waits for the other (not while the host is set up, where nothing asks the indexer); both only answer when they are asked at the same time.
            sources.StartCounting();
            await RunAsync<SearchDueWantedV1>(host);
            var order = sources.Order.ToArray();
            var direct = Array.IndexOf(order, "direct");
            var indexer = Array.IndexOf(order, "indexer");
            var directEnd = Array.IndexOf(order, "direct-end");
            var indexerEnd = Array.IndexOf(order, "indexer-end");

            // Each of them was asked before the other had answered, whichever of the two arrived first.
            Assert.IsTrue(direct >= 0 && indexer >= 0 && indexer < directEnd && direct < indexerEnd, "the indexer and the direct source were not asked at the same time: " + string.Join(",", order));
        }

        [TestMethod]
        public async Task Book_ContentThatIsNoEbook_WaitsForManualReviewAndStaysOutOfTheLibrary()
        {
            using var storage = new TemporaryDirectory();
            using var downloads = new TemporaryDirectory();
            var sources = new FakeBookSources { EpubBytes = Encoding.UTF8.GetBytes("<html>not a book</html>") };
            await using var host = await CreateHostAsync(storage.Path, downloads.Path, sources);
            using var imported = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/works/metadata", new ImportMetadataWorkDataV1(MediaType.Book, "OL66554W"));
            var work = (await imported.Content.ReadFromJsonAsync<WorkDetailsV1>())!;
            var wantedId = await CreateWantedAsync(host, work.Id);

            // The direct source is preferred, but invalid EPUB content must not enter the library.
            using var preferred = await host.SendAsync(HttpMethod.Put, Connections + "/3",
                new SaveAcquisitionConnectionDataV1("Project Gutenberg", AcquisitionConnectionType.Gutenberg, "https://gutendex.example.test", null, true, 5, [], null, null, null));
            Assert.AreEqual(HttpStatusCode.OK, preferred.StatusCode, await preferred.Content.ReadAsStringAsync());

            await RunAsync<SearchDueWantedV1>(host);
            Assert.AreEqual(0, sources.SabGrabs);
            await RunAsync<MonitorDownloadsV1>(host);

            // A download with no placeable book now waits for a person instead of failing and deleting its files.
            Assert.AreEqual((short)2, await host.ScalarAsync("SELECT \"OperationStatusTypeId\" FROM \"Operations\" WHERE \"OperationTypeId\" = 2"));
            Assert.AreEqual("import_nothing_placeable", await host.ScalarAsync("SELECT \"AttentionKey\" FROM \"Operations\" WHERE \"OperationTypeId\" = 2"));
            Assert.AreEqual(true, await host.ScalarAsync("SELECT \"ReasonKey\" IS NULL FROM \"AcquisitionReleases\""));
            StringAssert.Contains((string)(await host.ScalarAsync("SELECT \"AttentionProposal\"::text FROM \"Operations\" WHERE \"OperationTypeId\" = 2"))!, ".epub");
            Assert.IsFalse(Directory.Exists(Path.Combine(storage.Path, "1", "Books", "Jane Austen")));
            Assert.AreEqual(0L, await host.ScalarAsync("SELECT count(*) FROM \"StoredFiles\" WHERE \"MediaAssetId\" IS NOT NULL"));

            var kept = Directory.EnumerateFiles(downloads.Path, "*", SearchOption.AllDirectories).Single();
            CollectionAssert.AreEqual(sources.EpubBytes, File.ReadAllBytes(kept), "The downloaded bytes must remain available for manual review.");
            using var activity = await host.SendAsync(HttpMethod.Get, "/api/v1/admin/activity?activityTabTypeId=Queue");
            Assert.AreEqual(OperationType.Download, (await activity.Content.ReadFromJsonAsync<PageResult<ActivityItemV1>>())!.Items.Single().OperationTypeId);
            Assert.AreEqual(wantedId, (long)(await host.ScalarAsync("SELECT \"WantedItemId\" FROM \"AcquisitionReleases\""))!);
        }

        [TestMethod]
        public void Book_RulesForNamesCategoriesAndContent()
        {
            var german = BookReleaseJudge.Judge("Pride and Prejudice", "Jane Austen",
                new ReleaseOffer("Jane.Austen.Pride.and.Prejudice.German.EPUB", 1, "Indexer", 25, "de", new Uri("https://indexer.example.test/de"), null, 1_000_000, DateTimeOffset.UtcNow, ["title"]));
            CollectionAssert.AreEqual(new[] { "DE" }, german.Languages!.ToArray());
            CollectionAssert.AreEqual(new[] { "DE" }, ReleaseWords.ReadLanguages("Jane Austen - Pride and Prejudice (DE) (epub)"));
            CollectionAssert.AreEqual(new[] { "EN" }, ReleaseWords.ReadLanguages("An English Book (pdf)", "JA"));

            Assert.AreEqual("AZW3", BookReleaseJudge.Parse("Author - Title (2019) [azw3]").DocumentFormat);
            Assert.AreEqual(BookReleaseJudge.UnknownFormat, BookReleaseJudge.Parse("Author - Title (2019) retail").DocumentFormat);
            CollectionAssert.AreEqual(new[] { 7020 }, AcquisitionSelectionLogic.ReadCategories(AcquisitionKind.Book, [2040, 5040, 7020], null));
            CollectionAssert.AreEqual(new[] { 7000 }, AcquisitionSelectionLogic.ReadCategories(AcquisitionKind.Book, [2040], null));
            CollectionAssert.AreEqual(new[] { 2040 }, AcquisitionSelectionLogic.ReadCategories(AcquisitionKind.Movie, [2040, 7020], null));
            Assert.IsTrue(LibraryFileTypes.IsDocumentContent(".epub", ReadingTests.BuildEpub("x").AsSpan(0, 128)));
            Assert.IsTrue(LibraryFileTypes.IsDocumentContent(".pdf", "%PDF-1.7"u8));
            Assert.IsFalse(LibraryFileTypes.IsDocumentContent(".epub", "PK\u0003\u0004 something else entirely here..........................."u8));
            Assert.IsFalse(LibraryFileTypes.IsDocumentContent(".pdf", "<html>"u8));
        }

        [TestMethod]
        public async Task Discover_ABookIsFoundInItsCatalog_AndRequestedByAUser()
        {
            using var storage = new TemporaryDirectory();
            using var downloads = new TemporaryDirectory();
            await using var host = await CreateHostAsync(storage.Path, downloads.Path, new FakeBookSources(), razor: true);

            // The catalog finds the book; films stay with TMDB.
            async Task<DiscoverCatalogItemV1> FindAsync()
            {
                using var found = await host.SendAsync(HttpMethod.Post, "/api/v1/discover/catalog/search", new SearchDiscoverCatalogDataV1(MediaType.Book, "Pride and Prejudice"));
                Assert.AreEqual(HttpStatusCode.OK, found.StatusCode, await found.Content.ReadAsStringAsync());
                return (await found.Content.ReadFromJsonAsync<BaseDataList<DiscoverCatalogItemV1>>())!.First(item => item.ExternalId == "OL66554W");
            }

            // The found book is kept as a catalog Work; nobody asked for it yet.
            var book = await FindAsync();
            var workId = (long)(await host.ScalarAsync("SELECT \"WorkId\" FROM \"WorkExternalIdentities\" WHERE \"Namespace\" = 'openlibrary' AND \"Value\" = 'OL66554W'"))!;
            Assert.AreEqual((workId, MediaStateType.NotRequested), (book.WorkId, book.MediaStateTypeId));
            using var films = await host.SendAsync(HttpMethod.Post, "/api/v1/discover/catalog/search", new SearchDiscoverCatalogDataV1(MediaType.Movie, "Pride and Prejudice"));
            Assert.AreEqual(HttpStatusCode.BadRequest, films.StatusCode);

            // The page lists it with its Work's page, which asks for the language.
            using var page = await host.SendAsync(HttpMethod.Get, "/?type=books&q=Pride%20and%20Prejudice");
            var html = await page.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, page.StatusCode, html);
            StringAssert.Contains(html, "href=\"/Details/" + workId + "\"");
            Assert.IsFalse(html.Contains("/Discover/books/", StringComparison.Ordinal));

            // The Work's page brings the book's details on its first open and names it as they do in the profile's language, whatever a link claims.
            using var item = await host.SendAsync(HttpMethod.Get, "/Details/" + workId + "?title=Confirm%20your%20password");
            var form = await item.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, item.StatusCode, form);
            StringAssert.Contains(form, "Stolz und Vorurteil");
            Assert.IsFalse(form.Contains("Confirm your password", StringComparison.Ordinal));

            // Requesting asks for that same Work; the catalog still answers with it.
            using var requested = await host.SendAsync(HttpMethod.Post, "/Details/" + workId + "?handler=Request",
                new Dictionary<string, string> { ["__RequestVerificationToken"] = LibraryTests.Token(form), ["UiLocaleId"] = "1" });
            Assert.AreEqual(HttpStatusCode.Redirect, requested.StatusCode, await requested.Content.ReadAsStringAsync());
            Assert.AreEqual(1L, await host.ScalarAsync($"SELECT count(*) FROM \"AcquisitionRequests\" WHERE \"WorkId\" = {workId}"));
            Assert.AreEqual(1L, await host.ScalarAsync("SELECT count(*) FROM \"WorkExternalIdentities\" WHERE \"Namespace\" = 'openlibrary' AND \"Value\" = 'OL66554W'"));
            var asked = await FindAsync();
            Assert.AreEqual((workId, MediaStateType.Requested), (asked.WorkId, asked.MediaStateTypeId));

            // Books trend globally on Open Library; other rankings are not offered.
            using var trending = await host.SendAsync(HttpMethod.Post, "/api/v1/discover/catalog/feed", new ReadDiscoverCatalogFeedDataV1(MediaType.Book, DiscoverFeedType.Trending));
            Assert.AreEqual(HttpStatusCode.OK, trending.StatusCode, await trending.Content.ReadAsStringAsync());
            var trend = (await trending.Content.ReadFromJsonAsync<BaseDataList<DiscoverCatalogItemV1>>())!.Single();
            Assert.AreEqual((workId, "https://covers.openlibrary.org/b/id/14348537-L.jpg"), (trend.WorkId, trend.PosterUrl));
            using var top = await host.SendAsync(HttpMethod.Post, "/api/v1/discover/catalog/feed", new ReadDiscoverCatalogFeedDataV1(MediaType.Book, DiscoverFeedType.Top));
            Assert.AreEqual(HttpStatusCode.BadRequest, top.StatusCode);
            using var shelves = await host.SendAsync(HttpMethod.Get, "/?type=books&handler=Recommendations");
            var landing = await shelves.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, shelves.StatusCode, landing);
            StringAssert.Contains(landing, "/?type=books&amp;view=Trending");
            StringAssert.Contains(landing, "/Details/" + workId);
        }

        private static async Task<Host> CreateHostAsync(string storage, string downloads, FakeBookSources sources, bool razor = false)
        {
            var host = await LibraryTests.CreateHostAsync(storage, razor, settings: new Dictionary<string, string>
            {
                ["Storage:LocalPaths:2"] = downloads
            }, services: services =>
            {
                services.AddHttpClient<NewznabClient>().ConfigurePrimaryHttpMessageHandler(() => sources);
                services.AddHttpClient<SabnzbdClient>().ConfigurePrimaryHttpMessageHandler(() => sources);
                services.AddHttpClient<OpenLibraryClient>().ConfigurePrimaryHttpMessageHandler(() => sources);
                services.AddHttpClient<GutenbergClient>().ConfigurePrimaryHttpMessageHandler(() => sources);
                services.AddHttpClient<HardcoverClient>().ConfigurePrimaryHttpMessageHandler(() => sources);
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
            await DownloadStorageTests.ConfigureAsync(host, downloads, Jularr.Backend.Data.Enums.Storage.DownloadStorageArea.Direct);
            return host;
        }

        [TestMethod]
        public async Task Book_Editions_KeepLanguageFormatAndIsbn_AndARequestFindsItsLanguagesTitle()
        {
            using var storage = new TemporaryDirectory();
            using var downloads = new TemporaryDirectory();
            await using var host = await CreateHostAsync(storage.Path, downloads.Path, new FakeBookSources());
            using var imported = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/works/metadata", new ImportMetadataWorkDataV1(MediaType.Book, "OL66554W"));
            Assert.AreEqual(HttpStatusCode.OK, imported.StatusCode, await imported.Content.ReadAsStringAsync());
            var work = (await imported.Content.ReadFromJsonAsync<WorkDetailsV1>())!;

            // One edition per language and format, the one with an ISBN first; one without a language is left out. The translator belongs to their edition.
            Assert.AreEqual("de:2:Stolz und Vorurteil:Anaconda:2014, en:1:Pride and Prejudice:Penguin:2003", await host.ScalarAsync("""
                SELECT string_agg(concat_ws(':', "LanguageCode", "EditionFormatId", "Title", "Publisher", "ReleaseYear"), ', ' ORDER BY "LanguageCode")
                FROM "WorkEditions" WHERE "LanguageCode" IS NOT NULL
                """));
            Assert.AreEqual("9783730601442", await host.ScalarAsync("""
                SELECT identity."Value" FROM "WorkEditionExternalIdentities" AS identity JOIN "WorkEditions" AS edition ON edition."Id" = identity."WorkEditionId"
                WHERE edition."LanguageCode" = 'de' AND identity."Namespace" = 'isbn'
                """));
            Assert.AreEqual("Werner Beck", await host.ScalarAsync("""
                SELECT person."Name" FROM "WorkPeople" AS credit JOIN "People" AS person ON person."Id" = credit."PersonId"
                JOIN "WorkEditions" AS edition ON edition."Id" = credit."WorkEditionId" WHERE credit."PersonRoleId" = 12 AND edition."LanguageCode" = 'de'
                """));
            using var credits = await host.SendAsync(HttpMethod.Get, "/api/v1/works/" + work.Id + "/credits");
            CollectionAssert.AreEqual(new[] { "Jane Austen" }, (await credits.Content.ReadFromJsonAsync<BaseDataList<Jularr.Backend.Data.User.Media.V1.WorkCreditV1>>())!.Select(person => person.Name).ToArray());

            // Asked for in German, the German edition's title matches its releases; in English they stay another book.
            var wantedId = await CreateWantedAsync(host, work.Id);
            await host.Fixture.RunSqlAsync("UPDATE \"AcquisitionRequests\" SET \"UiLocaleId\" = (SELECT \"Id\" FROM \"UiLocales\" WHERE \"Locale\" = 'de-DE')");
            using var searched = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/wanted/" + wantedId + "/search", new SearchWantedDataV1());
            var candidates = (await searched.Content.ReadFromJsonAsync<BaseDataList<WantedReleaseCandidateV1>>())!;
            var translated = candidates.Single(candidate => candidate.Title.StartsWith("Stolz.und", StringComparison.Ordinal));
            Assert.AreEqual((ReleaseDecisionType.Eligible, "EPUB"), (translated.ReleaseDecisionTypeId, translated.QualityKey));
            await host.Fixture.RunSqlAsync("UPDATE \"AcquisitionRequests\" SET \"UiLocaleId\" = (SELECT \"Id\" FROM \"UiLocales\" WHERE \"Locale\" = 'en-GB')");
            using var english = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/wanted/" + wantedId + "/search", new SearchWantedDataV1());
            Assert.AreEqual(ReleaseDecisionType.Rejected, (await english.Content.ReadFromJsonAsync<BaseDataList<WantedReleaseCandidateV1>>())!
                .Single(candidate => candidate.Title.StartsWith("Stolz.und", StringComparison.Ordinal)).ReleaseDecisionTypeId);
        }

        [TestMethod]
        public async Task Book_ReadsInTheProfilesLanguage_AndWithoutAnEditionInItTakesTheOriginalImmediately()
        {
            using var storage = new TemporaryDirectory();
            using var downloads = new TemporaryDirectory();
            await using var host = await CreateHostAsync(storage.Path, downloads.Path, new FakeBookSources());
            using var imported = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/works/metadata", new ImportMetadataWorkDataV1(MediaType.Book, "OL66554W"));
            var work = (await imported.Content.ReadFromJsonAsync<WorkDetailsV1>())!;

            // The earliest edition names the language it was written in; a German profile sees the German edition's title, an English one the book's own.
            Assert.AreEqual("en", await host.ScalarAsync("SELECT \"OriginalLanguage\" FROM \"Works\" WHERE \"Id\" = " + work.Id));
            foreach (var (locale, title) in new[] { ("de-DE", "Stolz und Vorurteil"), ("en-GB", "Pride and Prejudice") })
            {
                await host.Fixture.RunSqlAsync("UPDATE \"Profiles\" SET \"UiLocaleId\" = (SELECT \"Id\" FROM \"UiLocales\" WHERE \"Locale\" = '" + locale + "') WHERE \"Id\" = 101");
                Assert.AreEqual(title, await host.ScalarAsync("SELECT \"Title\" FROM \"ProfileWorks\" WHERE \"ProfileId\" = 101 AND \"WorkId\" = " + work.Id), locale);
            }

            // Asked for in French, which it was never published in: the automatic search takes the book in its own language immediately.
            await host.Fixture.RunSqlAsync("""
                INSERT INTO "UiLocales" ("Locale", "Name") VALUES ('fr-FR', 'FranÃ§ais');
                """);
            var wantedId = await CreateWantedAsync(host, work.Id);
            await host.Fixture.RunSqlAsync("UPDATE \"AcquisitionRequests\" SET \"UiLocaleId\" = (SELECT \"Id\" FROM \"UiLocales\" WHERE \"Locale\" = 'fr-FR')");
            await RunAsync<SearchDueWantedV1>(host);
            Assert.AreEqual(1L, await host.ScalarAsync("SELECT count(*) FROM \"AcquisitionReleases\" WHERE \"WantedItemId\" = " + wantedId));

            // The request tells the reader it comes translated.
            await host.Fixture.RunSqlAsync("UPDATE \"AcquisitionRequests\" SET \"RequestedByProfileId\" = 101, \"RequestedByAccountId\" = 41");
            using var requests = await host.SendAsync(HttpMethod.Get, "/api/v1/requests");
            StringAssert.Contains(await requests.Content.ReadAsStringAsync(), "\"readsTranslated\":true");
        }

        [TestMethod]
        public async Task Book_OriginalArrivesImmediately_AndALaterRequestedLanguageReplacesIt()
        {
            using var storage = new TemporaryDirectory();
            using var downloads = new TemporaryDirectory();
            var sources = new FakeBookSources();
            await using var host = await CreateHostAsync(storage.Path, downloads.Path, sources);
            using var imported = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/works/metadata", new ImportMetadataWorkDataV1(MediaType.Book, "OL66554W"));
            var work = (await imported.Content.ReadFromJsonAsync<WorkDetailsV1>())!;
            var wantedId = await CreateWantedAsync(host, work.Id);
            await host.Fixture.RunSqlAsync("""
                UPDATE "AcquisitionProfiles" SET "Definition" = "Definition" ||
                    '{"UpgradeAllowed":true,"UpgradeCutoffQuality":"EPUB","FormatScores":[],"CutoffFormatScore":0}'::jsonb
                WHERE "MediaTypeId" = 3 AND "MediaSubtypeId" IS NULL AND "IsDefault";
                UPDATE "AcquisitionConnections" SET "IsEnabled" = FALSE WHERE "AcquisitionConnectionTypeId" = 1;
                """);
            await RunAsync<SearchDueWantedV1>(host);
            await RunAsync<MonitorDownloadsV1>(host);
            await RunAsync<RefreshStoredFileFormatsV1>(host);
            Assert.AreEqual(1L, await host.ScalarAsync("SELECT count(*) FROM \"StoredFiles\" WHERE \"MediaAssetId\" IS NOT NULL"));
            Assert.AreEqual(0L, await host.ScalarAsync("SELECT count(*) FROM \"WantedBelowCutoff\""));

            // A request for another language remains searchable even at the quality cutoff.
            await host.Fixture.RunSqlAsync("""
                UPDATE "AcquisitionRequests" SET "UiLocaleId" = (SELECT "Id" FROM "UiLocales" WHERE "Locale" = 'de-DE');
                UPDATE "WantedItems" SET "NextSearchAt" = NULL;
                """);
            Assert.AreEqual(1L, await host.ScalarAsync("SELECT count(*) FROM \"WantedBelowCutoff\""));
            sources.CatalogLanguage = "de";
            await RunAsync<SearchDueWantedV1>(host);
            Assert.AreEqual(2L, await host.ScalarAsync("SELECT count(*) FROM \"AcquisitionReleases\""));
            await RunAsync<MonitorDownloadsV1>(host);
            await RunAsync<RefreshStoredFileFormatsV1>(host);
            Assert.AreEqual(1L, await host.ScalarAsync("SELECT count(*) FROM \"StoredFiles\" WHERE \"MediaAssetId\" IS NOT NULL"));
            Assert.AreEqual(0L, await host.ScalarAsync("SELECT count(*) FROM \"WantedBelowCutoff\""));
            Assert.AreEqual(1L, await host.ScalarAsync("SELECT count(*) FROM \"ActivityEvents\" WHERE \"ActivityEventTypeId\" = 4"));
        }

        [TestMethod]
        public void Book_GroupedFormats_UpgradeByLanguageWithoutOverridingProfileScores()
        {
            var profile = new Data.Logic.Acquisition.QualityProfile("1", "Books", ["group:101"], ["group:101"], true, "group:101", null, null)
            {
                IsBook = true, LanguageOrder = ["de"],
                QualityGroups = [new Data.Logic.Acquisition.QualityGroup(101, "Readable books", ["EPUB", "PDF"])]
            };
            Assert.IsTrue(UpgradePolicy.IsUpgrade(profile, "EPUB", "PDF", false, 0, 0, ["en"], ["de"]));
            Assert.IsFalse(UpgradePolicy.IsUpgrade(profile, "PDF", "EPUB", false, 0, 0, ["de"], ["en"]));
            Assert.IsFalse(UpgradePolicy.IsUpgrade(profile, "EPUB", "PDF", false, 100, 0, ["en"], ["de"]));
        }

        [TestMethod]
        public async Task Book_Series_FromOpenLibraryThenHardcover_AndAnAdministratorsChoiceStays()
        {
            using var storage = new TemporaryDirectory();
            using var downloads = new TemporaryDirectory();
            var sources = new FakeBookSources();
            await using var host = await CreateHostAsync(storage.Path, downloads.Path, sources);
            async Task<WorkDetailsV1> ImportAsync()
            {
                using var imported = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/works/metadata", new ImportMetadataWorkDataV1(MediaType.Book, "OL66554W"));
                Assert.AreEqual(HttpStatusCode.OK, imported.StatusCode, await imported.Content.ReadAsStringAsync());
                return (await imported.Content.ReadFromJsonAsync<WorkDetailsV1>())!;
            }

            async Task<string?> SeriesAsync() => (string?)await host.ScalarAsync("""
                SELECT concat_ws(':', franchise."Name", work."FranchisePosition", identity."Namespace", work."IsFranchiseManual")
                FROM "Works" AS work JOIN "Franchises" AS franchise ON franchise."Id" = work."FranchiseId"
                LEFT JOIN "FranchiseExternalIdentities" AS identity ON identity."FranchiseId" = franchise."Id"
                """);

            // Without Hardcover, Open Library's numbered series counts; a publisher's line does not.
            var work = await ImportAsync();
            Assert.AreEqual("Austen Novels:2.00:openlibrary:f", await SeriesAsync());
            Assert.AreEqual(0, sources.HardcoverRequests.Count);

            // With Hardcover's token the editions' ISBNs find its series.
            var hardcover = (long)(await host.ScalarAsync("SELECT \"Id\" FROM \"Providers\" WHERE \"Key\" = 'hardcover'"))!;
            using var saved = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/providers/" + hardcover + "/secret", new Jularr.Backend.Data.Admin.Provider.V1.SaveProviderSecretDataV1("Bearer hardcover-token"));
            Assert.AreEqual(HttpStatusCode.OK, saved.StatusCode, await saved.Content.ReadAsStringAsync());
            await ImportAsync();
            StringAssert.StartsWith(sources.HardcoverRequests.Single(), "Bearer hardcover-token ");
            StringAssert.Contains(sources.HardcoverRequests.Single(), "9780141439518");
            Assert.AreEqual("Jane Austen's Novels:2.00:hardcover:f", await SeriesAsync());

            // An administrator's series stays through imports until its name is cleared.
            using var chosen = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/works/" + work.Id + "/franchise", new UpdateWorkFranchiseDataV1("Favourites", 1.5m));
            Assert.AreEqual(HttpStatusCode.OK, chosen.StatusCode, await chosen.Content.ReadAsStringAsync());
            await ImportAsync();
            Assert.AreEqual("Favourites:1.50:t", await SeriesAsync());
            using var cleared = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/works/" + work.Id + "/franchise", new UpdateWorkFranchiseDataV1(null, null));
            Assert.AreEqual(HttpStatusCode.OK, cleared.StatusCode, await cleared.Content.ReadAsStringAsync());
            await ImportAsync();
            Assert.AreEqual("Jane Austen's Novels:2.00:hardcover:f", await SeriesAsync());
        }

        private static async Task<long> CreateWantedAsync(Host host, long workId)
        {
            await host.Fixture.RunSqlAsync($"""
                INSERT INTO "AcquisitionRequests" ("Id", "WorkId", "RequestedByProfileId", "RequestedByAccountId", "UiLocaleId", "AcquisitionRequestStatusTypeId", "DecidedByAccountId", "DecidedAt")
                VALUES (700, {workId}, 202, 42, 2, 2, 41, now());
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

        internal sealed class FakeBookSources : HttpMessageHandler
        {
            public int SabGrabs { get; private set; }
            public int IndexerSearches { get; private set; }
            public bool WaitForIndexer { get; set; }

            public int DirectSearches { get; private set; }
            public bool BlockDirectSearch { get; set; }
            public bool DirectSearchCancelled { get; private set; }

            // Forgets what the set-up asked, so that only the next search is observed, and makes each source wait for the other from now on.
            public void StartCounting()
            {
                Order.Clear();
                IndexerSearches = 0;
                DirectSearches = 0;
                WaitForIndexer = true;
            }
            public ConcurrentQueue<string> Order { get; } = new();
            public List<string> HardcoverRequests { get; } = [];
            public byte[] EpubBytes { get; set; } = ReadingTests.BuildEpub("Pride and Prejudice");
            public string CatalogLanguage { get; set; } = "en";

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
                            "/works/OL66554W/editions.json" => Json("""
                                {"entries":[
                                  {"key":"/books/OL1M","title":"Pride and Prejudice","languages":[{"key":"/languages/eng"}],"physical_format":"Paperback","isbn_13":["9780141439518"],"publishers":["Penguin"],"publish_date":"2003","series":["Penguin Classics"]},
                                  {"key":"/books/OL2M","title":"Pride and Prejudice","languages":[{"key":"/languages/eng"}],"physical_format":"Paperback","publish_date":"1990","series":["Austen Novels ; 2"]},
                                  {"key":"/books/OL3M","title":"Stolz und Vorurteil","languages":[{"key":"/languages/ger"}],"physical_format":"ebook","isbn_13":["978-3-7306-0144-2"],"publishers":["Anaconda"],"publish_date":"2014","contributors":[{"role":"Translator","name":"Werner Beck"}]},
                                  {"key":"/books/OL4M","title":"Orgueil et prÃ©jugÃ©s","physical_format":"Paperback"}
                                ]}
                                """),
                            "/trending/weekly.json" => Json("""{"works":[{"key":"/works/OL66554W","title":"Pride and Prejudice","author_name":["Jane Austen"],"first_publish_year":1813,"cover_i":14348537}]}"""),
                            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
                        };
                    case "api.hardcover.app":
                        HardcoverRequests.Add(request.Headers.Authorization + " " + await request.Content!.ReadAsStringAsync(cancellationToken));
                        return Json("""{"data":{"editions":[{"book":{"id":5,"book_series":[{"position":2,"series":{"id":77,"name":"Jane Austen's Novels"}}]}}]}}""");
                    case "gutendex.example.test":
                        Order.Enqueue("direct");
                        DirectSearches++;
                        if (BlockDirectSearch)
                        {
                            try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
                            finally { DirectSearchCancelled = cancellationToken.IsCancellationRequested; }
                        }
                        if (WaitForIndexer)
                        {
                            // The direct source waits for the indexer's search; it only comes when both are asked at the same time.
                            for (var attempt = 0; attempt < 100 && IndexerSearches == 0; attempt++)
                            {
                                await Task.Delay(100, cancellationToken);
                            }
                        }

                        Order.Enqueue("direct-end");
                        if (CatalogLanguage == "de") return Json("""{"results":[{"id":1343,"title":"Pride and Prejudice","authors":[{"name":"Austen, Jane"}],"languages":["de"],"formats":{"application/epub+zip":"https://www.gutenberg.org/ebooks/1343.epub3.images"}}]}""");
                        return Json("""{"results":[{"id":1342,"title":"Pride and Prejudice","authors":[{"name":"Austen, Jane"}],"languages":["en"],"formats":{"application/epub+zip":"https://www.gutenberg.org/ebooks/1342.epub3.images"}},{"id":7,"title":"Without Epub","authors":[],"languages":["en"],"formats":{"text/plain":"x"}}]}""");
                    case "www.gutenberg.org" when uri.AbsolutePath == "/ebooks/1342.epub3.images":
                        return new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("https://www.gutenberg.org/cache/epub/1342/pg1342-images-3.epub") } };
                    case "www.gutenberg.org":
                        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(EpubBytes) };
                    case "indexer.example.test":
                        Order.Enqueue("indexer");
                        IndexerSearches++;
                        if (WaitForIndexer)
                        {
                            for (var attempt = 0; attempt < 100 && DirectSearches == 0; attempt++)
                            {
                                await Task.Delay(100, cancellationToken);
                            }
                        }

                        Order.Enqueue("indexer-end");
                        return Xml("""
                            <rss><channel>
                              <item><title>Seth Grahame-Smith - Pride and Prejudice and Zombies (epub)</title><guid>zombies</guid><link>https://indexer.example.test/getnzb/zombies</link><size>1000000</size></item>
                              <item><title>Pride.and.Prejudice.Retold.2019.EPUB</title><guid>retold</guid><link>https://indexer.example.test/getnzb/retold</link><size>1000000</size></item>
                              <item><title>Jane.Austen.Pride.and.Prejudice.1813.PDF</title><guid>pdf</guid><link>https://indexer.example.test/getnzb/pdf</link><size>2000000</size></item>
                              <item><title>Stolz.und.Vorurteil.Jane.Austen.2014.German.EPUB</title><guid>german</guid><link>https://indexer.example.test/getnzb/german</link><size>1000000</size></item>
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

