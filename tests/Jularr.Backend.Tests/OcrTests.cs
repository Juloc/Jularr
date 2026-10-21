using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Reading;
using Jularr.Backend.Data.System.Reading.V1;
using Jularr.Backend.Data.User.Reading.V1;
using Jularr.Backend.Logic.Reading;
using Jularr.Backend.Service.Core;
using Jularr.Backend.Service.System.Reading.V1;
using Jularr.Integrations.Logic.Ocr;
using Microsoft.Extensions.DependencyInjection;
using SkiaSharp;
using UglyToad.PdfPig;
using Host = Jularr.Backend.Tests.AuthenticationTests.Host;
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
            Assert.AreEqual(Sentences.Length, page.Lines.Count);
            for (var index = 0; index < Sentences.Length; index++)
            {
                Assert.AreEqual(Sentences[index], page.Lines[index].Text);
                Assert.IsTrue(page.Lines[index].Confidence > 0.8);
            }

            // A page the PDF shows turned is read upright: the same lines, found in the turned picture.
            var turned = engine.Read(TypesetPage(Sentences), 90);
            Assert.AreEqual(page.Height, turned.Width);
            Assert.AreEqual(page.Width, turned.Height);

            // A scanned PDF of such pages reflows like text: the sentence open at a page's end goes on on the next.
            var pdf = ScannedPdf(pages: 2);
            var recognized = new Dictionary<int, RecognizedPage>();
            using (var document = PdfDocument.Open(pdf))
            {
                foreach (var scanned in document.GetPages())
                {
                    recognized[scanned.Number] = PdfScanPages.Read(scanned, engine.Read);
                }
            }

            var book = PdfBook.Read(new MemoryStream(pdf), recognized);
            var paragraphs = book.Chapters.SelectMany(chapter => chapter.Blocks).Select(block => block.Text).ToArray();
            CollectionAssert.AreEqual(new[] { string.Join(' ', Sentences.Concat(Sentences)) }, paragraphs);
            Assert.AreEqual(0, book.Images.Count);
        }

        // A scan waits while the Ocr module recognizes it in the background; the original pages are there meanwhile, then it reads like any converted PDF, its page pictures gone.
        [TestMethod]
        public async Task ScannedPdf_WaitsForRecognition_ThenReadsAsText()
        {
            using var storage = new TemporaryDirectory();
            var recognizer = new FakeRecognizer();
            await using var host = await LibraryTests.CreateHostAsync(storage.Path, razor: true, services: services => services.AddSingleton<IPageRecognizer>(recognizer));
            var second = await AddScanAsync(host, storage.Path, ScannedPdf(pages: 2));
            var open = "/api/v1/reading/assets/" + second + "/open";

            // Without the module a scan stays a PDF; with it, it waits and opens as its original pages meanwhile.
            using var off = await host.SendAsync(HttpMethod.Post, open);
            StringAssert.Contains(await off.Content.ReadAsStringAsync(), "reading_pdf_without_text");
            await host.Fixture.RunSqlAsync("UPDATE \"InstanceModules\" SET \"IsEnabled\" = TRUE WHERE \"Key\" = 'Ocr'");
            using var waiting = await host.SendAsync(HttpMethod.Post, open);
            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, waiting.StatusCode);
            StringAssert.Contains(await waiting.Content.ReadAsStringAsync(), "reading_pdf_recognizing");
            using var page = await host.SendAsync(HttpMethod.Get, "/Read/" + second);
            var reader = await page.Content.ReadAsStringAsync();
            StringAssert.Contains(reader, "The scanned pages are being turned into text");
            StringAssert.Contains(reader, "data-format=\"pages\"");

            // The original works without any analysis; the state shows the scan being read.
            var before = await StateAsync(host, second);
            Assert.IsTrue(before.HasPdf);
            Assert.IsNull(before.RecommendedView);
            Assert.IsTrue(before.IsRecognizing);

            // The worker reads every page once; then the queue is empty and the text is prepared.
            await RecognizeAsync(host, true);
            await RecognizeAsync(host, false);
            Assert.AreEqual(2, recognizer.Pages);
            var document = await ReadingTests.OpenAsync(host, second);
            Assert.AreEqual(ReadingFormatType.Pdf, document.ReadingFormatTypeId);
            Assert.IsTrue(document.IsRecommended);
            using var section = await host.SendAsync(HttpMethod.Get, "/api/v1/reading/documents/" + document.DocumentId + "/sections/0");
            var html = (await section.Content.ReadFromJsonAsync<ReadingSectionV1>())!.Html;
            StringAssert.Contains(html, string.Join(' ', Sentences));
            Assert.IsFalse(html.Contains("<img", StringComparison.Ordinal), html);

            // The reading text knows where each paragraph came from; a better view is now recommended.
            StringAssert.Contains(html, "data-pdf-page=\"1\"");
            var after = await StateAsync(host, second);
            Assert.AreEqual(ReadingView.Smart, after.RecommendedView);
            Assert.IsFalse(after.IsRecognizing);
        }

        // A long scan is read a few pages at a time, the page a reader has open first; an interrupted scan goes on where it stopped.
        [TestMethod]
        public async Task Recognition_ReadsInSlices_OpenPageFirst_AndResumes()
        {
            using var storage = new TemporaryDirectory();
            var recognizer = new FakeRecognizer();
            await using var host = await LibraryTests.CreateHostAsync(storage.Path, razor: true, services: services => services.AddSingleton<IPageRecognizer>(recognizer));
            var asset = await AddScanAsync(host, storage.Path, ScannedPdf(pages: 20));
            await host.Fixture.RunSqlAsync("UPDATE \"InstanceModules\" SET \"IsEnabled\" = TRUE WHERE \"Key\" = 'Ocr'");
            using var waiting = await host.SendAsync(HttpMethod.Post, "/api/v1/reading/assets/" + asset + "/open");
            StringAssert.Contains(await waiting.Content.ReadAsStringAsync(), "reading_pdf_recognizing");

            // The profile reads page 15 in the original pages: the next slice starts there and wraps around.
            using var saved = await host.SendAsync(HttpMethod.Put, "/api/v1/reading/assets/" + asset + "/progress", new SaveReadingProgressDataV1("p15", 0.75m, false));
            Assert.AreEqual(HttpStatusCode.OK, saved.StatusCode, await saved.Content.ReadAsStringAsync());
            Assert.IsTrue(await RecognizeAsync(host, true));
            CollectionAssert.AreEqual(new[] { 1, 2, 15, 16, 17, 18, 19, 20 }, await ReadPagesAsync(host));
            var halfway = await StateAsync(host, asset);
            Assert.AreEqual(20, halfway.RecognitionPages);
            Assert.AreEqual(8, halfway.RecognizedPages);
            Assert.IsTrue(halfway.IsRecognizing);

            // Pages already read are not read again; a changed file starts over.
            Assert.IsTrue(await RecognizeAsync(host, true));
            Assert.AreEqual(16, recognizer.Pages);
            CollectionAssert.AreEqual(Enumerable.Range(1, 20).Where(page => page is <= 10 or >= 15).ToArray(), await ReadPagesAsync(host));
            Assert.IsTrue(await RecognizeAsync(host, true));
            Assert.AreEqual(20, recognizer.Pages);
            Assert.IsFalse(await RecognizeAsync(host, false));
            Assert.IsNotNull(await host.ScalarAsync("SELECT \"FinishedAt\" FROM \"ReadingRecognitions\""));
            Assert.AreEqual(ReadingView.Smart, (await StateAsync(host, asset)).RecommendedView);

            await host.Fixture.RunSqlAsync("UPDATE \"StoredFiles\" SET \"ModifiedAt\" = \"ModifiedAt\" + interval '1 minute' WHERE \"RelativePath\" LIKE '%.pdf'");
            using var again = await host.SendAsync(HttpMethod.Post, "/api/v1/reading/assets/" + asset + "/open");
            StringAssert.Contains(await again.Content.ReadAsStringAsync(), "reading_pdf_recognizing");
            Assert.AreEqual(0L, await host.ScalarAsync("SELECT count(*) FROM \"ReadingRecognizedPages\""));
        }

        // Noise a recognizer makes of a picture is not shown as lines: that page stays a whole page picture in the text, the other pages read as text.
        [TestMethod]
        public async Task Recognition_NoiseOfAnIllustration_BecomesTheOriginalPage()
        {
            using var storage = new TemporaryDirectory();
            var recognizer = new FakeRecognizer { NoisyPages = [2] };
            await using var host = await LibraryTests.CreateHostAsync(storage.Path, razor: true, services: services => services.AddSingleton<IPageRecognizer>(recognizer));
            var asset = await AddScanAsync(host, storage.Path, ScannedPdf(pages: 3));
            await host.Fixture.RunSqlAsync("UPDATE \"InstanceModules\" SET \"IsEnabled\" = TRUE WHERE \"Key\" = 'Ocr'");
            await host.SendAsync(HttpMethod.Post, "/api/v1/reading/assets/" + asset + "/open");
            await RecognizeAsync(host, true);

            var document = await ReadingTests.OpenAsync(host, asset);
            Assert.IsTrue(document.IsRecommended);
            Assert.AreEqual(0, document.OriginalPages);
            var html = new System.Text.StringBuilder();
            for (var index = 0; index < document.SectionCount; index++)
            {
                using var section = await host.SendAsync(HttpMethod.Get, "/api/v1/reading/documents/" + document.DocumentId + "/sections/" + index);
                html.Append((await section.Content.ReadFromJsonAsync<ReadingSectionV1>())!.Html);
            }

            StringAssert.Contains(html.ToString(), "data-figure=\"page\" data-pdf-page=\"2\"");
            Assert.IsFalse(html.ToString().Contains("zq", StringComparison.Ordinal), html.ToString());
            StringAssert.Contains(html.ToString(), Sentences[0]);
        }

        [TestMethod]
        [DataRow(20, null, new[] { 1, 2, 3 }, 4, 17)]
        [DataRow(20, 15, new[] { 15, 16 }, 17, 18)]
        [DataRow(5, 9, new int[0], 5, 5)]
        public void Plan_ReadsTheOpenPageFirstThenWrapsAround(int pages, int? wanted, int[] done, int first, int count)
        {
            var remaining = ReadingRecognitionPlan.Remaining(pages, wanted, done.ToHashSet()).ToArray();
            Assert.AreEqual(count, remaining.Length);
            Assert.AreEqual(first, remaining[0]);
            Assert.AreEqual(remaining.Length, remaining.Distinct().Count());
        }

        [TestMethod]
        [DataRow(2, 2)]
        [DataRow(30, 30)]
        [DataRow(300, 60)]
        public void Plan_RestsAsLongAsItWorkedButNeverMoreThanAMinute(int seconds, int expected)
        {
            Assert.AreEqual(TimeSpan.FromSeconds(expected), ReadingRecognitionPlan.Rest(TimeSpan.FromSeconds(seconds)));
        }

        private static async Task<long> AddScanAsync(Host host, string storage, byte[] pdf)
        {
            var (_, second) = await ReadingTests.CreateBooksAsync(host, storage, "First Book", "Second Book");
            File.WriteAllBytes(Path.Combine(storage, "1", "Books", "Second Book", "Second Book.pdf"), pdf);
            await host.Fixture.RunSqlAsync("UPDATE \"StoredFiles\" SET \"MissingSince\" = now() WHERE \"MediaAssetId\" = " + second);
            await host.Fixture.RunSqlAsync("INSERT INTO \"StoredFiles\" (\"LibraryRootId\", \"RelativePath\", \"SizeBytes\", \"ModifiedAt\", \"LastSeenAt\", \"MediaAssetId\") "
                + "SELECT \"LibraryRootId\", 'Second Book/Second Book.pdf', " + pdf.Length + ", now(), now(), \"MediaAssetId\" FROM \"StoredFiles\" WHERE \"MediaAssetId\" = " + second);
            return second;
        }

        private static async Task<bool> RecognizeAsync(Host host, bool expected)
        {
            await using var scope = host.Services.CreateAsyncScope();
            var worked = (await scope.ServiceProvider.GetRequiredService<RecognizeReadingV1>().ExecuteAsync<UpdateResult>(new ReadingRecognitionParametersV1(), default, CancellationToken.None)).Success;
            Assert.AreEqual(expected, worked);
            return worked;
        }

        private static async Task<ReadingStateV1> StateAsync(Host host, long asset)
        {
            using var response = await host.SendAsync(HttpMethod.Get, "/api/v1/reading/assets/" + asset + "/state");
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
            return (await response.Content.ReadFromJsonAsync<ReadingStateV1>())!;
        }

        private static async Task<int[]> ReadPagesAsync(Host host) =>
            [.. ((string?)await host.ScalarAsync("SELECT string_agg(\"PageNumber\"::text, ',' ORDER BY \"PageNumber\") FROM \"ReadingRecognizedPages\"") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse)];

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

        // Each page reads as a chapter number and one paragraph in the picture's pixels; a noisy page reads as the symbols a recognizer makes of a drawing.
        private sealed class FakeRecognizer : IPageRecognizer
        {
            public int Pages;
            public int[] NoisyPages { get; init; } = [];
            private int _calls;

            public Task<Func<byte[], int, OcrPage>> RecognizerAsync(string? language, CancellationToken ct) => Task.FromResult<Func<byte[], int, OcrPage>>((_, _) =>
            {
                var number = Interlocked.Increment(ref Pages);

                // Noisy pages are named by the order the pages are read in a book of three.
                var noisy = NoisyPages.Contains(Interlocked.Increment(ref _calls));
                if (noisy)
                {
                    return new OcrPage(1240, 1754, [.. Enumerable.Range(0, 12).Select(index => new OcrLine(120 + index * 80, 300 + index * 60, 190 + index * 80, 340 + index * 60, "zq ~ x_ | zqk", 0.9f))]);
                }

                return new OcrPage(1240, 1754, [new OcrLine(120, 120, 150, 160, number.ToString(CultureInfo.InvariantCulture), 0.99f),
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
