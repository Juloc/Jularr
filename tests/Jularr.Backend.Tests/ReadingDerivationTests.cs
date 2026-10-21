using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Reading;
using Jularr.Backend.Data.System.Reading.V1;
using Jularr.Backend.Data.User.Reading.V1;
using Jularr.Backend.Service.Core;
using Jularr.Backend.Service.System.Reading.V1;
using Microsoft.Extensions.DependencyInjection;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using TemporaryDirectory = Jularr.Backend.Tests.LibraryTests.TemporaryDirectory;

namespace Jularr.Backend.Tests
{
    // The reading copy of a digital PDF: written beside it, preferred only while it is current, never over a book's own EPUB.
    [TestClass]
    public sealed class ReadingDerivationTests
    {
        [TestMethod]
        public async Task DigitalPdf_BecomesAReadingCopyBesideIt_ReadFromWhileCurrent_AndIsConvertedOnce()
        {
            using var storage = new TemporaryDirectory();
            await using var host = await LibraryTests.CreateHostAsync(storage.Path, razor: true);
            var (_, second) = await ReadingTests.CreateBooksAsync(host, storage.Path, "First Book", "Second Book");
            var pdf = DigitalPdf();
            await PutPdfAsync(host, storage.Path, second, pdf, ownEpub: false);

            // Off, nothing is converted and the book reads from its PDF as before.
            await using (var scope = host.Services.CreateAsyncScope())
            {
                await Assert.ThrowsExactlyAsync<System.Security.SecurityException>(() => Derive(scope));
            }

            await host.Fixture.RunSqlAsync("UPDATE \"InstanceModules\" SET \"IsEnabled\" = TRUE WHERE \"Key\" = 'SmartPdf'");
            using var before = await host.SendAsync(HttpMethod.Get, "/api/v1/reading/assets/" + second + "/source");
            var preparing = (await before.Content.ReadFromJsonAsync<ReadingSourceV1>())!;
            Assert.AreEqual(ReadingFormatType.Pdf, preparing.ReadingFormatTypeId);
            Assert.AreEqual(ReadingDerivationStatus.Preparing, preparing.ReadingDerivationStatusTypeId);

            // One PDF per call; then there is nothing left to do.
            await using (var scope = host.Services.CreateAsyncScope())
            {
                Assert.IsTrue(await Derive(scope));
                Assert.IsFalse(await Derive(scope));
            }

            // The copy sits beside the PDF as a file of the same book, which stays untouched.
            var folder = Path.Combine(storage.Path, "1", "Books", "Second Book");
            Assert.IsTrue(File.Exists(Path.Combine(folder, "Second Book.smart.epub")));
            Assert.IsFalse(Directory.EnumerateFiles(folder, "*.partial").Any());
            CollectionAssert.AreEqual(pdf, File.ReadAllBytes(Path.Combine(folder, "Second Book.pdf")));
            Assert.AreEqual(1L, await host.ScalarAsync("SELECT count(*) FROM \"StoredFiles\" WHERE \"MediaAssetId\" = " + second + " AND \"RelativePath\" = 'Second Book/Second Book.smart.epub'"));
            Assert.AreEqual((short)2, await host.ScalarAsync("SELECT \"ReadingDerivationStatusTypeId\" FROM \"ReadingDerivations\""));
            Assert.AreEqual(32, ((byte[])(await host.ScalarAsync("SELECT \"SourceSha256\" FROM \"ReadingDerivations\""))!).Length);

            // The book now opens from the copy, with its figure page, and the original stays available.
            var document = await ReadingTests.OpenAsync(host, second);
            Assert.AreEqual(ReadingFormatType.Epub, document.ReadingFormatTypeId);
            using var section = await host.SendAsync(HttpMethod.Get, "/api/v1/reading/documents/" + document.DocumentId + "/sections/0");
            var html = (await section.Content.ReadFromJsonAsync<ReadingSectionV1>())!.Html;
            StringAssert.Contains(html, "data-figure=\"page\"");
            using var source = await host.SendAsync(HttpMethod.Get, "/api/v1/reading/assets/" + second + "/source");
            Assert.IsTrue((await source.Content.ReadFromJsonAsync<ReadingSourceV1>())!.HasPdf);
            using var epub = ZipFile.OpenRead(Path.Combine(folder, "Second Book.smart.epub"));
            StringAssert.Contains(ReadingTests.Entry(epub, "OEBPS/s00000.xhtml"), "epub:type=\"figure\"");

            // A PDF that changed is no longer served as its old copy: the book reads from the PDF until a new copy is ready.
            await host.Fixture.RunSqlAsync("UPDATE \"StoredFiles\" SET \"SizeBytes\" = \"SizeBytes\" + 1 WHERE \"RelativePath\" = 'Second Book/Second Book.pdf'");
            using var stale = await host.SendAsync(HttpMethod.Get, "/api/v1/reading/assets/" + second + "/source");
            Assert.AreEqual(ReadingFormatType.Pdf, (await stale.Content.ReadFromJsonAsync<ReadingSourceV1>())!.ReadingFormatTypeId);
            await using (var scope = host.Services.CreateAsyncScope())
            {
                Assert.IsTrue(await Derive(scope));
            }

            using var fresh = await host.SendAsync(HttpMethod.Get, "/api/v1/reading/assets/" + second + "/source");
            Assert.AreEqual(ReadingFormatType.Epub, (await fresh.Content.ReadFromJsonAsync<ReadingSourceV1>())!.ReadingFormatTypeId);
            Assert.AreEqual(1L, await host.ScalarAsync("SELECT count(*) FROM \"StoredFiles\" WHERE \"RelativePath\" LIKE '%.smart.epub'"));
        }

