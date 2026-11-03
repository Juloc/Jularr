using System.Diagnostics;
using System.Net;
using System.Text;
using Jularr.Backend.Data.Admin.Library.V1;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Enums.Reading;
using Jularr.Backend.Logic.Acquisition;
using Jularr.Backend.Logic.Library;
using Jularr.Integrations.Data.Djvu;
using Jularr.Integrations.Data.Storage;
using Jularr.Integrations.Logic.Djvu;
using SkiaSharp;
using Host = Jularr.Backend.Tests.AuthenticationTests.Host;
using TemporaryDirectory = Jularr.Backend.Tests.LibraryTests.TemporaryDirectory;

namespace Jularr.Backend.Tests
{
    // DjVu books: taken by their content, placed like any book, and read as pages DjVuLibre draws when they are first shown.
    // Tests that draw need JULARR_TEST_DJVULIBRE, the folder of ddjvu, djvused, c44, cjb2 and djvm (CI and the image have them).
    [TestClass]
    public sealed class DjvuTests
    {
        [TestMethod]
        public async Task ContentCheck_Djvu_TakesOneAndSeveralPagesAndNamesAWrongFile()
        {
            static Task<string?> CheckAsync(string extension, byte[] bytes) => LibraryFileTypes.CheckDocumentAsync(extension, new MemoryStream(bytes), CancellationToken.None);
            static byte[] Iff(string form) => [.. "AT&TFORM"u8, 0, 0, 0, 40, .. Encoding.ASCII.GetBytes(form), .. "INFO"u8, .. new byte[36]];

            // A single page (DJVU) and a bundled book (DJVM), under either extension.
            Assert.IsNull(await CheckAsync(".djvu", Iff("DJVU")));
            Assert.IsNull(await CheckAsync(".djvu", Iff("DJVM")));
            Assert.IsNull(await CheckAsync(".DJV", Iff("DJVM")));

            // Another IFF form, another file under the name or a DjVu under another format's name fail their check.
            Assert.AreEqual("content_djvu", await CheckAsync(".djvu", Iff("DJVI")));
            Assert.AreEqual("content_djvu", await CheckAsync(".djv", "%PDF-1.7"u8.ToArray()));
            Assert.AreEqual("content_djvu", await CheckAsync(".djvu", "AT&T"u8.ToArray()));
            Assert.AreEqual("content_pdf", await CheckAsync(".pdf", Iff("DJVM")));

            // A DjVu is a book of every book library.
            Assert.AreEqual(MediaAssetType.Ebook, LibraryFileTypes.GetAssetType("Ann Author/Night Train.djvu"));
            Assert.AreEqual(MediaAssetType.Ebook, LibraryFileTypes.GetAssetType("Night Train.DJV"));
            Assert.IsTrue(LibraryFileTypes.GetScanExtensions(MediaType.Book, MediaSubtype.Manga).Contains(".djvu"));
            Assert.IsTrue(LibraryFileTypes.GetScanExtensions(MediaType.Book, MediaSubtype.LightNovel).Contains(".djv"));
            Assert.AreEqual("DJVU", LibraryFileTypes.FormatName("Night Train.djvu"));
        }

        [TestMethod]
        public void Placement_DjvuBookAndMangaVolume_ArePlacedUnderTheirNames()
        {
            static StorageFile[] Files(params string[] names) => [.. names.Select(name => new StorageFile("Release/" + name, 1000, DateTime.UtcNow))];

            // A book: the DjVu under the book's name, its release note left behind.
            var unplaced = new List<UnplacedFile>();
            var book = ImportPlacementLogic.Plan(MediaType.Book, null, "Night Train", 2020, null, Files("Night Train.djvu", "release notes.txt"), NamingPatternTests.DefaultSet, null, "Ann Author",
                unplaced: unplaced);
            Assert.AreEqual(".djvu", book.Single().Extension);
            Assert.AreEqual("Ann Author/Night Train (2020)/Night Train", book.Single().TargetStem);
            Assert.HasCount(0, unplaced);

            // A manga volume in DjVu beside one in CBZ: each is its volume.
            var manga = ImportPlacementLogic.Plan(MediaType.Book, MediaSubtype.Manga, "Example Manga", null, null, Files("Example Manga v01.djvu", "Example Manga v02.cbz"), NamingPatternTests.DefaultSet,
                null);
            CollectionAssert.AreEqual(new[] { ".cbz", ".djvu" }, manga.Select(placement => placement.Extension).Order(StringComparer.Ordinal).ToArray());
        }

