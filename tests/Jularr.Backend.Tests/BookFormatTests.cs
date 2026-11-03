using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using Jularr.Backend.Data.Admin.Library.V1;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.User.Reading.V1;
using Jularr.Backend.Logic.Acquisition;
using Jularr.Backend.Logic.Library;
using Jularr.Backend.Logic.Reading;
using Jularr.Integrations.Data.Storage;
using Host = Jularr.Backend.Tests.AuthenticationTests.Host;
using TemporaryDirectory = Jularr.Backend.Tests.LibraryTests.TemporaryDirectory;

namespace Jularr.Backend.Tests
{
    [TestClass]
    public sealed class BookFormatTests
    {
        [TestMethod]
        public async Task ContentCheck_NewFormats_TakeTheirContentAndNameTheFailedCheck()
        {
            static Task<string?> CheckAsync(string extension, byte[] bytes) => LibraryFileTypes.CheckDocumentAsync(extension, new MemoryStream(bytes), CancellationToken.None);
            var fb2 = BookFormatFixtures.Fb2();

            // Each format is taken by its content, a protected Kindle book too: it is a book, which the reader says it cannot open.
            Assert.IsNull(await CheckAsync(".fb2", fb2));
            Assert.IsNull(await CheckAsync(".fb2", [0xEF, 0xBB, 0xBF, .. fb2]));
            Assert.IsNull(await CheckAsync(".fb2", Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(Encoding.UTF8.GetString(fb2))).ToArray()));
            Assert.IsNull(await CheckAsync(".fb2.zip", BookFormatFixtures.Zip(("book.fb2", fb2))));
            Assert.IsNull(await CheckAsync(".txt", BookFormatFixtures.Text()));
            Assert.IsNull(await CheckAsync(".rtf", BookFormatFixtures.Rtf()));
            Assert.IsNull(await CheckAsync(".docx", BookFormatFixtures.Docx()));
            Assert.IsNull(await CheckAsync(".azw", BookFormatFixtures.Mobi("<p>x</p>")));
            Assert.IsNull(await CheckAsync(".azw3", BookFormatFixtures.Kf8()));
            Assert.IsNull(await CheckAsync(".mobi", BookFormatFixtures.PalmDoc("Plain text")));
            Assert.IsNull(await CheckAsync(".azw3", BookFormatFixtures.Protected()));

            // Malformed files and files under another format's name say which check they failed.
            Assert.AreEqual("content_fb2", await CheckAsync(".fb2", Encoding.UTF8.GetBytes("<html><body>not a book</body></html>")));
            Assert.AreEqual("content_fb2", await CheckAsync(".fb2.zip", BookFormatFixtures.Zip(("readme.txt", "x"u8.ToArray()))));
            Assert.AreEqual("content_fb2", await CheckAsync(".fb2.zip", fb2));
            Assert.AreEqual("content_txt", await CheckAsync(".txt", BookFormatFixtures.Png));
            Assert.AreEqual("content_rtf", await CheckAsync(".rtf", BookFormatFixtures.Text()));
            Assert.AreEqual("content_docx", await CheckAsync(".docx", BookFormatFixtures.Docx(withDocument: false)));
            Assert.AreEqual("content_docx", await CheckAsync(".docx", ReadingTests.BuildEpub("x")));
            Assert.AreEqual("content_azw", await CheckAsync(".azw", BookFormatFixtures.PalmDoc("Plain text")));
            Assert.AreEqual("content_epub", await CheckAsync(".epub", BookFormatFixtures.Docx()));
        }

