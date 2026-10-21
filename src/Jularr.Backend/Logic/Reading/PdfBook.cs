using System.Globalization;
using System.Text.RegularExpressions;
using Jularr.Integrations.Logic.Ocr;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Core;

namespace Jularr.Backend.Logic.Reading
{
    // A PDF with a text layer as a reflowable book: running headers and footers drop, lines join into paragraphs across pages and columns, short numbered or titled lines start chapters.
    // Pictures, drawings, tables and irregular pages stay whole as figures where they stood; a page only the original can show is marked as such. Scanned pages are read by a recognizer where one is given.
    internal static partial class PdfBook
    {
        // Raised when the converted output changes; old documents are prepared again.
        public const int AnalyzerVersion = 3;
        public const int MaximumPages = 5000;

        // Pictures smaller than an icon are decoration; a book keeps a bounded number of bounded pictures.
        private const double MinimumImagePoints = 48;
        private const int MaximumImages = 500;
        private const int MaximumImageBytes = 10 * 1024 * 1024;

        // A picture over most of the page is the page itself: a scan, a plate or a cover.
        private const double FullPage = 0.85;
        private const double ScanCoverage = 0.5;
        private const float MinimumConfidence = 0.5f;

        // A page picture shows as a page of its own from this share of the page, as the text's width from a smaller one.
        private const double PageFigureShare = 0.45;
        private const double WideFigureWidth = 0.4;
        private const double WideFigureHeight = 0.1;

        // Pages whose layers together take fewer bytes than this are blank.
        private const int BlankScanBytes = 8000;

        // A chapter without headings is split so no section grows unbounded.
        private const int MaximumParagraphs = 150;

        [GeneratedRegex(@"\d+")]
        private static partial Regex Digits();

        [GeneratedRegex(@"^(\d{1,3}|[IVXLC]{1,7}|(Kapitel|Chapter|Chapitre|Cap[ií]tulo|Capitolo|Teil|Part|Partie|Parte|Buch|Book|Livre|Libro|Prolog|Prologue|Prólogo|Epilog|Epilogue|Epílogo|Vorwort|Nachwort|Einleitung|Foreword|Preface|Prefacio|Introduction|Introducción|Introduzione)\b.{0,60})$", RegexOptions.IgnoreCase)]
        private static partial Regex HeadingText();

        // A heading that names itself needs no gap below it: a kind of division and its number, or a lone front-matter word.
        [GeneratedRegex(@"^((Kapitel|Chapter|Chapitre|Cap[ií]tulo|Capitolo|Teil|Part|Partie|Parte|Buch|Book|Livre|Libro)\s+(\d{1,3}|[IVXLC]{1,7})|Prolog|Prologue|Prólogo|Epilog|Epilogue|Epílogo|Vorwort|Nachwort|Einleitung|Foreword|Preface|Prefacio|Introduction|Introducción|Introduzione)\.?$", RegexOptions.IgnoreCase)]
        private static partial Regex NamedHeading();

        [GeneratedRegex(@"[.!?”""’)…:]$")]
        private static partial Regex SentenceEnd();

        public static PdfBookContent Read(Stream stream, Func<byte[], OcrPage>? recognize = null)
        {
            var state = new BookState();
            List<List<PdfLine>> pages;
            try
            {
                using var pdf = PdfDocument.Open(stream);
                if (pdf.NumberOfPages > MaximumPages)
                {
                    throw new InvalidDataException("The PDF has too many pages.");
                }

                pages = [.. pdf.GetPages().Select(page => ReadPage(page, state, recognize))];
            }
            catch (Exception exception) when (IsFault(exception))
            {
                throw new InvalidDataException("The PDF cannot be read.", exception);
            }

            DropRunningLines(pages);
            var lines = pages.SelectMany(page => page).Where(line => line.Image is null && line.Figure is null).ToArray();
            if (state.TextCharacters < Math.Max(200, pages.Count * 20) || lines.Length == 0)
            {
                throw new PdfWithoutTextException();
            }

            // The body is the most common size, spacing and left edge; nine in ten lines end before the right margin.
            var body = Mode(lines.Where(line => !line.Hidden).Select(line => Math.Round(line.Size)).DefaultIfEmpty(Mode(lines.Select(line => Math.Round(line.Size)))));
            var spacing = Mode(pages.Select(page => page.Where(line => line.Image is null && line.Figure is null).ToArray())
                .SelectMany(page => page.Zip(page.Skip(1), (upper, lower) => Math.Round(upper.Bottom - lower.Bottom))).Where(gap => gap > 0).DefaultIfEmpty(body * 1.2));
            var left = Mode(lines.Select(line => Math.Round(line.Left)));
            var right = lines.Select(line => line.Right).Order().ElementAt((int)(lines.Length * 0.9));
            return new PdfBookContent(Chapters(pages, body, spacing, left, right), state.Images, state.Reports, PdfQuality.Judge(state.Reports, false), PdfQuality.Judge(state.Reports, true));
        }