        [TestMethod]
        public void Encode_PnmPages_BecomeWebPicturesOfTheirSize()
        {
            // Black and white (P4) becomes a PNG; grey (P5) and colour (P6) a JPEG, each of its own size.
            var bitonal = DjvuTools.Encode([.. "P4\n# drawn\n10 2\n"u8, 0xFF, 0xC0, 0x00, 0x00]);
            Assert.AreEqual(("image/png", 10, 2), (bitonal.ContentType, bitonal.Width, bitonal.Height));
            using (var decoded = SKBitmap.Decode(bitonal.Bytes))
            {
                Assert.AreEqual(SKColors.Black.Red, decoded.GetPixel(0, 0).Red);
                Assert.AreEqual((byte)255, decoded.GetPixel(0, 1).Red);
            }

            var grey = DjvuTools.Encode([.. "P5 3 2 255\n"u8, 0, 128, 255, 255, 128, 0]);
            Assert.AreEqual(("image/jpeg", 3, 2), (grey.ContentType, grey.Width, grey.Height));
            var colour = DjvuTools.Encode([.. "P6\n2 1\n255\n"u8, 255, 0, 0, 0, 0, 255]);
            Assert.AreEqual(("image/jpeg", 2, 1), (colour.ContentType, colour.Width, colour.Height));
            using (var decoded = SKBitmap.Decode(colour.Bytes))
            {
                Assert.AreEqual(2, decoded.Width);
            }

            // Pictures larger than the bound, cut short or of another kind are refused.
            Assert.ThrowsExactly<InvalidDataException>(() => DjvuTools.Encode("P5 4000 4000 255\n"u8));
            Assert.ThrowsExactly<InvalidDataException>(() => DjvuTools.Encode([.. "P5 3 2 255\n"u8, 1, 2]));
            Assert.ThrowsExactly<InvalidDataException>(() => DjvuTools.Encode("P3 1 1 255\n0 0 0"u8));
            Assert.ThrowsExactly<InvalidDataException>(() => DjvuTools.Encode("P6 1 1 65535\n"u8));
        }

