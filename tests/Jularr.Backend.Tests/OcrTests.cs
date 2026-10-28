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
using Jularr.Integrations.Logic.Pdf;
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

            // A scanned PDF of such pages, drawn by PDFium, reflows like text: the sentence open at a page's end goes on on the next.
            var pdf = ScannedPdf(pages: 2);
            var recognized = new Dictionary<int, RecognizedPage>();
            using (var rendering = PdfRendering.Open(pdf))
            using (var document = PdfDocument.Open(pdf))
            {
                foreach (var scanned in document.GetPages())
                {
                    recognized[scanned.Number] = PdfScanPages.Read(scanned, rendering, engine.Read);
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

        // The text of a scanned page is erased from a copy of it; what is left in a large connected area is its picture, and short text in the picture's rows is
        // its label, not running text. A caption below it stays text.
        [TestMethod]
        public void ScanAnalysis_FindsThePicture_AndItsLabels()
        {
            using var page = IllustratedPage();
            var lines = PdfScanAnalysis.Analyse(page, Read(page), []);

            var picture = lines.Single(line => line.Kind == RecognizedLine.Picture);
            Assert.AreEqual(0.24, picture.Left, 0.03);
            Assert.AreEqual(0.40, picture.Top, 0.03);
            Assert.AreEqual(0.77, picture.Right, 0.03);
            Assert.AreEqual(0.63, picture.Bottom, 0.03);
            Assert.AreEqual(RecognizedLine.Label, lines.Single(line => line.Text == "ALWAYS").Kind);
            Assert.IsNull(lines.Single(line => line.Text == "He shouted.").Kind);
            Assert.IsTrue(lines.Where(line => Sentences.Contains(line.Text)).All(line => line.Kind is null));
        }

        // Where the recognizer is unsure of a word, the scan's own recognition layer at the same place wins; quotes it dropped come back from there; straight
        // quotes become typographic ones.
        [TestMethod]
        public void ScanAnalysis_TakesTheLayersWord_WhereTheRecognizerWasUnsure()
        {
            using var page = new SKBitmap(1000, 1000);
            page.Erase(SKColors.White);
            OcrCharacter[] characters = [.. "1ne heavier Drinking it's".Select((character, index) =>
                new OcrCharacter(character.ToString(), 100 + index * 20, index < 3 ? 0.4f : 0.99f))];
            var read = new OcrPage(1000, 1000, [new OcrLine(90, 100, 600, 130, "1ne heavier Drinking it's", 0.9f) { Characters = characters }]);
            PdfLayerWord[] layer = [new("The", 0.09, 0.1, 0.15, 0.13), new("“Drinking”", 0.32, 0.1, 0.5, 0.13)];

            var line = PdfScanAnalysis.Analyse(page, read, layer).Single();
            Assert.AreEqual("The heavier “Drinking” it’s", line.Text);

            // An opening quote read apart from its word joins it; a closing quote read twice is one.
            OcrCharacter[] caption = [.. "\" A horse.\"\"".Select((character, index) => new OcrCharacter(character.ToString(), 100 + index * 20, 0.99f))];
            var captioned = new OcrPage(1000, 1000, [new OcrLine(90, 300, 400, 330, "\" A horse.\"\"", 0.9f) { Characters = caption }]);
            Assert.AreEqual("“A horse.”", PdfScanAnalysis.Analyse(page, captioned, []).Single().Text);

            // A double quote that the recognizer closed with a single one closes as double.
            OcrCharacter[] shouted = [.. "\"Found it!' he shouted.".Select((character, index) => new OcrCharacter(character.ToString(), 100 + index * 20, 0.99f))];
            var found = new OcrPage(1000, 1000, [new OcrLine(90, 500, 600, 530, "\"Found it!' he shouted.", 0.9f) { Characters = shouted }]);
            Assert.AreEqual("“Found it!” he shouted.", PdfScanAnalysis.Analyse(page, found, []).Single().Text);
        }

        // A scan's picture is cut out of the page PDFium draws and stands in the text with its caption; the page's text reads as text. The first and last
        // pages are the covers and show whole.
        [TestMethod]
        public void ScannedPage_WithAPicture_ReadsAsTextWithThePictureAndItsCaption()
        {
            byte[] pdf;
            using (var page = IllustratedPage())
            using (var image = SKImage.FromBitmap(page))
            using (var png = image.Encode(SKEncodedImageFormat.Png, 100))
            {
                var builder = new UglyToad.PdfPig.Writer.PdfDocumentBuilder();
                foreach (var _ in Enumerable.Range(0, 4))
                {
                    builder.AddPage(UglyToad.PdfPig.Content.PageSize.A4).AddPng(png.ToArray(), new UglyToad.PdfPig.Core.PdfRectangle(0, 0, 595, 842));
                }

                pdf = builder.Build();
            }

            using var rendering = PdfRendering.Open(pdf);
            var recognized = new Dictionary<int, RecognizedPage>();
            using (var document = PdfDocument.Open(pdf))
            {
                foreach (var scanned in document.GetPages())
                {
                    recognized[scanned.Number] = PdfScanPages.Read(scanned, rendering, Read);
                    Assert.AreEqual(RecognizedPage.Recognized, recognized[scanned.Number].Reason);
                }
            }

            var book = PdfBook.Read(new MemoryStream(pdf), recognized, rendering);
            Assert.IsFalse(book.WantsRecognition);
            var blocks = book.Chapters.SelectMany(chapter => chapter.Blocks).ToArray();
            var figures = blocks.Where(block => block.Image is not null).ToArray();
            CollectionAssert.AreEqual(new[] { 1, 2, 3, 4 }, figures.Select(figure => figure.Page).ToArray());
            Assert.IsTrue(figures.Where(figure => figure.Page is 1 or 4).All(cover => cover.Figure == ReadingHtml.PageFigure && cover.Caption is null));
            Assert.IsTrue(figures.Where(figure => figure.Page is 2 or 3).All(figure => figure.Caption == "He shouted." && figure.Figure == ReadingHtml.WideFigure));
            Assert.IsTrue(book.Images.All(picture => picture.MediaType == "image/jpeg" && SKBitmap.Decode(picture.Bytes) is { Width: > 300 }));
            Assert.IsFalse(blocks.Any(block => block.Text?.Contains("ALWAYS", StringComparison.Ordinal) == true || block.Text?.Contains("He shouted", StringComparison.Ordinal) == true));
            StringAssert.Contains(blocks.First(block => block.Text is not null).Text, Sentences[0]);
        }

        // Words of large type detected apart are read as their line; a narrow column beside large type, and columns of text, stay apart.
        [TestMethod]
        public void Recognizer_JoinsTheWordsOfALine_ButNotTheColumns()
        {
            static SKRectI Box(int left, int top, int right, int bottom) => new(left, top, right, bottom);
            var joined = OcrEngine.Join([Box(400, 200, 700, 320), Box(740, 205, 1000, 318), Box(1030, 198, 1300, 322)]);
            CollectionAssert.AreEqual(new[] { Box(400, 198, 1300, 322) }, joined.ToArray(), "the words of a large heading");
            Assert.AreEqual(2, OcrEngine.Join([Box(60, 230, 300, 270), Box(400, 200, 900, 330)]).Count, "a small line beside large type");
            Assert.AreEqual(2, OcrEngine.Join([Box(60, 300, 500, 330), Box(560, 302, 1000, 331)]).Count, "two columns of text");
            Assert.AreEqual(1, OcrEngine.Join([Box(60, 300, 100, 320), Box(110, 296, 300, 330), Box(310, 300, 350, 320)]).Count, "a short word beside one with a descender");
        }

        // A page drawn by PDFium keeps its colours: an orange box stays orange, not blue.
        [TestMethod]
        public void Rendering_KeepsTheColoursOfThePage()
        {
            using var orange = new SKBitmap(200, 200);
            orange.Erase(new SKColor(230, 110, 20));
            using var image = SKImage.FromBitmap(orange);
            using var png = image.Encode(SKEncodedImageFormat.Png, 100);
            var builder = new UglyToad.PdfPig.Writer.PdfDocumentBuilder();
            builder.AddPage(UglyToad.PdfPig.Content.PageSize.A4).AddPng(png.ToArray(), new UglyToad.PdfPig.Core.PdfRectangle(0, 0, 595, 842));
            using var rendering = PdfRendering.Open(builder.Build());
            using var drawn = rendering.Render(1, 400);
            var pixel = drawn.GetPixel(drawn.Width / 2, drawn.Height / 2);
            Assert.IsTrue(pixel.Red > 200 && pixel.Blue < 60, pixel.ToString());
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
        [DataRow(2, false, 2)]
        [DataRow(2, true, 8)]
        [DataRow(30, true, 60)]
        public void Plan_RestsAsLongAsItWorkedAndLongerWhileInUse(int seconds, bool used, int expected)
        {
            Assert.AreEqual(TimeSpan.FromSeconds(expected), ReadingRecognitionPlan.Rest(TimeSpan.FromSeconds(seconds), used));
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

        // A page of a book: three lines of text, a drawing with a label in its rows and a caption below it.
        private static SKBitmap IllustratedPage()
        {
            var bitmap = new SKBitmap(1240, 1754);
            using var canvas = new SKCanvas(bitmap);
            canvas.Clear(new SKColor(245, 238, 222));
            using var font = new SKFont(SKTypeface.FromFamilyName("Times New Roman") ?? SKTypeface.Default, 30);
            using var ink = new SKPaint { Color = new SKColor(30, 28, 28), IsAntialias = true, StrokeWidth = 4 };
            for (var index = 0; index < Sentences.Length; index++)
            {
                canvas.DrawText(Sentences[index], 120, 240 + index * 48, SKTextAlign.Left, font, ink);
            }

            // The drawing: hatching and a frame from 300 to 900 across and 700 to 1100 down.
            for (var x = 300; x <= 900; x += 14)
            {
                canvas.DrawLine(x, 700, x + 60, 1100, ink);
            }

            canvas.DrawRect(300, 700, 600, 400, new SKPaint { Color = ink.Color, Style = SKPaintStyle.Stroke, StrokeWidth = 6 });
            canvas.DrawText("ALWAYS", 340, 1090, SKTextAlign.Left, font, new SKPaint { Color = new SKColor(30, 20, 140), IsAntialias = true });
            canvas.DrawText("He shouted.", 520, 1170, SKTextAlign.Left, font, ink);
            return bitmap;
        }

        // What a recognizer reads on the illustrated page, in the pixels of the picture it is given.
        private static OcrPage Read(SKBitmap page)
        {
            var (x, y) = (page.Width / 1240.0, page.Height / 1754.0);
            OcrLine Line(int left, int top, int right, int bottom, string text) => new((int)(left * x), (int)(top * y), (int)(right * x), (int)(bottom * y), text, 0.95f);
            return new OcrPage(page.Width, page.Height, [.. Sentences.Select((text, index) => Line(110, 210 + index * 48, 1110, 250 + index * 48, text)),
                Line(330, 1062, 470, 1098, "ALWAYS"), Line(510, 1142, 690, 1178, "He shouted.")]);
        }

        // Each page reads as a chapter number and one paragraph in the picture's pixels; a noisy page reads as the symbols a recognizer makes of a drawing.
        private sealed class FakeRecognizer : IPageRecognizer
        {
            public int Pages;
            public int[] NoisyPages { get; init; } = [];
            private int _calls;

            public Task<Func<SKBitmap, OcrPage>> RecognizerAsync(string? language, CancellationToken ct) => Task.FromResult<Func<SKBitmap, OcrPage>>(_ =>
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
