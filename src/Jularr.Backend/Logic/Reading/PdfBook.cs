using System.Text.RegularExpressions;
using Jularr.Integrations.Logic.Ocr;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Core;

namespace Jularr.Backend.Logic.Reading
{
    // A PDF with a text layer as a reflowable book: running headers and footers drop, lines join into paragraphs across pages,
    // short numbered or titled lines on their own start chapters, pictures stay where they stood. Scanned pages are read by a recognizer where one is given, else they are no book.
    internal static partial class PdfBook
    {
        // Raised when the converted output changes; old documents are prepared again.
        public const int AnalyzerVersion = 2;
        public const int MaximumPages = 5000;

        // Pictures smaller than an icon are decoration; a book keeps a bounded number of bounded pictures.
        private const double MinimumImagePoints = 48;
        private const int MaximumImages = 500;
        private const int MaximumImageBytes = 10 * 1024 * 1024;

        // A scanned page is one picture over most of the page; uncertain recognized lines are left out rather than read wrong.
        private const double ScanCoverage = 0.5;
        private const float MinimumConfidence = 0.5f;

        // A chapter without headings is split so no section grows unbounded.
        private const int MaximumParagraphs = 150;

        [GeneratedRegex(@"\d+")]
        private static partial Regex Digits();

        [GeneratedRegex(@"^(\d{1,3}|[IVXLC]{1,7}|(Kapitel|Chapter|Teil|Part|Buch|Book|Prolog|Prologue|Epilog|Epilogue)\b.{0,60})$", RegexOptions.IgnoreCase)]
        private static partial Regex HeadingText();

        [GeneratedRegex(@"[.!?”""’)…:]$")]
        private static partial Regex SentenceEnd();

        public static PdfBookContent Read(Stream stream, Func<byte[], OcrPage>? recognize = null)
        {
            List<List<PdfLine>> pages;
            var images = new List<PdfImage>();
            try
            {
                using var pdf = PdfDocument.Open(stream);
                if (pdf.NumberOfPages > MaximumPages)
                {
                    throw new InvalidDataException("The PDF has too many pages.");
                }

                pages = [.. pdf.GetPages().Select(page => Page(page, images, recognize))];
            }
            catch (PdfDocumentFormatException exception)
            {
                throw new InvalidDataException("The PDF cannot be read.", exception);
            }

            DropRunningLines(pages);
            var lines = pages.SelectMany(page => page).Where(line => line.Image is null).ToArray();
            if (lines.Sum(line => line.Text.Length) < Math.Max(200, pages.Count * 20))
            {
                throw new PdfWithoutTextException();
            }

            // The body is the most common size, spacing and left edge; nine in ten lines end before the right margin.
            var body = Mode(lines.Select(line => Math.Round(line.Size)));
            var spacing = Mode(pages.Select(page => page.Where(line => line.Image is null).ToArray())
                .SelectMany(page => page.Zip(page.Skip(1), (upper, lower) => Math.Round(upper.Bottom - lower.Bottom))).Where(gap => gap > 0).DefaultIfEmpty(body * 1.2));
            var left = Mode(lines.Select(line => Math.Round(line.Left)));
            var right = lines.Select(line => line.Right).Order().ElementAt((int)(lines.Length * 0.9));
            return new PdfBookContent(Chapters(pages, body, spacing, left, right), images);
        }

        // A picture stands in the page's flow at its top edge; PNG where PdfPig decodes it, JPEG as it is stored.
        private static IEnumerable<PdfLine> Images(UglyToad.PdfPig.Content.Page page, List<PdfImage> images)
        {
            foreach (var image in page.GetImages())
            {
                if (images.Count >= MaximumImages || image.Bounds.Width < MinimumImagePoints || image.Bounds.Height < MinimumImagePoints)
                {
                    continue;
                }

                var (bytes, mediaType, extension) = image.TryGetPng(out var png) ? (png, "image/png", ".png")
                    : image.RawMemory.Span is [0xFF, 0xD8, ..] ? (image.RawMemory.ToArray(), "image/jpeg", ".jpg")
                    : (null, "", "");
                if (bytes is null || bytes.Length > MaximumImageBytes)
                {
                    continue;
                }

                var path = "pdf/images/" + images.Count.ToString("D5", System.Globalization.CultureInfo.InvariantCulture) + extension;
                images.Add(new PdfImage(path, mediaType, bytes));
                yield return new PdfLine(image.Bounds.Top, 0, page.Height) { Image = path };
            }
        }

