using System.Text;
using System.Text.RegularExpressions;
using Jularr.Backend.Logic.Reading;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace Jularr.Backend.Tests
{
    // The conversion of PDFs into reflowable books with figures, on small PDFs written here.
    [TestClass]
    public sealed partial class PdfBookTests
    {
        private const string Line = "the long line of ordinary book text runs on across the whole width of the page like prose does";

        [GeneratedRegex(@"<img[^>]*src=""([^""]+)""")]
        private static partial Regex Picture();

        [TestMethod]
        public void TextPages_ReflowWithoutFigures_AndAreFitForAReadingCopy()
        {
            var book = PdfBook.Read(new MemoryStream(Build(builder => Pages(builder, 3, page => BodyLines(builder, page, 40)))));

            Assert.IsTrue(book.Reading.IsReadable);
            Assert.IsTrue(book.Ebook.IsReadable, book.Ebook.Reason);
            Assert.IsTrue(book.Pages.All(page => page.Kind == PdfPageKind.Text));
            Assert.IsFalse(book.Chapters.SelectMany(chapter => chapter.Blocks).Any(block => block.Image is not null));
        }

        [TestMethod]
        public void PictureOnItsOwnPage_BecomesAWholePageFigure_InTextOrder()
        {
            var png = ReadingTests.Picture(120, 80);
            var book = PdfBook.Read(new MemoryStream(Build(builder =>
            {
                var first = builder.AddPage(PageSize.A4);
                Body(builder, first, "First page marker.", 30);
                var plate = builder.AddPage(PageSize.A4);
                plate.AddPng(png, new PdfRectangle(40, 100, 555, 740));
                var last = builder.AddPage(PageSize.A4);
                Body(builder, last, "Last page marker.", 30);
            })));

            var blocks = book.Chapters.SelectMany(chapter => chapter.Blocks).ToArray();
            var figure = blocks.Single(block => block.Image is not null);
            Assert.AreEqual(ReadingHtml.PageFigure, figure.Figure);
            Assert.IsTrue(blocks.ToList().IndexOf(figure) > blocks.ToList().FindIndex(block => block.Text?.Contains("First page marker") == true));
            Assert.IsTrue(blocks.ToList().IndexOf(figure) < blocks.ToList().FindIndex(block => block.Text?.Contains("Last page marker") == true));
            Assert.AreEqual(PdfPageKind.Figure, book.Pages[1].Kind);
            Assert.IsTrue(book.Ebook.IsReadable, book.Ebook.Reason);
        }

        [TestMethod]
        public void WidePicture_KeepsItsCaptionAndAnAlternativeText_AndASmallOneStaysInTheLine()
        {
            var png = ReadingTests.Picture(120, 80);
            var book = PdfBook.Read(new MemoryStream(Build(builder =>
            {
                var page = builder.AddPage(PageSize.A4);
                Body(builder, page, "Text above the figure.", 10, firstLine: 790);
                page.AddPng(png, new PdfRectangle(150, 350, 450, 500));
                var font = builder.AddStandard14Font(Standard14Font.Helvetica);
                page.AddText("Figure 1: The caption of the picture", 9, new PdfPoint(150, 335), font);
                page.AddText("continues on a second line.", 9, new PdfPoint(150, 323), font);
                page.AddPng(png, new PdfRectangle(54, 250, 114, 300));
                page.AddText("Text below the figures.", 9, new PdfPoint(54, 200), font);
                foreach (var next in Enumerable.Range(0, 3))
                {
                    Body(builder, builder.AddPage(PageSize.A4), "More text " + next + ".", 30);
                }
            })));

            var blocks = book.Chapters.SelectMany(chapter => chapter.Blocks).ToArray();
            var wide = blocks.Single(block => block.Figure == ReadingHtml.WideFigure);
            Assert.AreEqual("Figure 1: The caption of the picture continues on a second line.", wide.Caption);
            Assert.AreEqual(wide.Caption, wide.Alt);
            Assert.IsFalse(blocks.Any(block => block.Text?.Contains("The caption of the picture") == true || block.Text?.Contains("second line") == true));
            Assert.AreEqual(1, blocks.Count(block => block.Image is not null && block.Figure is null));
        }

        [TestMethod]
        public void DiagramDrawnWithPaths_BecomesAnSvgFigureThatKeepsItsLabels()
        {
            var book = PdfBook.Read(new MemoryStream(Build(builder =>
            {
                var page = builder.AddPage(PageSize.A4);
                var font = builder.AddStandard14Font(Standard14Font.Helvetica);
                Body(builder, page, "Text before the diagram.", 8, firstLine: 800);
                page.SetStrokeColor(30, 90, 200);
                page.DrawCircle(new PdfPoint(250, 500), 160, 2);
                page.DrawCircle(new PdfPoint(340, 500), 160, 2);
                page.DrawCircle(new PdfPoint(295, 420), 160, 2);
                page.DrawCircle(new PdfPoint(295, 580), 160, 2);
                page.DrawLine(new PdfPoint(100, 330), new PdfPoint(500, 330), 2);
                page.DrawRectangle(new PdfPoint(150, 300), 300, 20, 1);
                foreach (var (text, x, y) in new[] { ("What you love", 190, 500), ("What the world needs", 330, 500), ("Ikigai", 280, 460), ("What you are good at", 250, 400), ("What you can be paid for", 250, 600) })
                {
                    page.AddText(text, 9, new PdfPoint(x, y), font);
                }

                foreach (var next in Enumerable.Range(0, 3))
                {
                    Body(builder, builder.AddPage(PageSize.A4), "More text " + next + ".", 30);
                }
            })));

            var blocks = book.Chapters.SelectMany(chapter => chapter.Blocks).ToArray();
            var diagram = blocks.Single(block => block.Image is not null);
            Assert.AreEqual("pdf/images/00000.svg", diagram.Image);
            Assert.IsTrue(diagram.Figure is ReadingHtml.PageFigure or ReadingHtml.WideFigure);
            StringAssert.Contains(diagram.Alt, "Ikigai");
            var svg = Encoding.UTF8.GetString(book.Images.Single().Bytes);
            StringAssert.StartsWith(svg, "<svg ");
            StringAssert.Contains(svg, ">paid</text>");
            StringAssert.Contains(svg, ">Ikigai</text>");
            StringAssert.Contains(svg, "<path ");

            // The labels live in the figure, the text around it flows on.
            Assert.IsFalse(blocks.Any(block => block.Text?.Contains("What the world needs") == true));
            Assert.IsTrue(blocks.Any(block => block.Text?.Contains("Text before the diagram.") == true));
            Assert.AreEqual("drawing", book.Pages[0].Reason);
        }

        [TestMethod]
        public void ChapterNumberWithItsCentredTitle_StartsAChapterNamedByBoth()
        {
            var book = PdfBook.Read(new MemoryStream(Build(builder =>
            {
                var font = builder.AddStandard14Font(Standard14Font.Helvetica);
                Body(builder, builder.AddPage(PageSize.A4), "Opening text.", 30);
                foreach (var (number, title) in new[] { (1, "Un animal sin importancia"), (2, "El arbol del saber") })
                {
                    var page = builder.AddPage(PageSize.A4);
                    page.AddText("Capitolo " + number, 9, new PdfPoint(268, 780), font);
                    page.AddText(title, 9, new PdfPoint(250, 766), font);
                    for (var line = 0; line < 30; line++)
                    {
                        page.AddText(Line + " " + line, 9, new PdfPoint(54, 730 - line * 14), font);
                    }
                }
            })));

            CollectionAssert.AreEqual(new[] { "Capitolo 1. Un animal sin importancia", "Capitolo 2. El arbol del saber" }, book.Chapters.Where(chapter => chapter.Title is not null).Select(chapter => chapter.Title).ToArray());
            Assert.IsFalse(book.Chapters.SelectMany(chapter => chapter.Blocks).Any(block => block.Text?.Contains("animal sin") == true));
        }

        [TestMethod]
        public void TwoColumnPage_ReadsOneColumnAfterTheOther()
        {
            var book = PdfBook.Read(new MemoryStream(Build(builder =>
            {
                foreach (var _ in Enumerable.Range(0, 2))
                {
                    var page = builder.AddPage(PageSize.A4);
                    var font = builder.AddStandard14Font(Standard14Font.Helvetica);
                    for (var line = 1; line <= 40; line++)
                    {
                        var y = 730 - line * 14;
                        page.AddText($"left column line {line:00} with some words to fill the column.", 9, new PdfPoint(54, y), font);
                        page.AddText($"right column line {line:00} with some words to fill the column.", 9, new PdfPoint(330, y), font);
                    }
                }
            })));

            var text = string.Join(' ', book.Chapters.SelectMany(chapter => chapter.Blocks).Select(block => block.Text));
            Assert.IsTrue(text.IndexOf("left column line 40", StringComparison.Ordinal) < text.IndexOf("right column line 01", StringComparison.Ordinal), text[..300]);
            Assert.AreEqual(2, book.Pages[0].Columns);
            Assert.AreEqual(PdfPageKind.Text, book.Pages[0].Kind);
        }

        [TestMethod]
        public void TableWithRules_BecomesAFigureOfItsOwn()
        {
            var book = PdfBook.Read(new MemoryStream(Build(builder =>
            {
                var page = builder.AddPage(PageSize.A4);
                var font = builder.AddStandard14Font(Standard14Font.Helvetica);
                Body(builder, page, "Text above the table.", 8, firstLine: 800);
                for (var row = 0; row < 8; row++)
                {
                    var y = 560 - row * 20;
                    page.DrawLine(new PdfPoint(54, y + 14), new PdfPoint(540, y + 14), 0.5);
                    foreach (var (cell, x) in new[] { ("Item " + row, 60), ("Quantity", 200), ("Price", 320), ("Total " + row, 440) })
                    {
                        page.AddText(cell, 9, new PdfPoint(x, y), font);
                    }
                }

                foreach (var next in Enumerable.Range(0, 3))
                {
                    Body(builder, builder.AddPage(PageSize.A4), "More text " + next + ".", 30);
                }
            })));

            var blocks = book.Chapters.SelectMany(chapter => chapter.Blocks).ToArray();
            var table = blocks.Single(block => block.Image is not null);
            Assert.IsTrue(table.Image!.EndsWith(".svg", StringComparison.Ordinal));
            StringAssert.Contains(table.Alt, "Total 7");
            Assert.AreEqual("table", book.Pages[0].Reason);
        }

        [TestMethod]
        public void ScanWithAHiddenTextLayer_ReadsItsText_ButNoReadingCopyIsMade()
        {
            var png = ReadingTests.Picture(60, 80);
            var book = PdfBook.Read(new MemoryStream(Build(builder =>
            {
                foreach (var _ in Enumerable.Range(0, 4))
                {
                    var page = builder.AddPage(PageSize.A4);
                    page.AddPng(png, new PdfRectangle(0, 0, 595, 842));
                    page.SetTextRenderingMode(TextRenderingMode.Neither);
                    BodyLines(builder, page, 40);
                }
            })));

            Assert.IsTrue(book.Reading.IsReadable);
            Assert.IsFalse(book.Ebook.IsReadable);
            Assert.AreEqual("scan", book.Ebook.Reason);
            Assert.IsTrue(book.Pages.All(page => page.IsHidden && page.Reason == "scan-layer"));
        }

        [TestMethod]
        public void ScannedPageWithNoisyText_ShowsItsPicture_WhereThePictureCanBeRead()
        {
            var png = ReadingTests.Picture(60, 80);
            var book = PdfBook.Read(new MemoryStream(Build(builder =>
            {
                foreach (var number in Enumerable.Range(0, 5))
                {
                    var page = builder.AddPage(PageSize.A4);
                    page.AddPng(png, new PdfRectangle(0, 0, 595, 842));
                    page.SetTextRenderingMode(TextRenderingMode.Neither);
                    if (number == 2)
                    {
                        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
                        for (var line = 0; line < 8; line++)
                        {
                            page.AddText("xq zv kk jj fh ql wm tt bb cc dd ff gg hh", 9, new PdfPoint(54, 700 - line * 14), font);
                        }
                    }
                    else
                    {
                        BodyLines(builder, page, 40);
                    }
                }
            })));

            var blocks = book.Chapters.SelectMany(chapter => chapter.Blocks).ToArray();
            var plate = blocks.Single(block => block.Image is not null);
            Assert.AreEqual(ReadingHtml.PageFigure, plate.Figure);
            Assert.AreEqual(PdfPageKind.Figure, book.Pages[2].Kind);
            Assert.IsFalse(blocks.Any(block => block.Text?.Contains("xq zv") == true));
        }

        [TestMethod]
        public void ScannedPagesWithAPictureNobodyCanRead_StandInForTheOriginal_ExceptBlankOnes()
        {
            var text = Enumerable.Range(0, 30).Select(line => Line + " " + line).ToArray();
            var noise = Enumerable.Range(0, 8).Select(_ => "xq zv kk jj fh ql wm tt bb cc dd ff gg hh").ToArray();
            var pdf = HandPdf.ScannedPages([text, text, noise, [], text], 12000);
            var small = HandPdf.ScannedPages([text, [], text, text], 600);

            var book = PdfBook.Read(new MemoryStream(pdf));
            var blocks = book.Chapters.SelectMany(chapter => chapter.Blocks).ToArray();
            CollectionAssert.AreEqual(new[] { 3, 4 }, blocks.Where(block => block.Figure == ReadingHtml.OriginalFigure).Select(block => block.Page).ToArray());
            Assert.IsTrue(book.Reading.IsReadable);
            Assert.IsFalse(book.Ebook.IsReadable);
            Assert.AreEqual("original_pages", book.Ebook.Reason);
            Assert.AreEqual(5, book.Pages.Count(page => page.UnreadablePictures > 0));

            // A page whose picture is tiny is blank and leaves nothing behind.
            var blank = PdfBook.Read(new MemoryStream(small));
            Assert.AreEqual(PdfPageKind.Blank, blank.Pages[1].Kind);
            Assert.IsFalse(blank.Chapters.SelectMany(chapter => chapter.Blocks).Any(block => block.Figure == ReadingHtml.OriginalFigure));
        }

        [TestMethod]
        public void PagesWithoutAnyText_AreNoBook()
        {
            var png = ReadingTests.Picture(60, 80);
            var pdf = Build(builder =>
            {
                foreach (var _ in Enumerable.Range(0, 3))
                {
                    builder.AddPage(PageSize.A4).AddPng(png, new PdfRectangle(0, 0, 595, 842));
                }
            });

            Assert.ThrowsExactly<PdfWithoutTextException>(() => PdfBook.Read(new MemoryStream(pdf)));
        }

        [TestMethod]
        public void BrokenFile_IsInvalid()
        {
            Assert.ThrowsExactly<InvalidDataException>(() => PdfBook.Read(new MemoryStream(Encoding.ASCII.GetBytes("%PDF-1.4 this is not a pdf at all"))));
            Assert.ThrowsExactly<InvalidDataException>(() => PdfBook.Read(new MemoryStream([1, 2, 3, 4, 5, 6])));
        }

        [TestMethod]
        public void FiguresOfAPdf_AreSectionsWithZoomableFigureMarkup()
        {
            var png = ReadingTests.Picture(120, 80);
            var book = PdfBook.Read(new MemoryStream(Build(builder =>
            {
                Body(builder, builder.AddPage(PageSize.A4), "Text first.", 30);
                builder.AddPage(PageSize.A4).AddPng(png, new PdfRectangle(40, 100, 555, 740));
                Body(builder, builder.AddPage(PageSize.A4), "Text last.", 30);
            })));

            var prepared = PdfBookHtml.Prepare(book, 7);

            var html = string.Concat(prepared.Sections.Select(section => section.Html));
            StringAssert.Contains(html, "<figure data-figure=\"page\"");
            Assert.AreEqual(1, Picture().Matches(html).Count);
            StringAssert.Contains(html, "/api/v1/reading/documents/7/resource?path=pdf%2Fimages%2F00000.png");
        }

        // Pages of the given builder with the body text of a book.
        private static byte[] Build(Action<PdfDocumentBuilder> define)
        {
            var builder = new PdfDocumentBuilder();
            define(builder);
            return builder.Build();
        }

        private static void Pages(PdfDocumentBuilder builder, int count, Action<PdfPageBuilder> fill)
        {
            foreach (var _ in Enumerable.Range(0, count))
            {
                fill(builder.AddPage(PageSize.A4));
            }
        }

        private static void BodyLines(PdfDocumentBuilder builder, PdfPageBuilder page, int lines)
        {
            var font = builder.AddStandard14Font(Standard14Font.Helvetica);
            for (var line = 0; line < lines; line++)
            {
                page.AddText(Line + " " + line, 9, new PdfPoint(54, 780 - line * 14), font);
            }
        }

        // A marker line and the lines of a page of book text.
        private static void Body(PdfDocumentBuilder builder, PdfPageBuilder page, string marker, int lines, double firstLine = 780)
        {
            var font = builder.AddStandard14Font(Standard14Font.Helvetica);
            page.AddText(marker, 9, new PdfPoint(54, firstLine), font);
            for (var line = 1; line < lines; line++)
            {
                page.AddText(Line + " " + line, 9, new PdfPoint(54, firstLine - line * 14), font);
            }
        }
    }
}