        [TestMethod]
        public async Task Tools_WithDjvulibre_CountMeasureAndDrawBoundedPages()
        {
            var folder = RequireTools();
            using var work = new TemporaryDirectory();
            var book = await WriteBookAsync(folder, work.Path, large: true);
            var tools = Tools(folder);

            // Three pages: colour, black and white, and a large black and white scan.
            Assert.AreEqual(3, await tools.CountPagesAsync(book, CancellationToken.None));
            var sizes = await tools.ReadSizesAsync(book, [1, 2, 3], CancellationToken.None);
            CollectionAssert.AreEqual(new[] { (600, 800), (600, 800), (2500, 3500) }, sizes.Select(size => (size.Width, size.Height)).ToArray());

            // A page within the box keeps its size; the large one is drawn into the box at its proportions.
            var colour = await tools.RenderAsync(book, sizes[0], CancellationToken.None);
            Assert.AreEqual(("image/jpeg", 600, 800), (colour.ContentType, colour.Width, colour.Height));
            var text = await tools.RenderAsync(book, sizes[1], CancellationToken.None);
            Assert.AreEqual(("image/png", 600, 800), (text.ContentType, text.Width, text.Height));
            var large = await tools.RenderAsync(book, sizes[2], CancellationToken.None);
            Assert.IsTrue(large.Width <= DjvuTools.MaximumWidth && large.Height <= DjvuTools.MaximumHeight && large.Height > 2000, large.Width + "x" + large.Height);
            using (var decoded = SKBitmap.Decode(large.Bytes))
            {
                Assert.AreEqual(large.Width, decoded.Width);
            }

            // Not a DjVu, a relative path and a missing tool each say so.
            var other = Path.Combine(work.Path, "other.djvu");
            File.WriteAllText(other, "not a book");
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => tools.CountPagesAsync(other, CancellationToken.None));
            await Assert.ThrowsExactlyAsync<ArgumentException>(() => tools.CountPagesAsync("-e", CancellationToken.None));
            var missing = new DjvuTools { DdjvuPath = Path.Combine(work.Path, "no-ddjvu"), DjvusedPath = Path.Combine(work.Path, "no-djvused") };
            await Assert.ThrowsExactlyAsync<DjvuUnavailableException>(() => missing.CountPagesAsync(book, CancellationToken.None));
            await Assert.ThrowsExactlyAsync<DjvuUnavailableException>(() => missing.RenderAsync(book, sizes[0], CancellationToken.None));

            // A tool that does not finish in time is stopped.
            if (!OperatingSystem.IsWindows())
            {
                var slow = Path.Combine(work.Path, "slow-ddjvu");
                File.WriteAllText(slow, "#!/bin/sh\nsleep 30\n");
                File.SetUnixFileMode(slow, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                var waiting = new DjvuTools { DdjvuPath = slow, DjvusedPath = tools.DjvusedPath, PageTime = TimeSpan.FromMilliseconds(300) };
                var watch = Stopwatch.StartNew();
                await Assert.ThrowsExactlyAsync<TimeoutException>(() => waiting.RenderAsync(book, sizes[0], CancellationToken.None));
                Assert.IsTrue(watch.Elapsed < TimeSpan.FromSeconds(10), watch.Elapsed.ToString());
            }
        }

        [TestMethod]
        public async Task Reader_DjvuBook_OpensAsItsPagesDrawnWhenFirstShown()
        {
            var folder = RequireTools();
            using var storage = new TemporaryDirectory();
            using var work = new TemporaryDirectory();
            var bytes = await File.ReadAllBytesAsync(await WriteBookAsync(folder, work.Path, large: false));
            await using var host = await LibraryTests.CreateHostAsync(storage.Path, razor: true, settings: Settings(folder));
            var asset = await CreateBookAsync(host, storage.Path, "Night Train", "Night Train.djvu", bytes);

            // Opening counts the pages; none is drawn yet.
            var document = await ReadingTests.OpenAsync(host, asset);
            Assert.AreEqual((ReadingFormatType.Comic, 2, true, true), (document.ReadingFormatTypeId, document.SectionCount, document.RendersPages, document.IsCached));
            Assert.AreEqual(0L, await host.ScalarAsync("SELECT count(*) FROM \"ReadingResources\" WHERE \"ReadingDocumentId\" = " + document.DocumentId));

            // The Reader shows the first page through its page handler, which draws it and the next one into the cache.
            using var reader = await host.SendAsync(HttpMethod.Get, "/Read/" + asset);
            var html = await reader.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, reader.StatusCode, html);
            var pages = "/Read/" + asset + "?handler=Page&amp;document=" + document.DocumentId + "&amp;path=";
            StringAssert.Contains(html, "data-pages=\"" + pages + "\"");
            using var first = await host.SendAsync(HttpMethod.Get, "/Read/" + asset + "?handler=Page&document=" + document.DocumentId + "&path=%3Apage%2F00000");
            Assert.AreEqual(HttpStatusCode.OK, first.StatusCode, await first.Content.ReadAsStringAsync());
            Assert.AreEqual("image/jpeg", first.Content.Headers.ContentType!.MediaType);
            using (var decoded = SKBitmap.Decode(await first.Content.ReadAsByteArrayAsync()))
            {
                Assert.AreEqual((600, 800), (decoded.Width, decoded.Height));
            }

