using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.RegularExpressions;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Instance.V1;
using Jularr.Backend.Data.Admin.Library.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Enums.Reading;
using Jularr.Backend.Data.User.Discover.V1;
using Jularr.Backend.Data.User.Instance.V1;
using Jularr.Backend.Data.User.Reading.V1;
using Jularr.Backend.Logic.Reading;
using Host = Jularr.Backend.Tests.AuthenticationTests.Host;
using TemporaryDirectory = Jularr.Backend.Tests.LibraryTests.TemporaryDirectory;

namespace Jularr.Backend.Tests
{
    [TestClass]
    public sealed class ReadingTests
    {
        private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR4nGNgYGD4DwABBAEAwS2OUAAAAABJRU5ErkJggg==");

        [TestMethod]
        public async Task Epub_OpensFromTheCacheOfflineSharesContentAndKeepsProgress()
        {
            using var storage = new TemporaryDirectory();
            await using var host = await LibraryTests.CreateHostAsync(storage.Path, razor: true);
            var (first, second) = await CreateBooksAsync(host, storage.Path, "First Book", "Second Book");

            // Opening prepares the whole book into the cache.
            var document = await OpenAsync(host, first);
            Assert.IsTrue(document.IsCached);
            Assert.AreEqual(2, document.SectionCount);
            Assert.AreEqual("First Book", document.Title);
            var prefix = "/api/v1/reading/documents/" + document.DocumentId;
            using var sectionResponse = await host.SendAsync(HttpMethod.Get, prefix + "/sections/0");
            var section = (await sectionResponse.Content.ReadFromJsonAsync<ReadingSectionV1>())!;
            Assert.IsFalse(section.Html.Contains("<script", StringComparison.OrdinalIgnoreCase));
            Assert.IsFalse(section.Html.Contains("onclick", StringComparison.OrdinalIgnoreCase));
            Assert.IsFalse(section.Html.Contains("iframe", StringComparison.OrdinalIgnoreCase));
            Assert.IsFalse(section.Html.Contains("evil.example", StringComparison.OrdinalIgnoreCase));
            StringAssert.Contains(section.Html, prefix + "/resource?path=OEBPS%2Fimages%2Fcover.png");
            StringAssert.Contains(section.Html, "href=\"?s=1#part\"");
            StringAssert.Contains(section.Html, "data-b=\"0\"");
            Assert.AreEqual("Chapter One", section.Title);

            // Images come from the cache; sections never through the resource route.
            using var image = await host.SendAsync(HttpMethod.Get, prefix + "/resource?path=OEBPS%2Fimages%2Fcover.png");
            Assert.AreEqual("image/png", image.Content.Headers.ContentType!.MediaType);
            CollectionAssert.AreEqual(Png, await image.Content.ReadAsByteArrayAsync());
            using var hidden = await host.SendAsync(HttpMethod.Get, prefix + "/resource?path=%3Asection%2F00000");
            Assert.AreEqual(HttpStatusCode.NotFound, hidden.StatusCode);
            using var toc = await host.SendAsync(HttpMethod.Get, prefix + "/toc");
            var entries = (await toc.Content.ReadFromJsonAsync<BaseDataList<ReadingTocEntryV1>>())!;
            CollectionAssert.AreEqual(new[] { "Chapter One", "Part Two" }, entries.Select(entry => entry.Title).ToArray());
            Assert.AreEqual("part", entries[1].Anchor);

            // Progress is the block locator; the reader continues there.
            using var saved = await host.SendAsync(HttpMethod.Put, "/api/v1/reading/assets/" + first + "/progress", new SaveReadingProgressDataV1("s1/b1", 0.6m, false));
            Assert.AreEqual(HttpStatusCode.OK, saved.StatusCode, await saved.Content.ReadAsStringAsync());
            Assert.AreEqual("s1/b1", (await OpenAsync(host, first)).Locator);
            using var continuing = await host.SendAsync(HttpMethod.Get, "/api/v1/continue");
            var shelf = (await continuing.Content.ReadFromJsonAsync<BaseDataList<ContinueItemV1>>())!;
            Assert.AreEqual(MediaAssetType.Ebook, shelf.Single(item => item.MediaAssetId == first).MediaAssetTypeId);

            // Reading does not depend on Playback.
            await host.Fixture.RunSqlAsync("UPDATE \"InstanceModules\" SET \"IsEnabled\" = FALSE WHERE \"Key\" = 'Playback'");
            using var readingOnly = await host.SendAsync(HttpMethod.Get, "/api/v1/continue");
            Assert.AreEqual(HttpStatusCode.OK, readingOnly.StatusCode);
            Assert.IsTrue((await readingOnly.Content.ReadFromJsonAsync<BaseDataList<ContinueItemV1>>())!.Any(item => item.MediaAssetId == first));
            using var home = await host.SendAsync(HttpMethod.Get, "/");
            StringAssert.Contains(await home.Content.ReadAsStringAsync(), "/Read/" + first);
            using var page = await host.SendAsync(HttpMethod.Get, "/Read/" + first);
            var html = await page.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, page.StatusCode, html);
            StringAssert.Contains(html, "The second part begins.");
            StringAssert.Contains(html, "data-locator=\"s1/b1\"");

            // Offline storage: the cached book stays readable, the other one says why it is not.
            Directory.Move(Path.Combine(storage.Path, "1", "Books"), Path.Combine(storage.Path, "1", "Offline"));
            Assert.AreEqual(document.DocumentId, (await OpenAsync(host, first)).DocumentId);
            using var offlineSection = await host.SendAsync(HttpMethod.Get, prefix + "/sections/1");
            Assert.AreEqual(HttpStatusCode.OK, offlineSection.StatusCode);
            using var uncached = await host.SendAsync(HttpMethod.Post, "/api/v1/reading/assets/" + second + "/open");
            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, uncached.StatusCode);
            StringAssert.Contains(await uncached.Content.ReadAsStringAsync(), "reading_unavailable");
            using var offlinePage = await host.SendAsync(HttpMethod.Get, "/Read/" + second);
            StringAssert.Contains(await offlinePage.Content.ReadAsStringAsync(), "not saved for reading while its storage is offline");