        // Faults of a file PdfPig cannot make sense of.
        private static bool IsFault(Exception exception) => exception is PdfDocumentFormatException or ArgumentException or InvalidOperationException or IndexOutOfRangeException or NotSupportedException
            or KeyNotFoundException or FormatException or OverflowException or NullReferenceException or InvalidCastException or ArithmeticException;

        // A page's lines, pictures and figures top to bottom, and what the analysis found on it.
        private static List<PdfLine> ReadPage(UglyToad.PdfPig.Content.Page page, BookState state, Func<byte[], OcrPage>? recognize)
        {
            PdfPageScan scan;
            try
            {
                scan = new PdfPageScan(page);
            }
            catch (Exception exception) when (IsFault(exception))
            {
                state.Reports.Add(new PdfPageReport(page.Number, PdfPageKind.Placeholder, "unreadable", 0, 0, 0, 0, 0, 1, false, 1));
                return [Stand(page.Number, page.Height)];
            }

            var result = new PageResult();
            try
            {
                Analyse(page, scan, state, recognize, result);
            }
            catch (Exception exception) when (IsFault(exception))
            {
                result = new PageResult { Kind = PdfPageKind.Placeholder, Reason = "error" };
                result.Items.Add(Stand(scan.Number, scan.Box.Top));
            }

            var characters = result.Characters ?? scan.Characters;
            state.TextCharacters += result.Kind == PdfPageKind.Placeholder ? 0 : result.Characters ?? scan.Words.Where(word => !word.Hidden || result.Kind == PdfPageKind.Text).Sum(word => word.Text.Length);
            state.Reports.Add(new PdfPageReport(scan.Number, result.Kind, result.Reason, characters, scan.Words.Count, scan.Pictures.Count, scan.Pictures.Count(picture => picture.Decoded is null),
                scan.Shapes, result.Columns, scan.IsHidden, result.Quality));
            return InReadingOrder(result.Items);
        }

        // Text lines keep the order the page layout gave them (columns, a scan's reading order); a picture or figure stands before the first line that lies below its top edge.
        private static List<PdfLine> InReadingOrder(List<PdfLine> items)
        {
            var ordered = items.Where(item => item.Image is null && item.Figure is null).ToList();
            foreach (var other in items.Where(item => item.Image is not null || item.Figure is not null).OrderBy(item => item.Bottom))
            {
                var index = ordered.FindIndex(line => line.Bottom < other.Bottom);
                ordered.Insert(index < 0 ? ordered.Count : index, other);
            }

            return ordered;
        }