        [TestMethod]
        public void ReadingCopy_Fb2_HasItsSectionsNotesPicturesAndTitle()
        {
            foreach (var (name, bytes) in new[] { ("Book.fb2", BookFormatFixtures.Fb2()), ("Book.fb2.zip", BookFormatFixtures.Zip(("inner/Book.fb2", BookFormatFixtures.Fb2()))) })
            {
                var (package, texts) = Convert(name, bytes);
                Assert.AreEqual(("Night Train", "en"), (package.Title, package.Language));
                CollectionAssert.AreEqual(new[] { "Night Train", "Chapter One", "Chapter Two", "A Part", "Notes" }, package.Toc.Select(entry => entry.Title).ToArray());
                CollectionAssert.AreEqual(new[] { 0, 0, 0, 1, 0 }, package.Toc.Select(entry => entry.Level).ToArray());
                var all = string.Join("\n", texts);
                StringAssert.Contains(all, "<em>slowly</em>");
                StringAssert.Contains(all, "<strong>carefully</strong>");
                StringAssert.Contains(all, "<p>A line of verse</p>");
                StringAssert.Contains(all, "A note about the train.");

                // The cover is a picture of the copy; the note link leads to the notes' section.
                Assert.AreEqual("image/png", package.Manifest.Single(item => item.Key.StartsWith("images/", StringComparison.Ordinal)).Value);
                StringAssert.Contains(texts[0], "<img src=\"images/");
                var notes = package.Spine[^1].Path;
                StringAssert.Contains(all, "href=\"" + notes + "#n1\"");
                StringAssert.Contains(texts[^1], "id=\"n1\"");
            }
        }

        [TestMethod]
        public void ReadingCopy_TextRtfAndDocx_KeepParagraphsHeadingsAndEmphasis()
        {
            // Plain text: paragraphs between blank lines join their lines; lines that name a chapter start one.
            var (text, textSections) = Convert("Book.txt", BookFormatFixtures.Text());
            CollectionAssert.AreEqual(new[] { "Chapter 1", "Chapter 2: Arrival" }, text.Toc.Select(entry => entry.Title).ToArray());
            StringAssert.Contains(string.Concat(textSections), "<p>The night train left at nine.</p>");
            StringAssert.Contains(string.Concat(textSections), "Grüße from the station.");

            // RTF: the title, headings by style and outline level, emphasis, code page and Unicode characters, the PNG and not the metafile stand-in nor the header.
            var (rich, richSections) = Convert("Book.rtf", BookFormatFixtures.Rtf());
            Assert.AreEqual("Rich Book", rich.Title);
            CollectionAssert.AreEqual(new[] { "Chapter One", "Chapter Two" }, rich.Toc.Select(entry => entry.Title).ToArray());
            var richText = string.Concat(richSections);
            StringAssert.Contains(richText, "<h1>Chapter One</h1>");
            StringAssert.Contains(richText, "<strong>Bold words</strong>");
            StringAssert.Contains(richText, "<em>slanted</em>");
            StringAssert.Contains(richText, "café naïve.");
            Assert.AreEqual(1, rich.Manifest.Count(item => item.Value == "image/png"));
            Assert.IsFalse(richText.Contains("Running header", StringComparison.Ordinal) || richText.Contains("Hidden Writer", StringComparison.Ordinal) || richText.Contains("Times", StringComparison.Ordinal));

            // DOCX: the title, heading styles, bold and a switched-off italic, the embedded picture and the table.
            var (word, wordSections) = Convert("Book.docx", BookFormatFixtures.Docx());
            Assert.AreEqual("Word Book", word.Title);
            CollectionAssert.AreEqual(new[] { "Chapter One", "A Part" }, word.Toc.Select(entry => entry.Title).ToArray());
            CollectionAssert.AreEqual(new[] { 0, 1 }, word.Toc.Select(entry => entry.Level).ToArray());
            var wordText = string.Concat(wordSections);
            StringAssert.Contains(wordText, "The night train left at nine. <strong>Bold words</strong> plain");
            StringAssert.Contains(wordText, "<td>Cell A</td><td>Cell B</td>");
            StringAssert.Contains(wordText, "<img src=\"images/00000.png\"");
        }