            // Another profile reads the same prepared content.
            using var assigned = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/accounts/41/profiles/202");
            Assert.AreEqual(HttpStatusCode.OK, assigned.StatusCode, await assigned.Content.ReadAsStringAsync());
            using var switched = await host.SendAsync(HttpMethod.Put, "/api/v1/auth/profile/202");
            Assert.AreEqual(HttpStatusCode.OK, switched.StatusCode, await switched.Content.ReadAsStringAsync());
            var other = await OpenAsync(host, first);
            Assert.AreEqual(document.DocumentId, other.DocumentId);
            Assert.IsNull(other.Locator);
            Assert.AreEqual(1L, await host.ScalarAsync("SELECT count(*) FROM \"ReadingDocuments\""));
        }

        [TestMethod]
        public async Task Annotations_StayWithTheProfileAndMarkTheReader()
        {
            using var storage = new TemporaryDirectory();
            await using var host = await LibraryTests.CreateHostAsync(storage.Path, razor: true);
            var (first, _) = await CreateBooksAsync(host, storage.Path, "First Book", "Second Book");
            await OpenAsync(host, first);
            var route = "/api/v1/reading/assets/" + first + "/annotations";

            // A highlight covers at least one character of the prepared text.
            using var created = await host.SendAsync(HttpMethod.Post, route, new CreateReadingAnnotationDataV1(0, 0, 0, 0, 5, "Chapt", null, HighlightColorType.Green));
            Assert.AreEqual(HttpStatusCode.OK, created.StatusCode, await created.Content.ReadAsStringAsync());
            var id = (await created.Content.ReadFromJsonAsync<CreateReadingAnnotationResultV1>())!.Id;
            using var empty = await host.SendAsync(HttpMethod.Post, route, new CreateReadingAnnotationDataV1(0, 1, 4, 1, 4, "x", null, HighlightColorType.Yellow));
            StringAssert.Contains(await empty.Content.ReadAsStringAsync(), "out_of_range");
            using var blank = await host.SendAsync(HttpMethod.Put, route + "/" + id, new UpdateReadingAnnotationDataV1("   ", HighlightColorType.Pink));
            Assert.AreEqual(HttpStatusCode.OK, blank.StatusCode, await blank.Content.ReadAsStringAsync());
            using var noted = await host.SendAsync(HttpMethod.Put, route + "/" + id, new UpdateReadingAnnotationDataV1("  Remember this.  ", HighlightColorType.Pink));
            Assert.AreEqual(HttpStatusCode.OK, noted.StatusCode, await noted.Content.ReadAsStringAsync());

            // The section shows it, the notes list it, the reader marks it.
            using var section = await host.SendAsync(HttpMethod.Get, "/api/v1/reading/assets/" + first + "/sections/0/annotations");
            var mark = (await section.Content.ReadFromJsonAsync<BaseDataList<ReadingAnnotationV1>>())!.Single();
            Assert.AreEqual("Remember this.", mark.Note);
            Assert.AreEqual(HighlightColorType.Pink, mark.HighlightColorTypeId);
            using var page = await host.SendAsync(HttpMethod.Get, "/Read/" + first + "?s=0");
            var html = await page.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, page.StatusCode, html);
            StringAssert.Contains(html, "&quot;quote&quot;:&quot;Chapt&quot;");
            StringAssert.Contains(html, "href=\"?s=0&amp;b=0\"");

            // Another profile neither sees nor changes it.
            using var assigned = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/accounts/41/profiles/202");
            using var switched = await host.SendAsync(HttpMethod.Put, "/api/v1/auth/profile/202");
            Assert.AreEqual(HttpStatusCode.OK, switched.StatusCode, await switched.Content.ReadAsStringAsync());
            using var foreign = await host.SendAsync(HttpMethod.Get, route);
            Assert.AreEqual(0, (await foreign.Content.ReadFromJsonAsync<PageResult<ReadingAnnotationV1>>())!.Items.Count);
            using var foreignDelete = await host.SendAsync(HttpMethod.Delete, route + "/" + id);
            Assert.AreEqual(HttpStatusCode.NotFound, foreignDelete.StatusCode);
            using var back = await host.SendAsync(HttpMethod.Put, "/api/v1/auth/profile/101");
            using var deleted = await host.SendAsync(HttpMethod.Delete, route + "/" + id);
            Assert.AreEqual(HttpStatusCode.OK, deleted.StatusCode, await deleted.Content.ReadAsStringAsync());
            Assert.AreEqual(0L, await host.ScalarAsync("SELECT count(*) FROM \"ReadingAnnotations\""));
        }

        [TestMethod]
        public async Task Bookmarks_StayWithTheProfileAreBoundedAndFollowTheModule()
        {
            using var storage = new TemporaryDirectory();
            await using var host = await LibraryTests.CreateHostAsync(storage.Path, razor: true);
            var (first, _) = await CreateBooksAsync(host, storage.Path, "First Book", "Second Book");
            await OpenAsync(host, first);
            var route = "/api/v1/reading/assets/" + first + "/bookmarks";

            // One place keeps one bookmark; the second save only updates it.
            using var created = await host.SendAsync(HttpMethod.Post, route, new CreateReadingBookmarkDataV1(1, 1, 0.6m, "Part Two"));
            Assert.AreEqual(HttpStatusCode.OK, created.StatusCode, await created.Content.ReadAsStringAsync());
            var id = (await created.Content.ReadFromJsonAsync<CreateReadingBookmarkResultV1>())!.Id;
            using var again = await host.SendAsync(HttpMethod.Post, route, new CreateReadingBookmarkDataV1(1, 1, 0.7m, "Part Two"));
            Assert.AreEqual(id, (await again.Content.ReadFromJsonAsync<CreateReadingBookmarkResultV1>())!.Id);
            using var earlier = await host.SendAsync(HttpMethod.Post, route, new CreateReadingBookmarkDataV1(0, 0, 0.1m, null));
            Assert.AreEqual(HttpStatusCode.OK, earlier.StatusCode, await earlier.Content.ReadAsStringAsync());
            using var invalid = await host.SendAsync(HttpMethod.Post, route, new CreateReadingBookmarkDataV1(0, 0, 1.5m, null));
            Assert.AreEqual(HttpStatusCode.BadRequest, invalid.StatusCode);

            // Reading order with the chapter title and place; the reader shows it and leads to it.
            using var listed = await host.SendAsync(HttpMethod.Get, route);
            var marks = (await listed.Content.ReadFromJsonAsync<BaseDataList<ReadingBookmarkV1>>())!;
            CollectionAssert.AreEqual(new[] { 0, 1 }, marks.Select(mark => mark.SectionIndex).ToArray());
            Assert.AreEqual("Part Two", marks[1].Title);
            Assert.AreEqual(0.7m, marks[1].Progression);
            using var page = await host.SendAsync(HttpMethod.Get, "/Read/" + first + "?s=0");
            var html = await page.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, page.StatusCode, html);
            StringAssert.Contains(html, "href=\"?s=1&amp;b=1\"");

            // Another profile neither sees nor deletes it.
            using var assigned = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/accounts/41/profiles/202");
            using var switched = await host.SendAsync(HttpMethod.Put, "/api/v1/auth/profile/202");
            Assert.AreEqual(HttpStatusCode.OK, switched.StatusCode, await switched.Content.ReadAsStringAsync());
            using var foreign = await host.SendAsync(HttpMethod.Get, route);
            Assert.AreEqual(0, (await foreign.Content.ReadFromJsonAsync<BaseDataList<ReadingBookmarkV1>>())!.Count);
            using var foreignDelete = await host.SendAsync(HttpMethod.Delete, route + "/" + id);
            Assert.AreEqual(HttpStatusCode.NotFound, foreignDelete.StatusCode);
            using var back = await host.SendAsync(HttpMethod.Put, "/api/v1/auth/profile/101");

            // At most 200 places per book; an existing place still updates.
            await host.Fixture.RunSqlAsync("INSERT INTO \"ReadingBookmarks\" (\"ProfileId\", \"MediaAssetId\", \"SectionIndex\", \"Block\", \"Progression\") "
                + "SELECT 101, " + first + ", 5, n, 0.5 FROM generate_series(1, 197) AS n");
            using var last = await host.SendAsync(HttpMethod.Post, route, new CreateReadingBookmarkDataV1(6, 0, 0.5m, null));
            Assert.AreEqual(HttpStatusCode.OK, last.StatusCode, await last.Content.ReadAsStringAsync());
            using var over = await host.SendAsync(HttpMethod.Post, route, new CreateReadingBookmarkDataV1(6, 1, 0.5m, null));
            StringAssert.Contains(await over.Content.ReadAsStringAsync(), "bookmark_limit");
            using var update = await host.SendAsync(HttpMethod.Post, route, new CreateReadingBookmarkDataV1(6, 0, 0.9m, null));
            Assert.AreEqual(HttpStatusCode.OK, update.StatusCode, await update.Content.ReadAsStringAsync());

            // Delete, and the Reading module off closes the lists.
            using var deleted = await host.SendAsync(HttpMethod.Delete, route + "/" + id);
            Assert.AreEqual(HttpStatusCode.OK, deleted.StatusCode, await deleted.Content.ReadAsStringAsync());
            Assert.AreEqual(0L, await host.ScalarAsync("SELECT count(*) FROM \"ReadingBookmarks\" WHERE \"Id\" = " + id));
            await host.Fixture.RunSqlAsync("UPDATE \"InstanceModules\" SET \"IsEnabled\" = FALSE WHERE \"Key\" = 'Reading'");
            using var off = await host.SendAsync(HttpMethod.Get, route);
            Assert.AreEqual(HttpStatusCode.Forbidden, off.StatusCode);
            using var offCreate = await host.SendAsync(HttpMethod.Post, route, new CreateReadingBookmarkDataV1(0, 0, 0.1m, null));
            Assert.AreEqual(HttpStatusCode.Forbidden, offCreate.StatusCode);
        }

        [TestMethod]
        public async Task Search_FindsTheSectionsWithTheWords()
        {
            using var storage = new TemporaryDirectory();
            await using var host = await LibraryTests.CreateHostAsync(storage.Path, razor: true);
            var (first, _) = await CreateBooksAsync(host, storage.Path, "First Book", "Second Book");
            var document = await OpenAsync(host, first);
            var route = "/api/v1/reading/documents/" + document.DocumentId + "/search?query=";

            // Text without markup, case-insensitive, in reading order.
            using var found = await host.SendAsync(HttpMethod.Get, route + "SECOND%20part");
            var hit = (await found.Content.ReadFromJsonAsync<BaseDataList<ReadingSearchHitV1>>())!.Single();
            Assert.AreEqual(1, hit.SectionIndex);
            StringAssert.Contains(hit.Snippet, "The second part begins.");
            Assert.AreEqual(1, hit.Matches);
            using var missing = await host.SendAsync(HttpMethod.Get, route + "nowhere");
            Assert.AreEqual(0, (await missing.Content.ReadFromJsonAsync<BaseDataList<ReadingSearchHitV1>>())!.Count);
            using var tooShort = await host.SendAsync(HttpMethod.Get, route + "a");
            Assert.AreEqual(HttpStatusCode.BadRequest, tooShort.StatusCode);

            // Entities count as the characters they stand for.
            await host.Fixture.RunSqlAsync("UPDATE \"ReadingResources\" SET \"Bytes\" = convert_to('<p data-b=\"0\">Tom &amp; Jerry</p>', 'UTF8') WHERE \"Path\" = ':section/00001'");
            using var entity = await host.SendAsync(HttpMethod.Get, route + "Tom%20%26%20Jerry");
            Assert.AreEqual("Tom & Jerry", (await entity.Content.ReadFromJsonAsync<BaseDataList<ReadingSearchHitV1>>())!.Single().Snippet);
            using var markup = await host.SendAsync(HttpMethod.Get, route + "amp");
            Assert.AreEqual(0, (await markup.Content.ReadFromJsonAsync<BaseDataList<ReadingSearchHitV1>>())!.Count);
            using var page = await host.SendAsync(HttpMethod.Get, "/Read/" + first + "?s=0&q=tom%20%26%20jerry");
            var html = await page.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, page.StatusCode, html);
            StringAssert.Contains(html, "href=\"?s=1&amp;q=tom%20%26%20jerry\"");
        }

        [TestMethod]
        public async Task Pdf_WithText_ReadsAsChaptersAcrossPagesAndKeepsTheOriginal()
        {
            using var storage = new TemporaryDirectory();
            await using var host = await LibraryTests.CreateHostAsync(storage.Path, razor: true);
            var second = await AddReadablePdfAsync(host, storage.Path);
            const string Long = "and the long line runs on across the whole width of the page like book text does";

            // Headers drop, chapters come from their numbers, paragraphs join across lines and pages.
            var document = await OpenAsync(host, second);
            Assert.AreEqual(ReadingFormatType.Pdf, document.ReadingFormatTypeId);
            Assert.AreEqual(2, document.SectionCount);
            using var toc = await host.SendAsync(HttpMethod.Get, "/api/v1/reading/documents/" + document.DocumentId + "/toc");
            CollectionAssert.AreEqual(new[] { "1", "2" }, (await toc.Content.ReadFromJsonAsync<BaseDataList<ReadingTocEntryV1>>())!.Select(entry => entry.Title).ToArray());
            using var first = await host.SendAsync(HttpMethod.Get, "/api/v1/reading/documents/" + document.DocumentId + "/sections/0");
            var html = (await first.Content.ReadFromJsonAsync<ReadingSectionV1>())!.Html;
            StringAssert.Contains(html, "into hyphenated halves");
            StringAssert.Contains(html, "this page " + Long + " but continues on the next one");
            Assert.IsFalse(html.Contains("Page ", StringComparison.Ordinal));
            Assert.AreEqual(2, Regex.Matches(html, "<p ").Count);

            // A picture stays where it stood, after the second chapter's text, and is served as a resource of the book.
            using var chapter = await host.SendAsync(HttpMethod.Get, "/api/v1/reading/documents/" + document.DocumentId + "/sections/1");
            var chapterHtml = (await chapter.Content.ReadFromJsonAsync<ReadingSectionV1>())!.Html;
            var picture = Regex.Match(chapterHtml, "<img[^>]*src=\"([^\"]+)\"");
            Assert.IsTrue(picture.Success, chapterHtml);
            Assert.IsTrue(chapterHtml.IndexOf("ends with this sentence.", StringComparison.Ordinal) < picture.Index, chapterHtml);
            using var image = await host.SendAsync(HttpMethod.Get, WebUtility.HtmlDecode(picture.Groups[1].Value));
            Assert.AreEqual(HttpStatusCode.OK, image.StatusCode);
            Assert.AreEqual("image/png", image.Content.Headers.ContentType?.MediaType);

            // The reader reflows it and still offers the original.
            using var page = await host.SendAsync(HttpMethod.Get, "/Read/" + second);
            var reader = await page.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, page.StatusCode, reader);
            StringAssert.Contains(reader, "href=\"/api/v1/reading/assets/" + second + "/file\"");
            Assert.IsFalse(reader.Contains("<iframe", StringComparison.Ordinal));

            // It downloads as an EPUB beside the original: the chapters as XHTML with the picture where it stood, and its contents.
            StringAssert.Contains(reader, "href=\"/api/v1/reading/documents/" + document.DocumentId + "/ebook\"");
            using var ebook = await host.SendAsync(HttpMethod.Get, "/api/v1/reading/documents/" + document.DocumentId + "/ebook");
            Assert.AreEqual(HttpStatusCode.OK, ebook.StatusCode, await ebook.Content.ReadAsStringAsync());
            Assert.AreEqual("application/epub+zip", ebook.Content.Headers.ContentType?.MediaType);
            Assert.AreEqual("Second Book.epub", ebook.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
            var bytes = await ebook.Content.ReadAsByteArrayAsync();
            Assert.AreEqual("mimetype", Encoding.ASCII.GetString(bytes, 30, 8));
            Assert.AreEqual(0, BitConverter.ToUInt16(bytes, 8));
            using var epub = new ZipArchive(new MemoryStream(bytes));
            var package = Xml(Entry(epub, "OEBPS/content.opf"));
            Assert.AreEqual("Second Book", package.Descendants().Single(element => element.Name.LocalName == "title").Value);
            Assert.AreEqual(2, package.Descendants().Count(element => element.Name.LocalName == "itemref"));
            var converted = Entry(epub, "OEBPS/s00001.xhtml");
            Xml(converted);
            StringAssert.Contains(converted, "ends with this sentence.");
            StringAssert.Contains(converted, "src=\"images/00000.png\"");
            Assert.IsNotNull(epub.GetEntry("OEBPS/images/00000.png"));
            StringAssert.Contains(Entry(epub, "OEBPS/nav.xhtml"), "href=\"s00001.xhtml\"");
        }

        // A second book whose only file is a PDF of three pages of ordinary book text in two chapters, with a picture on the second page.
        private static async Task<long> AddReadablePdfAsync(Host host, string storage)
        {
            var (_, second) = await CreateBooksAsync(host, storage, "First Book", "Second Book");
            const string Long = "and the long line runs on across the whole width of the page like book text does";
            var pdf = BuildPdf(
                [(760, "1"), (732, "The first paragraph starts here " + Long), (718, "with a word that breaks at the line end into hyph-"), (704, "enated halves and ends here."),
                    (676, "The second paragraph " + Long), (662, "and does not end on this page " + Long)],
                [(780, "but continues on the next one and ends."), (752, "2"), (724, "The second chapter " + Long), (710, "ends with this sentence.")],
                [(780, "A last paragraph " + Long), (766, "closes the book.")]);
            pdf = WithPicture(pdf, page: 2, top: 700);
            File.WriteAllBytes(Path.Combine(storage, "1", "Books", "Second Book", "Second Book.pdf"), pdf);
            await host.Fixture.RunSqlAsync("UPDATE \"StoredFiles\" SET \"MissingSince\" = now() WHERE \"MediaAssetId\" = " + second);
            await host.Fixture.RunSqlAsync("INSERT INTO \"StoredFiles\" (\"LibraryRootId\", \"RelativePath\", \"SizeBytes\", \"ModifiedAt\", \"LastSeenAt\", \"MediaAssetId\") "
                + "SELECT \"LibraryRootId\", 'Second Book/Second Book.pdf', " + pdf.Length + ", now(), now(), \"MediaAssetId\" FROM \"StoredFiles\" WHERE \"MediaAssetId\" = " + second);
            return second;
        }

        [TestMethod]
        public async Task ReadingView_IsKeptPerProfileAndBook_NextToTheLanguage_AndAutomaticDropsIt()
        {
            using var storage = new TemporaryDirectory();
            await using var host = await LibraryTests.CreateHostAsync(storage.Path, razor: true);
            var (first, second) = await CreateBooksAsync(host, storage.Path, "First Book", "Second Book");

            async Task<ReadingView?> ChosenAsync(long asset)
            {
                using var response = await host.SendAsync(HttpMethod.Get, "/api/v1/reading/assets/" + asset + "/source");
                return (await response.Content.ReadFromJsonAsync<ReadingSourceV1>())!.ReadingViewTypeId;
            }

            async Task SaveAsync(string path, object data)
            {
                using var response = await host.SendAsync(HttpMethod.Put, "/api/v1/reading/assets/" + first + "/" + path, data);
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
            }

            // The choice of one book does not touch another; a language and a view share one row.
            Assert.IsNull(await ChosenAsync(first));
            await SaveAsync("view", new SaveReadingViewDataV1(ReadingView.Original));
            Assert.AreEqual(ReadingView.Original, await ChosenAsync(first));
            Assert.IsNull(await ChosenAsync(second));
            await SaveAsync("language", new SaveReadingLanguageDataV1("original"));
            Assert.AreEqual(1L, await host.ScalarAsync("SELECT count(*) FROM \"ReadingPreferences\""));
            await SaveAsync("view", new SaveReadingViewDataV1(ReadingView.Smart));
            Assert.AreEqual(ReadingView.Smart, await ChosenAsync(first));

            // Automatic again forgets the view and keeps the language; with neither the row is gone.
            await SaveAsync("view", new SaveReadingViewDataV1(null));
            Assert.IsNull(await ChosenAsync(first));
            Assert.AreEqual(1L, await host.ScalarAsync("SELECT count(*) FROM \"ReadingPreferences\""));
            await SaveAsync("language", new SaveReadingLanguageDataV1(null));
            Assert.AreEqual(0L, await host.ScalarAsync("SELECT count(*) FROM \"ReadingPreferences\""));
        }

        [TestMethod]
        public async Task Pdf_ReadsInTheViewTheProfileChose_AndTheOtherViewOpensAtTheSamePage()
        {
            using var storage = new TemporaryDirectory();
            await using var host = await LibraryTests.CreateHostAsync(storage.Path, razor: true);
            var second = await AddReadablePdfAsync(host, storage.Path);
            var address = "/Read/" + second;
            async Task<string> PageAsync(string query = "")
            {
                using var response = await host.SendAsync(HttpMethod.Get, address + query);
                var html = await response.Content.ReadAsStringAsync();
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, html);
                return html;
            }

            // Automatic opens the Smart Book of a PDF with text, and offers the three views.
            var smart = await PageAsync();
            StringAssert.Contains(smart, "data-role=\"content\"");
            StringAssert.Contains(smart, "data-view-chosen=\"auto\"");
            StringAssert.Contains(smart, "data-view-url=\"/api/v1/reading/assets/" + second + "/view\"");
            foreach (var choice in new[] { "auto", "smart", "original" })
            {
                StringAssert.Contains(smart, "data-view-choice=\"" + choice + "\"");
            }

            // The original pages of the same book on request, without any text: the file is all they use.
            var original = await PageAsync("?view=original&pg=2");
            StringAssert.Contains(original, "data-format=\"pages\"");
            StringAssert.Contains(original, "data-page=\"2\"");
            Assert.IsFalse(original.Contains("data-role=\"content\"", StringComparison.Ordinal));

            // The choice of the profile is kept: the book now opens as its original pages by itself, at the page where the profile stopped.
            using var saved = await host.SendAsync(HttpMethod.Put, "/api/v1/reading/assets/" + second + "/view", new SaveReadingViewDataV1(ReadingView.Original));
            Assert.AreEqual(HttpStatusCode.OK, saved.StatusCode);
            using var progress = await host.SendAsync(HttpMethod.Put, "/api/v1/reading/assets/" + second + "/progress", new SaveReadingProgressDataV1("p3", 1m, true));
            Assert.AreEqual(HttpStatusCode.OK, progress.StatusCode);
            var kept = await PageAsync();
            StringAssert.Contains(kept, "data-format=\"pages\"");
            StringAssert.Contains(kept, "data-page=\"3\"");
            StringAssert.Contains(kept, "data-view-chosen=\"original\"");

            // A page of the original leads into the text where it starts, and a place in the text to its page.
            var document = await OpenAsync(host, second);
            Assert.IsTrue(document.IsRecommended);
            using var position = await host.SendAsync(HttpMethod.Get, "/api/v1/reading/assets/" + second + "/position?page=3");
            Assert.AreEqual(HttpStatusCode.OK, position.StatusCode, await position.Content.ReadAsStringAsync());
            var place = (await position.Content.ReadFromJsonAsync<ReadingPositionV1>())!;
            using var back = await host.SendAsync(HttpMethod.Get, "/api/v1/reading/assets/" + second + "/position?sectionIndex=0&block=0");
            Assert.AreEqual(HttpStatusCode.OK, back.StatusCode, await back.Content.ReadAsStringAsync());
            Assert.AreEqual(1, (await back.Content.ReadFromJsonAsync<ReadingPositionV1>())!.Page);
            var textAtPage = await PageAsync("?view=smart&pg=3");
            StringAssert.Contains(textAtPage, "data-role=\"content\"");
            StringAssert.Contains(textAtPage, "data-start-block=\"" + place.Block + "\"");

            // The profile's page notes stay with the profile and the book, anchored to the page and the first words of it.
            using var note = await host.SendAsync(HttpMethod.Post, "/api/v1/reading/assets/" + second + "/page-notes", new CreateReadingPageNoteDataV1(3, "A last paragraph", "Check this page."));
            Assert.AreEqual(HttpStatusCode.OK, note.StatusCode, await note.Content.ReadAsStringAsync());
            StringAssert.Contains(await PageAsync("?view=original&pg=3"), "Check this page.");
            Assert.AreEqual("A last paragraph", await host.ScalarAsync("SELECT \"Anchor\" FROM \"ReadingPageNotes\""));
            var noteId = (long)(await host.ScalarAsync("SELECT \"Id\" FROM \"ReadingPageNotes\""))!;
            using var removed = await host.SendAsync(HttpMethod.Delete, "/api/v1/reading/assets/" + second + "/page-notes/" + noteId);
            Assert.AreEqual(HttpStatusCode.OK, removed.StatusCode);
            Assert.AreEqual(0L, await host.ScalarAsync("SELECT count(*) FROM \"ReadingPageNotes\""));
        }

        internal static string Entry(ZipArchive archive, string name)
        {
            using var stream = new StreamReader(archive.GetEntry(name)!.Open());
            return stream.ReadToEnd();
        }

        // EPUB files are XML; their doctype is the HTML5 one.
        internal static System.Xml.Linq.XDocument Xml(string text) =>
            System.Xml.Linq.XDocument.Load(System.Xml.XmlReader.Create(new StringReader(text), new System.Xml.XmlReaderSettings { DtdProcessing = System.Xml.DtdProcessing.Ignore }));

        // The same pages with a picture on one of them, below the given height.
        private static byte[] WithPicture(byte[] pdf, int page, double top)
        {
            using var source = UglyToad.PdfPig.PdfDocument.Open(pdf);
            var builder = new UglyToad.PdfPig.Writer.PdfDocumentBuilder();
            for (var number = 1; number <= source.NumberOfPages; number++)
            {
                var copy = builder.AddPage(source, number);
                if (number == page)
                {
                    copy.AddPng(Picture(96, 64), new UglyToad.PdfPig.Core.PdfRectangle(54, top - 120, 234, top));
                }
            }

            return builder.Build();
        }

        // A plain RGB picture as PNG.
        internal static byte[] Picture(int width, int height)
        {
            var raw = new MemoryStream();
            for (var y = 0; y < height; y++)
            {
                raw.WriteByte(0);
                for (var x = 0; x < width; x++)
                {
                    raw.Write([(byte)(x * 2), (byte)(y * 3), 160]);
                }
            }

            var compressed = new MemoryStream();
            using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
            {
                raw.Position = 0;
                raw.CopyTo(zlib);
            }

            var png = new MemoryStream();
            png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
            var header = new byte[13];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(header, width);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
            header[8] = 8;
            header[9] = 2;
            Chunk(png, "IHDR", header);
            Chunk(png, "IDAT", compressed.ToArray());
            Chunk(png, "IEND", []);
            return png.ToArray();
        }

        private static void Chunk(Stream png, string type, byte[] data)
        {
            var length = new byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
            png.Write(length);
            var body = Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
            png.Write(body);
            var crc = new byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32(body));
            png.Write(crc);
        }

        private static uint Crc32(byte[] bytes)
        {
            var crc = 0xFFFFFFFFu;
            foreach (var value in bytes)
            {
                crc ^= value;
                for (var bit = 0; bit < 8; bit++)
                {
                    crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
                }
            }

            return ~crc;
        }

        // Text lines per page at given heights; every page carries a running header.
        private static byte[] BuildPdf(params (double Y, string Text)[][] pages)
        {
            var builder = new UglyToad.PdfPig.Writer.PdfDocumentBuilder();
            var font = builder.AddStandard14Font(UglyToad.PdfPig.Fonts.Standard14Fonts.Standard14Font.Helvetica);
            for (var index = 0; index < pages.Length; index++)
            {
                var page = builder.AddPage(UglyToad.PdfPig.Content.PageSize.A4);
                page.AddText("Page " + (index + 1) + " of " + pages.Length, 9, new UglyToad.PdfPig.Core.PdfPoint(460, 815), font);
                foreach (var (y, text) in pages[index])
                {
                    page.AddText(text, 9, new UglyToad.PdfPig.Core.PdfPoint(54, y), font);
                }
            }

            return builder.Build();
        }

        [TestMethod]
        public async Task Pdf_WithoutReadableText_OpensInTheBrowsersViewer()
        {
            using var storage = new TemporaryDirectory();
            await using var host = await LibraryTests.CreateHostAsync(storage.Path, razor: true);
            var (first, second) = await CreateBooksAsync(host, storage.Path, "First Book", "Second Book");
            File.WriteAllText(Path.Combine(storage.Path, "1", "Books", "Second Book", "Second Book.pdf"), "%PDF-1.4 test");
            await host.Fixture.RunSqlAsync("UPDATE \"StoredFiles\" SET \"MissingSince\" = now() WHERE \"MediaAssetId\" = " + second);
            await host.Fixture.RunSqlAsync("INSERT INTO \"StoredFiles\" (\"LibraryRootId\", \"RelativePath\", \"SizeBytes\", \"ModifiedAt\", \"LastSeenAt\", \"MediaAssetId\") "
                + "SELECT \"LibraryRootId\", 'Second Book/Second Book.pdf', 13, now(), now(), \"MediaAssetId\" FROM \"StoredFiles\" WHERE \"MediaAssetId\" = " + second);

            // The format decides the reader; a PDF Jularr cannot reflow comes from its storage.
            using var epub = await host.SendAsync(HttpMethod.Get, "/api/v1/reading/assets/" + first + "/source");
            Assert.AreEqual(ReadingFormatType.Epub, (await epub.Content.ReadFromJsonAsync<ReadingSourceV1>())!.ReadingFormatTypeId);
            using var pdf = await host.SendAsync(HttpMethod.Get, "/api/v1/reading/assets/" + second + "/source");
            Assert.AreEqual(ReadingFormatType.Pdf, (await pdf.Content.ReadFromJsonAsync<ReadingSourceV1>())!.ReadingFormatTypeId);
            using var file = await host.SendAsync(HttpMethod.Get, "/api/v1/reading/assets/" + second + "/file");
            Assert.AreEqual("application/pdf", file.Content.Headers.ContentType!.MediaType);
            Assert.AreEqual("%PDF-1.4 test", await file.Content.ReadAsStringAsync());
            using var page = await host.SendAsync(HttpMethod.Get, "/Read/" + second);
            var html = await page.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, page.StatusCode, html);

            // It opens as its original pages, drawn by the browser from that file; the Smart Book is not offered where there is no text.
            StringAssert.Contains(html, "data-format=\"pages\"");
            StringAssert.Contains(html, "data-file-url=\"/api/v1/reading/assets/" + second + "/file\"");
            Assert.IsFalse(html.Contains("<iframe", StringComparison.Ordinal));
        }

        [TestMethod]
        public async Task Comic_PagesInNaturalOrderTypedByBytesResumeOnTheExactPage()
        {
            using var storage = new TemporaryDirectory();
            await using var host = await LibraryTests.CreateHostAsync(storage.Path, razor: true);
            var mountId = await LibraryTests.CreateMountAsync(host);
            var folder = Path.Combine(storage.Path, mountId.ToString(System.Globalization.CultureInfo.InvariantCulture), "Manga", "Example Manga");
            Directory.CreateDirectory(folder);
            byte[] jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0, 16, 74, 70, 73, 70, 0];
            using (var archive = ZipFile.Open(Path.Combine(folder, "Example Manga v01.cbz"), ZipArchiveMode.Create))
            {
                foreach (var (name, bytes) in new[] { ("pages/p10.png", jpeg), ("pages/p2.png", Png), ("pages/p1.jpg", Png), ("pages/fake.png", "not an image"u8.ToArray()),
                    ("__MACOSX/pages/._p1.jpg", Png), ("notes.txt", "notes"u8.ToArray()) })
                {
                    await using var entry = archive.CreateEntry(name).Open();
                    await entry.WriteAsync(bytes);
                }
            }

            var rootId = await LibraryTests.CreateRootAsync(host, mountId, "Manga", MediaType.Book, MediaSubtype.Manga);
            await LibraryTests.ScanAsync(host, rootId);
            var fileId = (long)(await host.ScalarAsync("SELECT \"Id\" FROM \"StoredFiles\" WHERE \"RelativePath\" LIKE '%.cbz'"))!;
            using var assigned = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/library/files/" + fileId + "/assignment", new AssignLibraryFileDataV1(null, "Example Manga", null, null, null));
            Assert.AreEqual(HttpStatusCode.OK, assigned.StatusCode, await assigned.Content.ReadAsStringAsync());
            var asset = (long)(await host.ScalarAsync("SELECT \"MediaAssetId\" FROM \"StoredFiles\" WHERE \"Id\" = " + fileId))!;

            // Three real pages, p1 before p2 before p10; the bytes decide the type, Manga reads right to left.
            var document = await OpenAsync(host, asset);
            Assert.AreEqual(ReadingFormatType.Comic, document.ReadingFormatTypeId);
            Assert.AreEqual(3, document.SectionCount);
            Assert.IsTrue(document.IsCached);
            Assert.IsTrue(document.IsRightToLeft);
            var prefix = "/api/v1/reading/documents/" + document.DocumentId + "/resource?path=";
            using var first = await host.SendAsync(HttpMethod.Get, prefix + Uri.EscapeDataString(":page/00000"));
            Assert.AreEqual("image/png", first.Content.Headers.ContentType!.MediaType);
            using var last = await host.SendAsync(HttpMethod.Get, prefix + Uri.EscapeDataString(":page/00002"));
            Assert.AreEqual("image/jpeg", last.Content.Headers.ContentType!.MediaType);
            CollectionAssert.AreEqual(jpeg, await last.Content.ReadAsByteArrayAsync());

            // The exact page is the progress; the reader opens there again.
            using var saved = await host.SendAsync(HttpMethod.Put, "/api/v1/reading/assets/" + asset + "/progress", new SaveReadingProgressDataV1("p2", 0.6667m, false));
            Assert.AreEqual(HttpStatusCode.OK, saved.StatusCode, await saved.Content.ReadAsStringAsync());
            Assert.AreEqual("p2", (await OpenAsync(host, asset)).Locator);

            // Comics continue on the reading shelf and in the Continue Reading bar.
            using var continuing = await host.SendAsync(HttpMethod.Get, "/api/v1/continue");
            var item = (await continuing.Content.ReadFromJsonAsync<BaseDataList<ContinueItemV1>>())!.Single(entry => entry.MediaAssetId == asset);
            Assert.AreEqual(MediaAssetType.Comic, item.MediaAssetTypeId);
            Assert.AreEqual(0.6667m, item.Progression);
            using var home = await host.SendAsync(HttpMethod.Get, "/");
            var homeHtml = await home.Content.ReadAsStringAsync();
            StringAssert.Contains(homeHtml, "Continue reading");
            StringAssert.Contains(homeHtml, "href=\"/Read/" + asset + "\"");
            StringAssert.Contains(homeHtml, "<progress max=\"100\" value=\"67\"");
            using var bar = await host.SendAsync(HttpMethod.Get, "/Continue/Reading");
            var barHtml = await bar.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, bar.StatusCode, barHtml);
            StringAssert.Contains(barHtml, "data-continue-key=\"" + asset + ":0.6667\"");
            StringAssert.Contains(barHtml, "67% read");
            StringAssert.Contains(barHtml, "data-continue-dismiss");
            using var completed = await host.SendAsync(HttpMethod.Put, "/api/v1/reading/assets/" + asset + "/progress", new SaveReadingProgressDataV1("p2", 1m, true));
            Assert.AreEqual(HttpStatusCode.OK, completed.StatusCode, await completed.Content.ReadAsStringAsync());
            using var finished = await host.SendAsync(HttpMethod.Get, "/Continue/Reading");
            Assert.AreEqual(HttpStatusCode.NoContent, finished.StatusCode);
            using var page = await host.SendAsync(HttpMethod.Get, "/Read/" + asset);
            var html = await page.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, page.StatusCode, html);
            StringAssert.Contains(html, "data-page=\"2\"");
            StringAssert.Contains(html, "data-direction=\"rtl\"");
            StringAssert.Contains(html, "%3Apage%2F00002");
        }

        [TestMethod]
        public async Task Cache_EvictsOldReadsBeyondTheBudgetAndKeepsPermanentData()
        {
            using var storage = new TemporaryDirectory();
            await using var host = await LibraryTests.CreateHostAsync(storage.Path);
            var (first, second) = await CreateBooksAsync(host, storage.Path, "Old Book", "New Book");
            var old = await OpenAsync(host, first);
            using var saved = await host.SendAsync(HttpMethod.Put, "/api/v1/reading/assets/" + first + "/progress", new SaveReadingProgressDataV1("s0/b2", 0.2m, false));
            Assert.AreEqual(HttpStatusCode.OK, saved.StatusCode, await saved.Content.ReadAsStringAsync());

            // Recent reads are protected; an old one beyond the budget leaves the cache only.
            using var settingsRead = await host.SendAsync(HttpMethod.Get, "/api/v1/admin/instance/settings");
            var settings = (await settingsRead.Content.ReadFromJsonAsync<InstanceSettingsDetailsV1>())!;
            Assert.AreEqual(2048, settings.ReadingCacheMegabytes);
            using var budget = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/instance/settings",
                new UpdateInstanceSettingsDataV1(settings.Name, settings.UiLocaleId, settings.Version, 0, 100));
            Assert.AreEqual(HttpStatusCode.OK, budget.StatusCode, await budget.Content.ReadAsStringAsync());
            await host.Fixture.RunSqlAsync("UPDATE \"ReadingDocuments\" SET \"LastReadAt\" = now() - interval '30 days'");
            var recent = await OpenAsync(host, second);
            Assert.IsTrue(recent.IsCached);
            Assert.AreEqual(0L, await host.ScalarAsync("SELECT \"CacheBytes\" FROM \"ReadingDocuments\" WHERE \"Id\" = " + old.DocumentId));
            Assert.AreEqual(0L, await host.ScalarAsync("SELECT count(*) FROM \"ReadingResources\" WHERE \"ReadingDocumentId\" = " + old.DocumentId));
            Assert.AreEqual("s0/b2", await host.ScalarAsync("SELECT \"Locator\" FROM \"MediaProgress\""));
            Assert.IsTrue(File.Exists(Path.Combine(storage.Path, "1", "Books", "Old Book", "Old Book.epub")));

            // With the storage back the book is prepared again under the same document.
            var reopened = await OpenAsync(host, first);
            Assert.AreEqual(old.DocumentId, reopened.DocumentId);
            Assert.IsTrue(reopened.IsCached);
            Assert.AreEqual("s0/b2", reopened.Locator);
        }

        [TestMethod]
        public async Task ReadingModule_Off_LeavesBooksAsFilesOnly()
        {
            using var storage = new TemporaryDirectory();
            await using var host = await LibraryTests.CreateHostAsync(storage.Path, razor: true);
            var (first, _) = await CreateBooksAsync(host, storage.Path, "Plain Book", "Other Book");
            await host.Fixture.RunSqlAsync("UPDATE \"InstanceModules\" SET \"IsEnabled\" = FALSE WHERE \"Key\" = 'Reading'");

            // No preparation, no cache and no reader entry.
            using var opened = await host.SendAsync(HttpMethod.Post, "/api/v1/reading/assets/" + first + "/open");
            Assert.AreEqual(HttpStatusCode.Forbidden, opened.StatusCode);
            Assert.AreEqual(0L, await host.ScalarAsync("SELECT count(*) FROM \"ReadingDocuments\""));
            using var capabilities = await host.SendAsync(HttpMethod.Get, "/api/v1/instance/capabilities");
            CollectionAssert.DoesNotContain((await capabilities.Content.ReadFromJsonAsync<InstanceCapabilitiesDetailsV1>())!.Modules, "Reading");
            var workId = (long)(await host.ScalarAsync("SELECT \"WorkId\" FROM \"MediaAssets\" WHERE \"Id\" = " + first))!;
            using var details = await host.SendAsync(HttpMethod.Get, "/Details/" + workId);
            var html = await details.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, details.StatusCode, html);
            Assert.IsFalse(html.Contains("/Read/", StringComparison.Ordinal));
        }

        [TestMethod]
        public async Task Epub_RejectsForeignFilesAndUnsafePaths()
        {
            Assert.IsNull(EpubReader.Normalize("OEBPS/", "../../etc/passwd"));
            Assert.IsNull(EpubReader.Normalize("OEBPS/", "https://evil.example/x.png"));
            Assert.IsNull(EpubReader.Normalize("OEBPS/", "/etc/passwd"));
            Assert.AreEqual("OEBPS/images/a.png", EpubReader.Normalize("OEBPS/text/", "../images/a.png#x"));

            // A renamed archive is not an EPUB.
            using var storage = new TemporaryDirectory();
            await using var host = await LibraryTests.CreateHostAsync(storage.Path);
            var (first, _) = await CreateBooksAsync(host, storage.Path, "Fake Book", "Real Book");
            var fake = Path.Combine(storage.Path, "1", "Books", "Fake Book", "Fake Book.epub");
            var modified = File.GetLastWriteTimeUtc(fake);
            await using (var stream = File.Create(fake))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                await using var writer = new StreamWriter(archive.CreateEntry("readme.txt").Open());
                await writer.WriteAsync("not a book");
            }

            File.SetLastWriteTimeUtc(fake, modified);
            await host.Fixture.RunSqlAsync("UPDATE \"StoredFiles\" SET \"SizeBytes\" = " + new FileInfo(fake).Length + " WHERE \"RelativePath\" LIKE 'Fake Book/%'");
            using var opened = await host.SendAsync(HttpMethod.Post, "/api/v1/reading/assets/" + first + "/open");
            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, opened.StatusCode);
            StringAssert.Contains(await opened.Content.ReadAsStringAsync(), "reading_invalid_file");
        }

        internal static async Task<ReadingDocumentV1> OpenAsync(Host host, long assetId)
        {
            using var response = await host.SendAsync(HttpMethod.Post, "/api/v1/reading/assets/" + assetId + "/open");
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
            return (await response.Content.ReadFromJsonAsync<ReadingDocumentV1>())!;
        }

        // Two identified books in a book library.
        internal static async Task<(long First, long Second)> CreateBooksAsync(Host host, string storage, string firstTitle, string secondTitle)
        {
            var mountId = await LibraryTests.CreateMountAsync(host);
            foreach (var title in new[] { firstTitle, secondTitle })
            {
                var folder = Path.Combine(storage, mountId.ToString(System.Globalization.CultureInfo.InvariantCulture), "Books", title);
                Directory.CreateDirectory(folder);
                WriteEpub(Path.Combine(folder, title + ".epub"), title);
            }

            var rootId = await LibraryTests.CreateRootAsync(host, mountId, "Books", MediaType.Book);
            await LibraryTests.ScanAsync(host, rootId);
            var assets = new List<long>();
            foreach (var title in new[] { firstTitle, secondTitle })
            {
                var fileId = (long)(await host.ScalarAsync("SELECT \"Id\" FROM \"StoredFiles\" WHERE \"RelativePath\" LIKE '" + title + "/%'"))!;
                using var assigned = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/library/files/" + fileId + "/assignment", new AssignLibraryFileDataV1(null, title, null, null, null));
                Assert.AreEqual(HttpStatusCode.OK, assigned.StatusCode, await assigned.Content.ReadAsStringAsync());
                assets.Add((long)(await host.ScalarAsync("SELECT \"MediaAssetId\" FROM \"StoredFiles\" WHERE \"Id\" = " + fileId))!);
            }

            return (assets[0], assets[1]);
        }

        private static void WriteEpub(string path, string title) => File.WriteAllBytes(path, BuildEpub(title));

        // A small valid EPUB 3 with two sections, navigation, an image and hostile markup.
        internal static byte[] BuildEpub(string title)
        {
            using var stream = new MemoryStream();
            WriteArchive(stream, title);
            return stream.ToArray();
        }

        private static void WriteArchive(Stream stream, string title)
        {
            using var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);
            void Text(string name, string content, CompressionLevel level = CompressionLevel.Optimal)
            {
                using var writer = new StreamWriter(archive.CreateEntry(name, level).Open(), new UTF8Encoding(false));
                writer.Write(content);
            }

            Text("mimetype", "application/epub+zip", CompressionLevel.NoCompression);
            Text("META-INF/container.xml", """
                <?xml version="1.0"?>
                <container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container">
                  <rootfiles><rootfile full-path="OEBPS/content.opf" media-type="application/oebps-package+xml"/></rootfiles>
                </container>
                """);
            Text("OEBPS/content.opf", $$"""
                <?xml version="1.0"?>
                <package xmlns="http://www.idpf.org/2007/opf" version="3.0">
                  <metadata xmlns:dc="http://purl.org/dc/elements/1.1/"><dc:title>{{title}}</dc:title><dc:language>en</dc:language></metadata>
                  <manifest>
                    <item id="nav" href="nav.xhtml" media-type="application/xhtml+xml" properties="nav"/>
                    <item id="c1" href="text/ch1.xhtml" media-type="application/xhtml+xml"/>
                    <item id="c2" href="text/ch2.xhtml" media-type="application/xhtml+xml"/>
                    <item id="cover" href="images/cover.png" media-type="image/png"/>
                    <item id="escape" href="../../escape.png" media-type="image/png"/>
                  </manifest>
                  <spine><itemref idref="c1"/><itemref idref="c2"/></spine>
                </package>
                """);
            Text("OEBPS/nav.xhtml", """
                <html xmlns="http://www.w3.org/1999/xhtml" xmlns:epub="http://www.idpf.org/2007/ops"><body>
                  <nav epub:type="toc"><ol><li><a href="text/ch1.xhtml">Chapter One</a></li><li><a href="text/ch2.xhtml#part">Part Two</a></li></ol></nav>
                </body></html>
                """);
            Text("OEBPS/text/ch1.xhtml", """
                <!DOCTYPE html>
                <html xmlns="http://www.w3.org/1999/xhtml"><head><title>One</title><link rel="stylesheet" href="../style.css"/></head><body>
                  <h1 style="color:red" onclick="alert(1)">Chapter One</h1>
                  <script>alert('x')</script>
                  <p>It was a quiet morning.</p>
                  <img src="../images/cover.png" alt="Cover"/>
                  <img src="https://evil.example/track.png" alt=""/>
                  <iframe src="https://evil.example/"></iframe>
                  <p>Read <a href="ch2.xhtml#part">on</a>.</p>
                </body></html>
                """);
            Text("OEBPS/text/ch2.xhtml", """
                <html xmlns="http://www.w3.org/1999/xhtml"><body>
                  <h2 id="part">Part Two</h2>
                  <p>The second part begins.</p>
                  <p>And it ends.</p>
                </body></html>
                """);
            using var image = archive.CreateEntry("OEBPS/images/cover.png").Open();
            image.Write(Png);
        }
    }
}