        // A page's lines and pictures top to bottom; a page without text that is a scan, its recognized lines instead.
        private static List<PdfLine> Page(UglyToad.PdfPig.Content.Page page, List<PdfImage> images, Func<byte[], OcrPage>? recognize)
        {
            var lines = Lines(page);
            return [.. (recognize is not null && lines.Count == 0 && Recognized(page, recognize) is { } read ? read : lines.Concat(Images(page, images))).OrderByDescending(line => line.Bottom)];
        }

        // The scan's lines in page points, grouped by baseline like a text page's words.
        private static List<PdfLine>? Recognized(UglyToad.PdfPig.Content.Page page, Func<byte[], OcrPage> recognize)
        {
            var scan = page.GetImages().Where(image => image.BoundingBox.Width * image.BoundingBox.Height >= page.Width * page.Height * ScanCoverage).MaxBy(image => image.BoundingBox.Width * image.BoundingBox.Height);
            var bytes = scan is null ? null : scan.TryGetPng(out var png) ? png : scan.RawMemory.Span is [0xFF, 0xD8, ..] ? scan.RawMemory.ToArray() : null;
            if (scan is null || bytes is null || bytes.Length > MaximumImageBytes * 4)
            {
                return null;
            }

            OcrPage read;
            try
            {
                read = recognize(bytes);
            }
            catch (InvalidDataException)
            {
                return null;
            }

            var (scaleX, scaleY) = (scan.BoundingBox.Width / read.Width, scan.BoundingBox.Height / read.Height);
            var lines = new List<PdfLine>();
            foreach (var box in read.Lines.Where(line => line.Confidence >= MinimumConfidence).OrderByDescending(line => line.Bottom))
            {
                var (bottom, size) = (scan.BoundingBox.Top - box.Bottom * scaleY, (box.Bottom - box.Top) * scaleY * 0.7);
                var line = lines.LastOrDefault(candidate => Math.Abs(candidate.Bottom - bottom) < Math.Max(size, 1) * 0.4);
                if (line is null)
                {
                    lines.Add(line = new PdfLine(bottom, size, page.Height));
                }

                line.Words.Add((scan.BoundingBox.Left + box.Left * scaleX, scan.BoundingBox.Left + box.Right * scaleX, box.Text));
            }

            return lines;
        }

        private static List<PdfLine> Lines(UglyToad.PdfPig.Content.Page page)
        {
            // Words on one baseline form a line, top to bottom.
            var lines = new List<PdfLine>();
            foreach (var word in page.GetWords().Where(word => !string.IsNullOrWhiteSpace(word.Text)).OrderByDescending(word => word.BoundingBox.Bottom).ThenBy(word => word.BoundingBox.Left))
            {
                var size = word.Letters.Count == 0 ? 0 : word.Letters.Average(letter => letter.PointSize);
                var line = lines.LastOrDefault(candidate => Math.Abs(candidate.Bottom - word.BoundingBox.Bottom) < Math.Max(size, 1) * 0.4);
                if (line is null)
                {
                    lines.Add(line = new PdfLine(word.BoundingBox.Bottom, size, page.Height));
                }

                line.Words.Add((word.BoundingBox.Left, word.BoundingBox.Right, word.Text));
            }

            return [.. lines.OrderByDescending(line => line.Bottom)];
        }

        // Lines at the page edges whose text repeats, digits aside, on a fifth of the pages are headers or footers.
        private static void DropRunningLines(List<List<PdfLine>> pages)
        {
            static bool AtEdge(PdfLine line) => line.Image is null && (line.Bottom > line.PageHeight * 0.92 || line.Bottom < line.PageHeight * 0.08);
            var running = pages.SelectMany(page => page.Where(AtEdge)).GroupBy(line => Digits().Replace(line.Text, "#"))
                .Where(group => group.Count() >= Math.Max(3, pages.Count / 5)).Select(group => group.Key).ToHashSet(StringComparer.Ordinal);
            foreach (var page in pages)
            {
                page.RemoveAll(line => AtEdge(line) && running.Contains(Digits().Replace(line.Text, "#")));
            }
        }

