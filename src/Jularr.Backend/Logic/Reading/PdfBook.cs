using System.Text.RegularExpressions;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Core;

namespace Jularr.Backend.Logic.Reading
{
    // A PDF with a text layer as a reflowable book: running headers and footers drop, lines join into paragraphs across pages,
    // short numbered or titled lines on their own start chapters. Scanned pages without text are no book.
    internal static partial class PdfBook
    {
        // Raised when the converted output changes; old documents are prepared again.
        public const int AnalyzerVersion = 1;
        public const int MaximumPages = 5000;

        // A chapter without headings is split so no section grows unbounded.
        private const int MaximumParagraphs = 150;

        [GeneratedRegex(@"\d+")]
        private static partial Regex Digits();

        [GeneratedRegex(@"^(\d{1,3}|[IVXLC]{1,7}|(Kapitel|Chapter|Teil|Part|Buch|Book|Prolog|Prologue|Epilog|Epilogue)\b.{0,60})$", RegexOptions.IgnoreCase)]
        private static partial Regex HeadingText();

        [GeneratedRegex(@"[.!?”""’)…:]$")]
        private static partial Regex SentenceEnd();

        public static IReadOnlyList<PdfChapter> Read(Stream stream)
        {
            List<List<PdfLine>> pages;
            try
            {
                using var pdf = PdfDocument.Open(stream);
                if (pdf.NumberOfPages > MaximumPages)
                {
                    throw new InvalidDataException("The PDF has too many pages.");
                }

                pages = [.. pdf.GetPages().Select(page => Lines(page))];
            }
            catch (PdfDocumentFormatException exception)
            {
                throw new InvalidDataException("The PDF cannot be read.", exception);
            }

            DropRunningLines(pages);
            var lines = pages.SelectMany(page => page).ToArray();
            if (lines.Sum(line => line.Text.Length) < Math.Max(200, pages.Count * 20))
            {
                throw new PdfWithoutTextException();
            }

            // The body is the most common size, spacing and left edge; nine in ten lines end before the right margin.
            var body = Mode(lines.Select(line => Math.Round(line.Size)));
            var spacing = Mode(pages.SelectMany(page => page.Zip(page.Skip(1), (upper, lower) => Math.Round(upper.Bottom - lower.Bottom))).Where(gap => gap > 0).DefaultIfEmpty(body * 1.2));
            var left = Mode(lines.Select(line => Math.Round(line.Left)));
            var right = lines.Select(line => line.Right).Order().ElementAt((int)(lines.Length * 0.9));
            return Chapters(pages, body, spacing, left, right);
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
            static bool AtEdge(PdfLine line) => line.Bottom > line.PageHeight * 0.92 || line.Bottom < line.PageHeight * 0.08;
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
            var paragraphs = new List<string>();
            var lines = new List<string>();
            PdfLine? previous = null;

            void CloseParagraph()
            {
                if (lines.Count > 0)
                {
                    paragraphs.Add(Join(lines));
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
            private string? _text;
        }
    }

    // One chapter of a converted PDF: its heading, if any, and its paragraphs.
    internal sealed record PdfChapter(string? Title, IReadOnlyList<string> Paragraphs);

    // A PDF of scanned pages: it stays a PDF for the browser's own viewer.
    internal sealed class PdfWithoutTextException() : Exception("The PDF has no text layer.");
}