        private static void Analyse(UglyToad.PdfPig.Content.Page page, PdfPageScan scan, BookState state, Func<byte[], OcrPage>? recognize, PageResult result)
        {
            var area = scan.Box.Area;
            var words = scan.Words;
            var hidden = scan.IsHidden;
            var body = words.Count == 0 ? 10 : words.Where(word => !word.Hidden).Select(word => Math.Round(word.Size)).DefaultIfEmpty(10).GroupBy(size => size).OrderByDescending(group => group.Count()).First().Key;
            var quality = PdfQuality.Score(words.Select(word => word.Text));
            result.Quality = quality;

            // A scan without any text goes to the recognizer.
            if (recognize is not null && words.Count == 0 && Recognized(page, recognize) is { } read)
            {
                result.Kind = PdfPageKind.Text;
                result.Reason = "recognized";
                result.Characters = read.Sum(line => line.Text.Length);
                result.Items.AddRange(read);
                return;
            }

            var full = scan.Pictures.Where(picture => picture.Box.Area >= area * FullPage).ToList();
            var readable = full.Where(picture => picture.Decoded is not null).MaxBy(picture => picture.Box.Area);
            var textual = words.Count >= 3 && quality >= 0.6 && scan.Characters >= 40;
            var visibleText = words.Where(word => !word.Hidden).Sum(word => word.Text.Length);

            // A page that is a picture: a scan with its text layer, a plate, a cover or a page nobody can read.
            if (full.Count > 0 && !(visibleText >= 150 && !hidden))
            {
                if (hidden && textual)
                {
                    result.Kind = PdfPageKind.Text;
                    result.Reason = "scan-layer";
                    result.Items.AddRange(PdfLayout.Lines(words, scan.Box.Height));
                    return;
                }

                if (words.Count == 0 && readable is null && full.Sum(picture => picture.StoredBytes) < BlankScanBytes)
                {
                    result.Kind = PdfPageKind.Blank;
                    result.Reason = "blank";
                    return;
                }

                if (readable is not null && Picture(state, readable, scan, "page", Excerpt(words, hidden && quality >= 0.6)) is { } figure)
                {
                    result.Kind = PdfPageKind.Figure;
                    result.Reason = "page-picture";
                    result.Items.Add(figure);
                    result.Items.AddRange(PdfLayout.Lines([.. words.Where(word => !word.Hidden)], scan.Box.Height));
                    return;
                }

                result.Kind = PdfPageKind.Placeholder;
                result.Reason = readable is null ? "unreadable-picture" : "limit";
                result.Items.Add(Stand(scan.Number, scan.Box.Top));
                return;
            }

            // Text in lines, columns or tables.
            var rows = PdfLayout.Lines(words, scan.Box.Height);
            IReadOnlyList<double> gutters = [];
            var complex = false;
            if (!hidden)
            {
                gutters = PdfLayout.Gutters(words, rows, body, out complex);
            }

            var lines = gutters.Count > 0 && !complex ? PdfLayout.ColumnLines(words, gutters, scan.Box.Height) : rows;
            result.Columns = gutters.Count + 1;
            var regions = new List<PdfBox>();
            string? reason = null;
            if (complex)
            {
                regions.Add(scan.Box);
                reason = "columns";
            }
            else if (!hidden)
            {
                foreach (var (first, last) in PdfLayout.Tables(rows, body, scan.Rules))
                {
                    regions.Add(rows.Skip(first).Take(last - first + 1).Select(row => row.Box).Aggregate((one, other) => one.Union(other)).Expand(4));
                    reason = "table";
                }
            }

            if (scan.TooComplex)
            {
                regions.Add(scan.Box);
                reason = "drawing";
            }
            else if (scan.Shapes >= 5 && scan.ShapeBounds is { } drawn && drawn.Area >= area * 0.04)
            {
                regions.Add(drawn.Expand(2));
                reason ??= "drawing";
                if (reason != "table")
                {
                    reason = "drawing";
                }
            }

            regions = Merge(regions, lines, scan.Box);
            var items = new List<PdfLine>();
            foreach (var region in regions)
            {
                var inside = lines.Where(line => line.Box.OverlapShare(region) >= 0.5).ToList();
                var covered = scan.Pictures.Where(picture => picture.Box.Intersects(region) && picture.Box.OverlapShare(region) >= 0.15).ToList();
                var svg = covered.Any(picture => picture.Decoded is null && picture.Box.Area >= area * 0.02) ? null : PdfSvg.Render(page, region, scan.Pictures);
                if (svg is null || state.Images.Count >= MaximumImages)
                {
                    result.Kind = PdfPageKind.Placeholder;
                    result.Reason = "not-drawable";
                    result.Items.Clear();
                    result.Items.Add(Stand(scan.Number, scan.Box.Top));
                    return;
                }

                var share = region.Area / Math.Max(1, area);
                var captioned = Caption(lines, inside, region, body);
                var caption = captioned?.Text;
                var alt = caption ?? Excerpt([.. words.Where(word => word.Box.Intersects(region) && !word.Hidden)], true);
                var path = "pdf/images/" + state.Images.Count.ToString("D5", CultureInfo.InvariantCulture) + ".svg";
                state.Images.Add(new PdfImage(path, "image/svg+xml", svg));
                items.Add(new PdfLine(region.Top, 0, scan.Box.Height) { Image = path, Figure = share >= PageFigureShare ? ReadingHtml.PageFigure : ReadingHtml.WideFigure, Alt = alt, Caption = caption });
                lines = [.. lines.Where(line => !inside.Contains(line) && captioned?.Lines.Contains(line) != true)];
            }

            // Pictures stand where they stood.
            foreach (var picture in scan.Pictures.OrderByDescending(picture => picture.Box.Top))
            {
                if (regions.Any(region => picture.Box.OverlapShare(region) >= 0.5) || full.Contains(picture) || picture.Box.Width < MinimumImagePoints || picture.Box.Height < MinimumImagePoints)
                {
                    continue;
                }

                var share = picture.Box.Area / Math.Max(1, area);
                var kind = share >= PageFigureShare ? ReadingHtml.PageFigure
                    : picture.Box.Width >= scan.Box.Width * WideFigureWidth && picture.Box.Height >= scan.Box.Height * WideFigureHeight ? ReadingHtml.WideFigure : null;
                if (picture.Decoded is null)
                {
                    if (share >= 0.05)
                    {
                        result.Unreadable++;
                        items.Add(Stand(scan.Number, picture.Box.Top));
                    }

                    continue;
                }

                var captioned = kind is null ? null : Caption(lines, [], picture.Box, body);
                if (Picture(state, picture, scan, kind, captioned?.Text ?? "", captioned?.Text) is { } line)
                {
                    items.Add(line);
                    lines = [.. lines.Where(other => captioned?.Lines.Contains(other) != true)];
                }
            }

            items.AddRange(lines);
            result.Items.AddRange(items);
            result.Kind = lines.Count > 0 ? PdfPageKind.Text : items.Count > 0 ? PdfPageKind.Figure : PdfPageKind.Blank;
            result.Reason = lines.Count > 0 ? reason ?? "text" : items.Count > 0 ? reason ?? "picture" : "blank";
            if (result.Unreadable > 0 && result.Kind != PdfPageKind.Placeholder)
            {
                result.Reason += "+original";
            }

            if (result.Kind == PdfPageKind.Text && lines.Count > 0 && quality < 0.4)
            {
                result.Reason = "junk-text";
            }
        }

