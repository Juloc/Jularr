using System.Net;
using System.Net.Http.Json;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Reading;
using Jularr.Backend.Data.System.Reading.V1;
using Jularr.Backend.Data.User.Reading.V1;
using Jularr.Backend.Service.Core;
using Jularr.Backend.Service.System.Reading.V1;
using Jularr.Integrations.Logic.Ocr;
using Microsoft.Extensions.DependencyInjection;
using SkiaSharp;
using TemporaryDirectory = Jularr.Backend.Tests.LibraryTests.TemporaryDirectory;

namespace Jularr.Backend.Tests
{
    [TestClass]
    public sealed class OcrTests
    {
        private static readonly string[] Sentences =
        [
            "It is a truth universally acknowledged, that a single man",
            "in possession of a good fortune, must be in want of a wife.",
            "However little known the feelings or views of such a man"
        ];

        // The real PP-OCRv5 models are large downloads; with JULARR_OCR_MODELS naming their folder, a typeset page is read back.
        [TestMethod]
        public void Engine_ReadsTheLinesOfATypesetPageInOrder()
        {
            var folder = Environment.GetEnvironmentVariable("JULARR_OCR_MODELS");
            if (folder is null)
            {
                Assert.Inconclusive("JULARR_OCR_MODELS names no folder with the PP-OCRv5 models.");
            }

            using var engine = new OcrEngine(Path.Combine(folder, "ch_PP-OCRv5_det_mobile.onnx"), Path.Combine(folder, "latin_PP-OCRv5_rec_mobile.onnx"));
            var started = DateTime.UtcNow;
            var page = engine.Read(TypesetPage(Sentences));
            Console.WriteLine((DateTime.UtcNow - started).TotalMilliseconds + " ms");
            foreach (var line in page.Lines)
            {
                Console.WriteLine(line.Confidence.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + " " + line.Text);
            }

            Assert.AreEqual(Sentences.Length, page.Lines.Count);
            for (var index = 0; index < Sentences.Length; index++)
            {
                Assert.AreEqual(Sentences[index], page.Lines[index].Text);
                Assert.IsTrue(page.Lines[index].Confidence > 0.8);
            }

            // A scanned PDF of such pages reflows like text: the sentence open at a page's end goes on on the next.
            var book = Jularr.Backend.Logic.Reading.PdfBook.Read(new MemoryStream(ScannedPdf(pages: 2)), engine.Read);
            var paragraphs = book.Chapters.SelectMany(chapter => chapter.Blocks).Select(block => block.Text).ToArray();
            CollectionAssert.AreEqual(new[] { string.Join(' ', Sentences.Concat(Sentences)) }, paragraphs);
            Assert.AreEqual(0, book.Images.Count);
        }

        // A scan waits while the Ocr module recognizes it in the background; then it reads like any converted PDF, its page pictures gone.
        [TestMethod]
        public async Task ScannedPdf_WaitsForRecognition_ThenReadsAsText()
        {
            using var storage = new TemporaryDirectory();
            var recognizer = new FakeRecognizer();
            await using var host = await LibraryTests.CreateHostAsync(storage.Path, razor: true, services: services => services.AddSingleton<IPageRecognizer>(recognizer));
            var (_, second) = await ReadingTests.CreateBooksAsync(host, storage.Path, "First Book", "Second Book");
            var pdf = ScannedPdf(pages: 2);
            File.WriteAllBytes(Path.Combine(storage.Path, "1", "Books", "Second Book", "Second Book.pdf"), pdf);
            await host.Fixture.RunSqlAsync("UPDATE \"StoredFiles\" SET \"MissingSince\" = now() WHERE \"MediaAssetId\" = " + second);
            await host.Fixture.RunSqlAsync("INSERT INTO \"StoredFiles\" (\"LibraryRootId\", \"RelativePath\", \"SizeBytes\", \"ModifiedAt\", \"LastSeenAt\", \"MediaAssetId\") "
                + "SELECT \"LibraryRootId\", 'Second Book/Second Book.pdf', " + pdf.Length + ", now(), now(), \"MediaAssetId\" FROM \"StoredFiles\" WHERE \"MediaAssetId\" = " + second);
            var open = "/api/v1/reading/assets/" + second + "/open";

            // Without the module a scan stays a PDF; with it, it waits and opens as a PDF meanwhile.
            using var off = await host.SendAsync(HttpMethod.Post, open);
            StringAssert.Contains(await off.Content.ReadAsStringAsync(), "reading_pdf_without_text");
            await host.Fixture.RunSqlAsync("UPDATE \"InstanceModules\" SET \"IsEnabled\" = TRUE WHERE \"Key\" = 'Ocr'");
            using var waiting = await host.SendAsync(HttpMethod.Post, open);
            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, waiting.StatusCode);
            StringAssert.Contains(await waiting.Content.ReadAsStringAsync(), "reading_pdf_recognizing");
            using var page = await host.SendAsync(HttpMethod.Get, "/Read/" + second);
            var reader = await page.Content.ReadAsStringAsync();
            StringAssert.Contains(reader, "The scanned pages are being turned into text");
            StringAssert.Contains(reader, "<iframe class=\"reader__pdf\"");