        [TestMethod]
        public void ReadingCopy_KindleFormats_ReadPalmDocMobiAndKf8()
        {
            // An old PalmDOC is plain text.
            var (palm, palmSections) = Convert("Old.mobi", BookFormatFixtures.PalmDoc("Chapter 1\n\nThe night train left at nine, and the night train came back at ten.\n\nChapter 2\n\nThe end."));
            CollectionAssert.AreEqual(new[] { "Chapter 1", "Chapter 2" }, palm.Toc.Select(entry => entry.Title).ToArray());
            StringAssert.Contains(string.Concat(palmSections), "the night train came back at ten.");

            // MOBI: page breaks start chapters named by their heading, image records are pictures, a link by file position finds its anchor in the other chapter.
            var html = "<html><head><guide><reference type=\"toc\" title=\"Contents\" filepos=0000000000/></guide></head><body><h1>Chapter One</h1><p><font size=\"+1\">The night train left at nine.</font></p>"
                + "<p><img recindex=\"00001\"/></p><p><a filepos=0000000XXX>Onward</a></p><mbp:pagebreak/><h2>Chapter Two</h2><p>The second chapter ends.</p></body></html>";
            var target = html.IndexOf("<h2>", StringComparison.Ordinal);
            html = html.Replace("0000000XXX", target.ToString("D10"), StringComparison.Ordinal);
            var (mobi, mobiSections) = Convert("Book.azw", BookFormatFixtures.Mobi(html));
            Assert.AreEqual(("Kindle Book", "en"), (mobi.Title, mobi.Language));
            CollectionAssert.AreEqual(new[] { "Chapter One", "Chapter Two" }, mobi.Toc.Select(entry => entry.Title).ToArray(), string.Join("\n", mobiSections));
            StringAssert.Contains(mobiSections[0], "The night train left at nine.");
            Assert.IsFalse(mobiSections[0].Contains("<font", StringComparison.OrdinalIgnoreCase));
            StringAssert.Contains(mobiSections[0], "<img src=\"images/00000.png\"");
            StringAssert.Contains(mobiSections[0], "href=\"" + mobi.Spine[1].Path + "#fp" + target + "\"");
            StringAssert.Contains(mobiSections[1], "<a id=\"fp" + target + "\"></a><h2>Chapter Two</h2>");

            // KF8: each skeleton with its fragment is a file; pictures by kindle:embed, links by fragment.
            var (kf8, kf8Sections) = Convert("Book.azw3", BookFormatFixtures.Kf8());
            Assert.AreEqual("Kindle Eight", kf8.Title);
            CollectionAssert.AreEqual(new[] { "Chapter One", "Chapter Two" }, kf8.Toc.Select(entry => entry.Title).ToArray());
            Assert.HasCount(2, kf8Sections);
            StringAssert.Contains(kf8Sections[0], "<h1>Chapter One</h1><p>The night train left at nine.</p>");
            StringAssert.Contains(kf8Sections[0], "<img src=\"images/00000.png\"");
            StringAssert.Contains(kf8Sections[0], "href=\"" + kf8.Spine[1].Path + "#kf8-part-1\"");
            StringAssert.Contains(kf8Sections[1], "The second file ends.");
            Assert.IsFalse(string.Concat(kf8Sections).Contains("margin", StringComparison.Ordinal), "The style flow is no text.");

            // A combined file reads its KF8 part, with the picture of its MOBI part.
            var (joint, jointSections) = Convert("Joint.azw3", BookFormatFixtures.Joint());
            Assert.AreEqual("Kindle Eight", joint.Title);
            StringAssert.Contains(jointSections[0], "<img src=\"images/00000.png\"");
            Assert.IsFalse(string.Concat(jointSections).Contains("old MOBI", StringComparison.Ordinal));
        }