        // A readable picture becomes a resource and a line in the page's flow; null when the book has taken as many pictures as it keeps.
        private static PdfLine? Picture(BookState state, PdfPicture picture, PdfPageScan scan, string? figure, string alt, string? caption = null)
        {
            if (picture.Decoded is not var (bytes, mediaType, extension) || bytes.Length > MaximumImageBytes || state.Images.Count >= MaximumImages)
            {
                return null;
            }

            var path = "pdf/images/" + state.Images.Count.ToString("D5", CultureInfo.InvariantCulture) + extension;
            state.Images.Add(new PdfImage(path, mediaType, bytes));
            return new PdfLine(picture.Box.Top, 0, scan.Box.Height) { Image = path, Figure = figure, Alt = alt, Caption = caption };
        }

        // The page, shown by the original only.
        private static PdfLine Stand(int number, double top) => new(top, 0, top) { Figure = "original", Page = number };

        // The text a figure carries, as its alternative text.
        private static string Excerpt(IReadOnlyList<PdfWord> words, bool use)
        {
            if (!use || words.Count == 0)
            {
                return "";
            }

            var text = string.Join(' ', PdfLayout.Lines(words, 0).Select(line => line.Text));
            return text.Length <= 400 ? text : text[..400].TrimEnd() + "…";
        }

        // The caption just above or below a figure: a line starting like one, with the lines that follow it closely.
        private static (string Text, List<PdfLine> Lines)? Caption(IReadOnlyList<PdfLine> lines, IReadOnlyList<PdfLine> inside, PdfBox figure, double body)
        {
            var near = lines.Where(line => !inside.Contains(line) && line.Hidden == false && PdfLayout.IsCaption(line.Text)
                && (Math.Abs(line.Top - figure.Bottom) < body * 3 || Math.Abs(line.Bottom - figure.Top) < body * 3)).OrderBy(line => Math.Abs(line.Top - figure.Bottom)).FirstOrDefault();
            if (near is null)
            {
                return null;
            }

            var text = near.Text;
            var used = new List<PdfLine> { near };
            var index = lines.ToList().IndexOf(near);
            for (var next = index + 1; next < lines.Count && next <= index + 3; next++)
            {
                if (lines[next].Words.Count == 0 || lines[next - 1].Bottom - lines[next].Bottom > body * 1.8 || inside.Contains(lines[next]))
                {
                    break;
                }

                text += " " + lines[next].Text;
                used.Add(lines[next]);
            }

            return (text.Length > 600 ? text[..600].TrimEnd() + "…" : text, used);
        }

        // Overlapping or touching regions are one; a region that takes most of the page takes all of it.
        private static List<PdfBox> Merge(List<PdfBox> regions, IReadOnlyList<PdfLine> lines, PdfBox page)
        {
            var merged = new List<PdfBox>();
            foreach (var region in regions.OrderByDescending(region => region.Top))
            {
                var current = region;
                foreach (var other in merged.Where(other => other.Expand(6).Intersects(current)).ToArray())
                {
                    current = current.Union(other);
                    merged.Remove(other);
                }

                merged.Add(current);
            }

            // Text lines the region cuts through belong to it.
            for (var index = 0; index < merged.Count; index++)
            {
                foreach (var line in lines.Where(line => line.Box.OverlapShare(merged[index]) is > 0.05 and < 0.98))
                {
                    merged[index] = merged[index].Union(line.Box);
                }

                if (merged[index].Area >= page.Area * 0.7)
                {
                    merged[index] = page;
                }
            }

            return merged;
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

            return [.. lines.OrderByDescending(line => line.Bottom)];
        }