        private static List<PdfChapter> Chapters(List<List<PdfLine>> pages, double body, double spacing, double left, double right)
        {
            var chapters = new List<PdfChapter>();
            string? title = null;
            var paragraphs = new List<PdfBlock>();
            var lines = new List<string>();
            PdfLine? previous = null;

            void CloseParagraph()
            {
                if (lines.Count > 0)
                {
                    paragraphs.Add(new PdfBlock(Join(lines), null));
                    lines.Clear();
                }

                if (paragraphs.Count >= MaximumParagraphs)
                {
                    CloseChapter(title);
                }
            }

            void CloseChapter(string? next)
            {
                if (paragraphs.Count > 0 || title is not null)
                {
                    chapters.Add(new PdfChapter(title, [.. paragraphs]));
                    paragraphs.Clear();
                }

                title = next;
            }

            foreach (var page in pages)
            {
                for (var index = 0; index < page.Count; index++)
                {
                    var line = page[index];
                    if (line.Image is { } picture)
                    {
                        CloseParagraph();
                        paragraphs.Add(new PdfBlock(null, picture));
                        continue;
                    }

                    // A short heading line stands apart from its neighbours.
                    var apart = (index == 0 || page[index - 1].Bottom - line.Bottom > spacing * 1.5) && (index + 1 >= page.Count || line.Bottom - page[index + 1].Bottom > spacing * 1.5);
                    if (apart && line.Words.Count <= 10 && (HeadingText().IsMatch(line.Text) || line.Size > body * 1.15))
                    {
                        CloseParagraph();
                        CloseChapter(line.Text);
                        previous = line;
                        continue;
                    }

                    // A wide gap or an indent starts a paragraph; on a new page so does a short line ending a sentence before it. A line in lower case continues.
                    var starts = index > 0
                        ? page[index - 1].Bottom - line.Bottom > spacing * 1.5 || line.Left > left + body * 1.2
                        : previous is null || previous.Right < right - body * 3 && SentenceEnd().IsMatch(previous.Text) || line.Left > left + body * 1.2;
                    if (starts && !(line.Text.Length > 0 && char.IsLower(line.Text[0])))
                    {
                        CloseParagraph();
                    }

                    lines.Add(line.Text);
                    previous = line;
                }
            }

            CloseParagraph();
            CloseChapter(null);
            return chapters;
        }

        // A hyphen at a line's end joins the word it split.
        private static string Join(List<string> lines)
        {
            var text = lines[0];
            foreach (var next in lines.Skip(1))
            {
                text = text.EndsWith('-') && !text.EndsWith("--", StringComparison.Ordinal) && next.Length > 0 && char.IsLower(next[0]) ? text[..^1] + next : text + " " + next;
            }

            return text;
        }

        private static double Mode(IEnumerable<double> values) => values.GroupBy(value => value).OrderByDescending(group => group.Count()).ThenBy(group => group.Key).Select(group => group.Key).FirstOrDefault();

        private sealed class PdfLine(double bottom, double size, double pageHeight)
        {
            public double Bottom { get; } = bottom;
            public double Size { get; } = size;
            public double PageHeight { get; } = pageHeight;
            public List<(double Left, double Right, string Text)> Words { get; } = [];
            public double Left => Words.Min(word => word.Left);
            public double Right => Words.Max(word => word.Right);
            public string Text => _text ??= string.Join(' ', Words.OrderBy(word => word.Left).Select(word => word.Text));

            // A picture's path among the book's resources; it has no words.
            public string? Image { get; init; }
            private string? _text;
        }
    }

    // One chapter of a converted PDF: its heading, if any, and its paragraphs and pictures in order.
    internal sealed record PdfChapter(string? Title, IReadOnlyList<PdfBlock> Blocks);

    // A paragraph's text or a picture's path.
    internal sealed record PdfBlock(string? Text, string? Image);

    internal sealed record PdfImage(string Path, string MediaType, byte[] Bytes);

    internal sealed record PdfBookContent(IReadOnlyList<PdfChapter> Chapters, IReadOnlyList<PdfImage> Images);

    // A PDF of scanned pages: it stays a PDF for the browser's own viewer.
    internal sealed class PdfWithoutTextException() : Exception("The PDF has no text layer.");
}