        [TestMethod]
        public async Task BookWithItsOwnEpub_IsNeverGivenAReadingCopy_AndAScanStaysAPdf()
        {
            using var storage = new TemporaryDirectory();
            await using var host = await LibraryTests.CreateHostAsync(storage.Path, razor: true);
            var (first, second) = await ReadingTests.CreateBooksAsync(host, storage.Path, "First Book", "Second Book");
            await host.Fixture.RunSqlAsync("UPDATE \"InstanceModules\" SET \"IsEnabled\" = TRUE WHERE \"Key\" = 'SmartPdf'");

            // The first book keeps its EPUB beside a PDF; the second has only a scan without any text.
            await PutPdfAsync(host, storage.Path, first, DigitalPdf(), ownEpub: true);
            var scan = new PdfDocumentBuilder();
            foreach (var _ in Enumerable.Range(0, 3))
            {
                scan.AddPage(PageSize.A4).AddPng(ReadingTests.Picture(120, 80), new PdfRectangle(0, 0, 595, 842));
            }

            var scanned = scan.Build();
            await PutPdfAsync(host, storage.Path, second, scanned, ownEpub: false);
            await using (var scope = host.Services.CreateAsyncScope())
            {
                Assert.IsTrue(await Derive(scope));
                Assert.IsFalse(await Derive(scope));
            }

            Assert.AreEqual(0L, await host.ScalarAsync("SELECT count(*) FROM \"ReadingDerivations\" WHERE \"StoredFileId\" IN (SELECT \"Id\" FROM \"StoredFiles\" WHERE \"MediaAssetId\" = " + first + ")"));
            Assert.AreEqual((short)3, await host.ScalarAsync("SELECT \"ReadingDerivationStatusTypeId\" FROM \"ReadingDerivations\""));
            Assert.AreEqual(0L, await host.ScalarAsync("SELECT count(*) FROM \"StoredFiles\" WHERE \"RelativePath\" LIKE '%.smart.epub'"));
            Assert.IsFalse(Directory.EnumerateFiles(storage.Path, "*.smart.epub", SearchOption.AllDirectories).Any());
            using var manual = await host.SendAsync(HttpMethod.Get, "/api/v1/reading/assets/" + first + "/source");
            Assert.AreEqual(ReadingFormatType.Epub, (await manual.Content.ReadFromJsonAsync<ReadingSourceV1>())!.ReadingFormatTypeId);
            using var pdf = await host.SendAsync(HttpMethod.Get, "/api/v1/reading/assets/" + second + "/source");
            var kept = (await pdf.Content.ReadFromJsonAsync<ReadingSourceV1>())!;
            Assert.AreEqual(ReadingFormatType.Pdf, kept.ReadingFormatTypeId);
            Assert.AreEqual(ReadingDerivationStatus.Declined, kept.ReadingDerivationStatusTypeId);
            Assert.AreEqual("no_text", kept.Reason);
            CollectionAssert.AreEqual(scanned, File.ReadAllBytes(Path.Combine(storage.Path, "1", "Books", "Second Book", "Second Book.pdf")));

            // With the Ocr module on, the same job queues the scan for the recognizer without anybody opening it.
            Assert.AreEqual(0L, await host.ScalarAsync("SELECT count(*) FROM \"ReadingRecognitions\""));
            await host.Fixture.RunSqlAsync("UPDATE \"InstanceModules\" SET \"IsEnabled\" = TRUE WHERE \"Key\" = 'Ocr'");
            await host.Fixture.RunSqlAsync("DELETE FROM \"ReadingDerivations\"");
            await using (var scope = host.Services.CreateAsyncScope())
            {
                Assert.IsTrue(await Derive(scope));
            }

            Assert.AreEqual(1L, await host.ScalarAsync("SELECT count(*) FROM \"ReadingRecognitions\" WHERE \"FinishedAt\" IS NULL AND \"FailedAt\" IS NULL"));
        }