        [TestMethod]
        public void ReadingCopy_ProtectedUnsupportedAndBrokenBooks_SayWhy()
        {
            Assert.ThrowsExactly<BookProtectedException>(() => BookConversion.ToEpub("Locked.azw3", new MemoryStream(BookFormatFixtures.Protected())));
            Assert.ThrowsExactly<BookProtectedException>(() => BookConversion.ToEpub("Locked.mobi", new MemoryStream(BookFormatFixtures.Mobi("<p>x</p>", encryption: 1))));
            Assert.ThrowsExactly<BookCompressionException>(() => BookConversion.ToEpub("Huff.mobi", new MemoryStream(BookFormatFixtures.Mobi("<p>x</p>", compression: 17480))));

            // Cut-off, malformed and empty files and archives without their document are no book; nothing throws anything else.
            var kf8 = BookFormatFixtures.Kf8();
            var fb2 = BookFormatFixtures.Fb2();
            foreach (var (name, bytes) in new[]
            {
                ("Cut.azw3", kf8[..400]), ("Cut.mobi", BookFormatFixtures.Mobi("<p>The night train left at nine.</p>")[..120]), ("Broken.fb2", fb2[..(fb2.Length / 2)]),
                ("Html.fb2", "<html><p>x</p></html>"u8.ToArray()), ("Empty.txt", Array.Empty<byte>()), ("Plain.rtf", "not rtf"u8.ToArray()), ("Empty.fb2.zip", BookFormatFixtures.Zip(("a.txt", "x"u8.ToArray()))),
                ("NoBody.docx", BookFormatFixtures.Docx(withDocument: false)), ("Text.docx", "PK not a zip"u8.ToArray()), ("Deep.rtf", Encoding.ASCII.GetBytes(@"{\rtf1" + new string('{', 600) + "x" + new string('}', 601)))
            })
            {
                Assert.ThrowsExactly<InvalidDataException>(() => BookConversion.ToEpub(name, new MemoryStream(bytes)), name);
            }

            // A corrupted byte in any place of a Kindle, RTF or Word file still ends in a reason, never another failure.
            foreach (var (name, book) in new[]
            {
                ("Damaged.azw3", kf8), ("Damaged.mobi", BookFormatFixtures.Mobi("<p>The night train <a filepos=0000000003>left</a> at nine.</p>")), ("Damaged.rtf", BookFormatFixtures.Rtf()),
                ("Damaged.docx", BookFormatFixtures.Docx())
            })
            {
                for (var position = 0; position < book.Length; position++)
                {
                    foreach (var value in new byte[] { 0x00, 0x7F, 0xFF })
                    {
                        var damaged = book.ToArray();
                        damaged[position] = value;
                        try
                        {
                            BookConversion.ToEpub(name, new MemoryStream(damaged));
                        }
                        catch (Exception exception) when (exception is InvalidDataException or BookCompressionException or BookProtectedException)
                        {
                        }
                    }
                }
            }
        }

        [TestMethod]
        public void Placement_NewFormats_PlaceOneFilePerFormatAndLeaveTheReleaseNote()
        {
            static StorageFile[] Files(params string[] names) => [.. names.Select(name => new StorageFile("Release/" + name, 1000, DateTime.UtcNow))];
            var unplaced = new List<UnplacedFile>();
            var placed = ImportPlacementLogic.Plan(MediaType.Book, null, "Night Train", 2020, null, Files("Night Train.fb2.zip", "Night Train.docx", "release notes.txt", "Night Train.azw"),
                NamingPatternTests.DefaultSet, null, "Ann Author", unplaced: unplaced);
            CollectionAssert.AreEqual(new[] { ".azw", ".docx", ".fb2.zip" }, placed.Select(placement => placement.Extension).Order(StringComparer.Ordinal).ToArray());
            Assert.IsTrue(placed.All(placement => placement.TargetStem == "Ann Author/Night Train (2020)/Night Train"), placed[0].TargetStem);
            Assert.HasCount(0, unplaced);

            // A text file alone is the book.
            Assert.AreEqual(".txt", ImportPlacementLogic.Plan(MediaType.Book, null, "Night Train", 2020, null, Files("Night Train.txt"), NamingPatternTests.DefaultSet, null, "Ann Author").Single().Extension);
            Assert.AreEqual(MediaAssetType.Ebook, LibraryFileTypes.GetAssetType("Ann Author/Night Train.FB2.zip"));
            Assert.AreEqual("FB2", LibraryFileTypes.FormatName("Night Train.fb2.zip"));
        }

