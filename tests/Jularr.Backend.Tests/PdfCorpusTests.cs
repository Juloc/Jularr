using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using Jularr.Backend.Logic.Reading;

namespace Jularr.Backend.Tests
{
    // The evaluation of the PDF and EPUB pipeline over the owner's own books. Opt in with JULARR_TEST_BOOKS naming the folder; the compact report goes to JULARR_TEST_REPORT or the test output.
    [TestClass]
    public sealed partial class PdfCorpusTests
    {
        [GeneratedRegex(@"data-figure=""(page|wide|original)""")]
        private static partial Regex Figures();

        [GeneratedRegex(@"<img\b")]
        private static partial Regex Pictures();

        [TestMethod]
        public void Corpus_EveryBook_ReportsItsPagesAndFigures()
        {
            var root = Environment.GetEnvironmentVariable("JULARR_TEST_BOOKS");
            if (root is null)
            {
                Assert.Inconclusive("JULARR_TEST_BOOKS names no folder with books.");
            }

            var report = new StringBuilder();
            var total = new Totals();
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Where(file => file.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".epub", StringComparison.OrdinalIgnoreCase)).Order())
            {
                var name = Path.GetRelativePath(root, file);
                var timer = Stopwatch.StartNew();
                try
                {
                    if (file.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
                    {
                        Pdf(file, name, report, total);
                    }
                    else
                    {
                        Epub(file, name, report, total);
                    }
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    total.Errors++;
                    report.AppendLine($"{name}: ERROR {exception.GetType().Name}: {exception.Message}");
                }

                report.AppendLine($"    {timer.Elapsed.TotalSeconds:0.0} s");
            }

            report.AppendLine();
            report.AppendLine($"TOTAL pdf={total.Pdfs} epub={total.Epubs} readable={total.Readable} declined={total.Declined} errors={total.Errors} figures(page/wide/original)={total.PageFigures}/{total.WideFigures}/{total.Stands}");
            var target = Environment.GetEnvironmentVariable("JULARR_TEST_REPORT") ?? Path.Combine(AppContext.BaseDirectory, "pdf-corpus-report.txt");
            File.WriteAllText(target, report.ToString());
            Console.WriteLine(report);
        }

        private static void Pdf(string file, string name, StringBuilder report, Totals total)
        {
            total.Pdfs++;
            using var stream = File.OpenRead(file);
            PdfBookContent book;
            try
            {
                book = PdfBook.Read(stream);
            }
            catch (PdfWithoutTextException)
            {
                total.Declined++;
                report.AppendLine($"{name}: no text layer; stays a PDF");
                return;
            }

            var pages = book.Pages;
            var kinds = string.Join(' ', pages.GroupBy(page => page.Kind).OrderBy(group => group.Key).Select(group => $"{group.Key}={group.Count()}"));
            var reasons = string.Join(' ', pages.GroupBy(page => page.Reason).OrderByDescending(group => group.Count()).Select(group => $"{group.Key}={group.Count()}"));
            var blocks = book.Chapters.SelectMany(chapter => chapter.Blocks).ToArray();
            var characters = blocks.Sum(block => block.Text?.Length ?? 0);
            var page = blocks.Count(block => block.Figure == ReadingHtml.PageFigure);
            var wide = blocks.Count(block => block.Figure == ReadingHtml.WideFigure);
            var stands = blocks.Count(block => block.Figure == ReadingHtml.OriginalFigure);
            var small = blocks.Count(block => block.Image is not null && block.Figure is null);
            var found = pages.Sum(item => item.Pictures);
            var unreadable = pages.Sum(item => item.UnreadablePictures);
            var quality = pages.Where(item => item.Kind == PdfPageKind.Text).Sum(item => item.Characters) is var chars and > 0 ? pages.Where(item => item.Kind == PdfPageKind.Text).Sum(item => item.TextQuality * item.Characters) / chars : 1;
            total.PageFigures += page;
            total.WideFigures += wide;
            total.Stands += stands;
            if (book.Ebook.IsReadable)
            {
                total.Readable++;
            }
            else
            {
                total.Declined++;
            }

            report.AppendLine($"{name}: {pages.Count} pages [{kinds}] chars={characters} chapters={book.Chapters.Count} blocks={blocks.Length} quality={quality.ToString("0.00", CultureInfo.InvariantCulture)}");
            report.AppendLine($"    reasons: {reasons}");
            report.AppendLine($"    pictures found={found} unreadable={unreadable} kept={book.Images.Count} ({book.Images.Sum(image => image.Bytes.Length) / 1024} KB) figures page/wide/small/original={page}/{wide}/{small}/{stands}");
            report.AppendLine($"    reading={(book.Reading.IsReadable ? "reflow" : "original: " + book.Reading.Reason)} ebook={(book.Ebook.IsReadable ? "derive" : "decline: " + book.Ebook.Reason)}");
        }

        private static void Epub(string file, string name, StringBuilder report, Totals total)
        {
            total.Epubs++;
            using var archive = ZipFile.OpenRead(file);
            var package = EpubReader.Read(archive);
            var indexes = package.Spine.Select((item, index) => (item.Path, index)).ToDictionary(pair => pair.Path, pair => pair.index, StringComparer.Ordinal);
            var sizes = new Dictionary<string, (int Width, int Height)?>(StringComparer.Ordinal);
            (int Width, int Height)? SizeOf(string path) => sizes.TryGetValue(path, out var size) ? size : sizes[path] = archive.GetEntry(path) is { } entry ? ImageSize.Read(EpubReader.ReadPrefix(entry, 256 * 1024)) : null;
            var (page, wide, stand, images, characters) = (0, 0, 0, 0, 0L);
            var referenced = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in package.Spine)
            {
                var prepared = ReadingHtml.Prepare(EpubReader.ReadText(archive.GetEntry(item.Path)!), item.Path, 1, indexes, package.Manifest, SizeOf);
                var figures = Figures().Matches(prepared.Html).Select(match => match.Groups[1].Value).ToArray();
                page += figures.Count(kind => kind == "page");
                wide += figures.Count(kind => kind == "wide");
                stand += figures.Count(kind => kind == "original");
                images += Pictures().Count(prepared.Html);
                characters += prepared.Characters;
                referenced.UnionWith(prepared.Resources);
            }

            var pictures = package.Manifest.Count(entry => entry.Value.StartsWith("image/", StringComparison.Ordinal));
            total.PageFigures += page;
            total.WideFigures += wide;
            total.Readable++;
            report.AppendLine($"{name}: {package.Spine.Count} sections chars={characters} images in package={pictures} shown={referenced.Count} dropped={Math.Max(0, pictures - referenced.Count)} pictures in text={images} figures page/wide={page}/{wide}");
        }

        private sealed class Totals
        {
            public int Pdfs;
            public int Epubs;
            public int Readable;
            public int Declined;
            public int Errors;
            public int PageFigures;
            public int WideFigures;
            public int Stands;
        }
    }
}