        private static async Task<bool> Derive(AsyncServiceScope scope) =>
            (await scope.ServiceProvider.GetRequiredService<DeriveReadingEbooksV1>().ExecuteAsync<UpdateResult>(new ReadingDerivationParametersV1(), default, CancellationToken.None)).Success;

        // The book's PDF on its storage and in its library; the book's EPUB stays or goes.
        private static async Task PutPdfAsync(AuthenticationTests.Host host, string storage, long asset, byte[] pdf, bool ownEpub)
        {
            var title = (string)(await host.ScalarAsync("SELECT work.\"CanonicalTitle\" FROM \"MediaAssets\" AS asset JOIN \"Works\" AS work ON work.\"Id\" = asset.\"WorkId\" WHERE asset.\"Id\" = " + asset))!;
            File.WriteAllBytes(Path.Combine(storage, "1", "Books", title, title + ".pdf"), pdf);
            if (!ownEpub)
            {
                await host.Fixture.RunSqlAsync("UPDATE \"StoredFiles\" SET \"MissingSince\" = now() WHERE \"MediaAssetId\" = " + asset);
            }

            await host.Fixture.RunSqlAsync("INSERT INTO \"StoredFiles\" (\"LibraryRootId\", \"RelativePath\", \"SizeBytes\", \"ModifiedAt\", \"LastSeenAt\", \"MediaAssetId\") "
                + "SELECT \"LibraryRootId\", '" + title + "/" + title + ".pdf', " + pdf.Length.ToString(CultureInfo.InvariantCulture) + ", now(), now(), \"MediaAssetId\" FROM \"StoredFiles\" WHERE \"MediaAssetId\" = " + asset + " LIMIT 1");
        }

        // Four pages of book text with a picture of its own page in the middle.
        private static byte[] DigitalPdf()
        {
            var builder = new PdfDocumentBuilder();
            var font = builder.AddStandard14Font(Standard14Font.Helvetica);
            foreach (var number in new[] { 1, 2, 3, 4, 5 })
            {
                var page = builder.AddPage(PageSize.A4);
                if (number == 3)
                {
                    page.AddPng(ReadingTests.Picture(120, 80), new PdfRectangle(40, 100, 555, 740));
                    continue;
                }

                for (var line = 0; line < 40; line++)
                {
                    page.AddText($"page {number} line {line} of the book text runs on across the whole width of the page like prose does", 9, new PdfPoint(54, 740 - line * 14), font);
                }
            }

            return builder.Build();
        }
    }
}
