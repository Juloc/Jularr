using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using Jularr.Backend.Data.Admin.Instance.V1;
using Jularr.Backend.Data.Admin.Library.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
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
            Assert.IsFalse((await capabilities.Content.ReadFromJsonAsync<InstanceCapabilitiesDetailsV1>())!.CanRead);
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

        private static async Task<ReadingDocumentV1> OpenAsync(Host host, long assetId)
        {
            using var response = await host.SendAsync(HttpMethod.Post, "/api/v1/reading/assets/" + assetId + "/open");
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
            return (await response.Content.ReadFromJsonAsync<ReadingDocumentV1>())!;
        }

        // Two identified books in a book library.
        private static async Task<(long First, long Second)> CreateBooksAsync(Host host, string storage, string firstTitle, string secondTitle)
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