            Assert.AreEqual(2L, await host.ScalarAsync("SELECT count(*) FROM \"ReadingResources\" WHERE \"ReadingDocumentId\" = " + document.DocumentId));
            Assert.IsTrue((long)(await host.ScalarAsync("SELECT \"CacheBytes\" FROM \"ReadingDocuments\" WHERE \"Id\" = " + document.DocumentId))! > 0);

            // The second page comes from the cache; a page past the end and a path that names no page are not found.
            using var second = await host.SendAsync(HttpMethod.Get, "/Read/" + asset + "?handler=Page&document=" + document.DocumentId + "&path=%3Apage%2F00001");
            Assert.AreEqual("image/png", second.Content.Headers.ContentType!.MediaType);
            using var beyond = await host.SendAsync(HttpMethod.Get, "/Read/" + asset + "?handler=Page&document=" + document.DocumentId + "&path=%3Apage%2F00002");
            Assert.AreEqual(HttpStatusCode.NotFound, beyond.StatusCode);
            using var other = await host.SendAsync(HttpMethod.Get, "/Read/" + asset + "?handler=Page&document=" + document.DocumentId + "&path=cover.png");
            Assert.AreEqual(HttpStatusCode.NotFound, other.StatusCode);

            // The file in the library stays as it was, and opening again reuses the document.
            CollectionAssert.AreEqual(bytes, File.ReadAllBytes(Directory.EnumerateFiles(storage.Path, "Night Train.djvu", SearchOption.AllDirectories).Single()));
            Assert.AreEqual(document.DocumentId, (await ReadingTests.OpenAsync(host, asset)).DocumentId);
        }