            // The worker reads every page once; then the queue is empty.
            await using (var scope = host.Services.CreateAsyncScope())
            {
                var recognize = scope.ServiceProvider.GetRequiredService<RecognizeReadingV1>();
                Assert.IsTrue((await recognize.ExecuteAsync<UpdateResult>(new ReadingRecognitionParametersV1(), default, CancellationToken.None)).Success);
                Assert.IsFalse((await recognize.ExecuteAsync<UpdateResult>(new ReadingRecognitionParametersV1(), default, CancellationToken.None)).Success);
            }

            Assert.AreEqual(2, recognizer.Pages);
            var document = await ReadingTests.OpenAsync(host, second);
            Assert.AreEqual(ReadingFormatType.Pdf, document.ReadingFormatTypeId);
            using var section = await host.SendAsync(HttpMethod.Get, "/api/v1/reading/documents/" + document.DocumentId + "/sections/0");
            var html = (await section.Content.ReadFromJsonAsync<ReadingSectionV1>())!.Html;
            StringAssert.Contains(html, string.Join(' ', Sentences));
            Assert.IsFalse(html.Contains("<img", StringComparison.Ordinal), html);
        }

        // Pages that are only a picture each, as a scanner writes them.
        private static byte[] ScannedPdf(int pages)
        {
            var builder = new UglyToad.PdfPig.Writer.PdfDocumentBuilder();
            var scan = TypesetPage(Sentences);
            for (var index = 0; index < pages; index++)
            {
                builder.AddPage(UglyToad.PdfPig.Content.PageSize.A4).AddPng(scan, new UglyToad.PdfPig.Core.PdfRectangle(0, 0, 595, 842));
            }

            return builder.Build();
        }

        // Each page reads as a chapter number and one paragraph in the picture's pixels.
        private sealed class FakeRecognizer : IPageRecognizer
        {
            public int Pages;

            public Task<Func<byte[], OcrPage>> RecognizerAsync(string? language, CancellationToken ct) => Task.FromResult<Func<byte[], OcrPage>>(_ =>
            {
                var number = Interlocked.Increment(ref Pages);
                return new OcrPage(1240, 1754, [new OcrLine(120, 120, 150, 160, number.ToString(System.Globalization.CultureInfo.InvariantCulture), 0.99f),
                    .. Sentences.Select((text, index) => new OcrLine(120, 260 + index * 48, 1100, 296 + index * 48, text, 0.95f)),
                    new OcrLine(120, 500, 1100, 536, "a line too uncertain to keep", 0.2f)]);
            });
        }

        // A scanned page: black lines of text on white at about 150 dpi, as PNG.
        internal static byte[] TypesetPage(IReadOnlyList<string> lines, int width = 1240, int height = 1754)
        {
            using var bitmap = new SKBitmap(width, height);
            using (var canvas = new SKCanvas(bitmap))
            {
                canvas.Clear(SKColors.White);
                using var font = new SKFont(SKTypeface.FromFamilyName("Times New Roman") ?? SKTypeface.Default, 30);
                using var paint = new SKPaint { Color = SKColors.Black, IsAntialias = true };
                for (var index = 0; index < lines.Count; index++)
                {
                    canvas.DrawText(lines[index], 120, 200 + index * 48, SKTextAlign.Left, font, paint);
                }
            }

            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            return data.ToArray();
        }
    }
}