        // Lines in the top or bottom tenth of the page whose text repeats, digits aside, on a fifth of the pages are headers or footers.
        private static void DropRunningLines(List<List<PdfLine>> pages)
        {
            static bool AtEdge(PdfLine line) => line.Image is null && line.Figure is null && (line.Top > line.PageHeight * 0.9 || line.Bottom < line.PageHeight * 0.1);
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
                    if (line.Image is not null || line.Figure is not null)
                    {
                        CloseParagraph();
                        paragraphs.Add(new PdfBlock(null, line.Image, line.Figure, line.Alt, line.Caption, line.Page));
                        continue;
                    }

                    // A short heading line stands apart from its neighbours; recognized text has no reliable size.
                    var first = index == 0 || page[index - 1].Bottom - line.Bottom > spacing * 1.5 || page[index - 1].Figure is not null;
                    var apart = first && (index + 1 >= page.Count || line.Bottom - page[index + 1].Bottom > spacing * 1.5);
                    var named = first && NamedHeading().IsMatch(line.Text);
                    if (line.Words.Count <= 10 && (named || apart && (HeadingText().IsMatch(line.Text) || !line.Hidden && line.Size > body * 1.15)))
                    {
                        // A chapter's name may stand on the lines below its number, centred like it.
                        CloseParagraph();
                        var heading = line.Text;
                        var last = line;
                        while (index + 1 < page.Count && TitleLine(last, page[index + 1], spacing))
                        {
                            heading += (heading.EndsWith('.') ? " " : ". ") + page[++index].Text;
                            last = page[index];
                        }

                        CloseChapter(heading);
                        previous = last;
                        continue;
                    }

                    // A wide gap or an indent starts a paragraph; on a new page or column so does a short line ending a sentence before it. A line in lower case continues.
                    var jump = index > 0 && page[index - 1].Image is null && page[index - 1].Figure is null && line.Bottom - page[index - 1].Bottom > spacing;
                    var starts = index > 0 && !jump
                        ? page[index - 1].Bottom - line.Bottom > spacing * 1.5 || line.Left > left + body * 1.2 || page[index - 1].Figure is not null
                        : previous is null || SentenceEnd().IsMatch(previous.Text) && (jump || previous.Right < right - body * 3) || line.Left > left + body * 1.2;
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

        // The line right below a heading, short, without sentence end and centred under it, continues the heading.
        private static bool TitleLine(PdfLine heading, PdfLine next, double spacing) => next.Image is null && next.Figure is null && heading.Bottom - next.Bottom <= spacing * 1.3 && next.Words.Count is > 0 and <= 8
            && !SentenceEnd().IsMatch(next.Text) && !char.IsLower(next.Text[0]) && Math.Abs((heading.Left + heading.Right - next.Left - next.Right) / 2) < 30;

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

        private sealed class BookState
        {
            public List<PdfImage> Images { get; } = [];
            public List<PdfPageReport> Reports { get; } = [];
            public long TextCharacters { get; set; }
        }

        private sealed class PageResult
        {
            public PdfPageKind Kind { get; set; } = PdfPageKind.Blank;
            public string Reason { get; set; } = "";
            public List<PdfLine> Items { get; } = [];
            public int Columns { get; set; } = 1;
            public double Quality { get; set; } = 1;
            public int? Characters { get; set; }
            public int Unreadable { get; set; }
        }
    }

    // One chapter of a converted PDF: its heading, if any, and its paragraphs, pictures and figures in order.
    internal sealed record PdfChapter(string? Title, IReadOnlyList<PdfBlock> Blocks);

    // A paragraph's text, or a picture's path with how it is shown (a page of its own, the text's width, or none for a small one), its alternative text and caption; a stand-in for a page names the page.
    internal sealed record PdfBlock(string? Text, string? Image, string? Figure = null, string? Alt = null, string? Caption = null, int Page = 0);

    internal sealed record PdfImage(string Path, string MediaType, byte[] Bytes);

    internal sealed record PdfBookContent(IReadOnlyList<PdfChapter> Chapters, IReadOnlyList<PdfImage> Images, IReadOnlyList<PdfPageReport> Pages, PdfVerdict Reading, PdfVerdict Ebook);

    // A PDF of scanned pages: it stays a PDF for the browser's own viewer.
    internal sealed class PdfWithoutTextException() : Exception("The PDF has no text layer.");
}