        [TestMethod]
        public async Task Reader_DjvuWithoutDjvulibre_SaysWhatTheServerNeeds()
        {
            using var storage = new TemporaryDirectory();
            await using var host = await LibraryTests.CreateHostAsync(storage.Path, settings: new Dictionary<string, string> { ["Reading:DdjvuPath"] = "", ["Reading:DjvusedPath"] = "" });
            var asset = await CreateBookAsync(host, storage.Path, "Night Train", "Night Train.djvu", [.. "AT&TFORM"u8, 0, 0, 0, 4, .. "DJVU"u8]);

            using var opened = await host.SendAsync(HttpMethod.Post, "/api/v1/reading/assets/" + asset + "/open");
            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, opened.StatusCode);
            StringAssert.Contains(await opened.Content.ReadAsStringAsync(), "reading_djvu_unavailable");
            Assert.AreEqual(0L, await host.ScalarAsync("SELECT count(*) FROM \"ReadingDocuments\""));
        }

        private static string RequireTools()
        {
            var folder = Environment.GetEnvironmentVariable("JULARR_TEST_DJVULIBRE");
            if (folder is not { Length: > 0 } || !File.Exists(Path.Combine(folder, "ddjvu")))
            {
                Assert.Inconclusive("JULARR_TEST_DJVULIBRE names no folder with DjVuLibre's tools.");
            }

            return folder!;
        }

        private static DjvuTools Tools(string folder) => new() { DdjvuPath = Path.Combine(folder, "ddjvu"), DjvusedPath = Path.Combine(folder, "djvused") };

        private static Dictionary<string, string> Settings(string folder) => new()
        {
            ["Reading:DdjvuPath"] = Path.Combine(folder, "ddjvu"),
            ["Reading:DjvusedPath"] = Path.Combine(folder, "djvused")
        };

        // A bundled book made with DjVuLibre's own encoders: a colour page (c44), a black and white page (cjb2) and, when asked, a large scan.
        private static async Task<string> WriteBookAsync(string folder, string work, bool large)
        {
            const int Width = 600, Height = 800;
            var colour = new byte[Width * Height * 3];
            for (var index = 0; index < colour.Length; index += 3)
            {
                (colour[index], colour[index + 1], colour[index + 2]) = ((byte)(index / 3 % Width * 255 / Width), (byte)(index / 3 / Width * 255 / Height), 128);
            }

            await File.WriteAllBytesAsync(Path.Combine(work, "colour.ppm"), [.. Encoding.ASCII.GetBytes("P6\n600 800\n255\n"), .. colour]);
            await File.WriteAllBytesAsync(Path.Combine(work, "text.pbm"), Bitonal(Width, Height));
            await RunAsync(folder, "c44", work, "colour.ppm", "colour.djvu");
            await RunAsync(folder, "cjb2", work, "text.pbm", "text.djvu");
            string[] pages = ["colour.djvu", "text.djvu"];
            if (large)
            {
                await File.WriteAllBytesAsync(Path.Combine(work, "scan.pbm"), Bitonal(2500, 3500));
                await RunAsync(folder, "cjb2", work, "scan.pbm", "scan.djvu");
                pages = [.. pages, "scan.djvu"];
            }

            await RunAsync(folder, "djvm", work, ["-c", "book.djvu", .. pages]);
            return Path.Combine(work, "book.djvu");
        }

        // Bands of black and white, like lines of text.
        private static byte[] Bitonal(int width, int height)
        {
            var row = (width + 7) / 8;
            var pixels = new byte[row * height];
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < row; x++)
                {
                    pixels[y * row + x] = (byte)((y / 20 + x / 4) % 3 == 0 ? 0xFF : 0x00);
                }
            }

            return [.. Encoding.ASCII.GetBytes("P4\n" + width + " " + height + "\n"), .. pixels];
        }

        private static async Task RunAsync(string folder, string tool, string work, params string[] arguments)
        {
            var start = new ProcessStartInfo(Path.Combine(folder, tool)) { WorkingDirectory = work, RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
            foreach (var argument in arguments)
            {
                start.ArgumentList.Add(argument);
            }

            using var process = Process.Start(start)!;
            var errors = process.StandardError.ReadToEndAsync();
            await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();
            Assert.AreEqual(0, process.ExitCode, tool + ": " + await errors);
        }

        // A book of a book library, scanned and assigned.
        private static async Task<long> CreateBookAsync(Host host, string storage, string title, string name, byte[] bytes)
        {
            var mountId = await LibraryTests.CreateMountAsync(host);
            var folder = Path.Combine(storage, mountId.ToString(System.Globalization.CultureInfo.InvariantCulture), "Books", title);
            Directory.CreateDirectory(folder);
            await File.WriteAllBytesAsync(Path.Combine(folder, name), bytes);
            var rootId = await LibraryTests.CreateRootAsync(host, mountId, "Books", MediaType.Book);
            await LibraryTests.ScanAsync(host, rootId);
            var fileId = (long)(await host.ScalarAsync("SELECT \"Id\" FROM \"StoredFiles\" WHERE \"RelativePath\" LIKE '" + title + "/%'"))!;
            if (await host.ScalarAsync("SELECT \"MediaAssetId\" FROM \"StoredFiles\" WHERE \"Id\" = " + fileId) is long assetId)
            {
                return assetId;
            }

            using var assigned = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/library/files/" + fileId + "/assignment", new AssignLibraryFileDataV1(null, title, null, null, null));
            Assert.AreEqual(HttpStatusCode.OK, assigned.StatusCode, await assigned.Content.ReadAsStringAsync());
            return (long)(await host.ScalarAsync("SELECT \"MediaAssetId\" FROM \"StoredFiles\" WHERE \"Id\" = " + fileId))!;
        }
    }
}