        [TestMethod]
        public async Task Reader_OpensConvertedBooksAndNamesAProtectedOne()
        {
            using var storage = new TemporaryDirectory();
            await using var host = await LibraryTests.CreateHostAsync(storage.Path);
            var assets = await CreateBooksAsync(host, storage.Path, ("Night Train", "Night Train.fb2", BookFormatFixtures.Fb2()), ("Kindle Eight", "Kindle Eight.azw3", BookFormatFixtures.Kf8()),
                ("Locked Book", "Locked Book.azw3", BookFormatFixtures.Protected()));

            // A FictionBook opens through its reading copy; the file itself stays as it was.
            var file = Path.Combine(storage.Path, "1", "Books", "Night Train", "Night Train.fb2");
            var before = File.ReadAllBytes(file);
            var document = await ReadingTests.OpenAsync(host, assets[0]);
            Assert.IsTrue(document.IsCached);
            Assert.AreEqual(5, document.SectionCount);
            using var section = await host.SendAsync(HttpMethod.Get, "/api/v1/reading/documents/" + document.DocumentId + "/sections/1");
            var html = (await section.Content.ReadFromJsonAsync<ReadingSectionV1>())!.Html;
            StringAssert.Contains(html, "The night train left at nine.");
            CollectionAssert.AreEqual(before, File.ReadAllBytes(file));
            Assert.AreEqual(5, (await ReadingTests.OpenAsync(host, assets[0])).SectionCount);

            // So does a KF8 book, with its picture from the cache.
            var kindle = await ReadingTests.OpenAsync(host, assets[1]);
            using var first = await host.SendAsync(HttpMethod.Get, "/api/v1/reading/documents/" + kindle.DocumentId + "/sections/0");
            StringAssert.Contains((await first.Content.ReadFromJsonAsync<ReadingSectionV1>())!.Html, "/resource?path=images%2F00000.png");

            // A protected one says so and leaves nothing behind.
            using var locked = await host.SendAsync(HttpMethod.Post, "/api/v1/reading/assets/" + assets[2] + "/open");
            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, locked.StatusCode);
            StringAssert.Contains(await locked.Content.ReadAsStringAsync(), "reading_drm");
            Assert.AreEqual(2L, await host.ScalarAsync("SELECT count(*) FROM \"ReadingDocuments\""));
        }

        // The copy as its package and the HTML of its sections.
        private static (Data.Logic.Reading.EpubPackage Package, string[] Sections) Convert(string name, byte[] bytes)
        {
            var epub = BookConversion.ToEpub(name, new MemoryStream(bytes));
            using var archive = new ZipArchive(new MemoryStream(epub), ZipArchiveMode.Read);
            var package = EpubReader.Read(archive);
            return (package, [.. package.Spine.Select(item => EpubReader.ReadText(archive.GetEntry(item.Path)!))]);
        }

        // Books of a book library, one folder each, scanned and assigned.
        private static async Task<long[]> CreateBooksAsync(Host host, string storage, params (string Title, string Name, byte[] Bytes)[] books)
        {
            var mountId = await LibraryTests.CreateMountAsync(host);
            foreach (var (title, name, bytes) in books)
            {
                var folder = Path.Combine(storage, mountId.ToString(System.Globalization.CultureInfo.InvariantCulture), "Books", title);
                Directory.CreateDirectory(folder);
                File.WriteAllBytes(Path.Combine(folder, name), bytes);
            }

            var rootId = await LibraryTests.CreateRootAsync(host, mountId, "Books", MediaType.Book);
            await LibraryTests.ScanAsync(host, rootId);
            var assets = new List<long>();
            foreach (var (title, _, _) in books)
            {
                var fileId = (long)(await host.ScalarAsync("SELECT \"Id\" FROM \"StoredFiles\" WHERE \"RelativePath\" LIKE '" + title + "/%'"))!;
                if (await host.ScalarAsync("SELECT \"MediaAssetId\" FROM \"StoredFiles\" WHERE \"Id\" = " + fileId) is not long assetId)
                {
                    using var assigned = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/library/files/" + fileId + "/assignment", new AssignLibraryFileDataV1(null, title, null, null, null));
                    Assert.AreEqual(HttpStatusCode.OK, assigned.StatusCode, await assigned.Content.ReadAsStringAsync());
                    assetId = (long)(await host.ScalarAsync("SELECT \"MediaAssetId\" FROM \"StoredFiles\" WHERE \"Id\" = " + fileId))!;
                }

                assets.Add(assetId);
            }

            return [.. assets];
        }
    }
}
