using System.Text.RegularExpressions;

namespace Jularr.Backend.Logic.Reading
{
    // A rectangle in page points, y upwards.
    internal readonly record struct PdfBox(double Left, double Bottom, double Right, double Top)
    {
        public double Width => Right - Left;
        public double Height => Top - Bottom;
        public double Area => Math.Max(0, Width) * Math.Max(0, Height);

        public PdfBox Union(PdfBox other) => new(Math.Min(Left, other.Left), Math.Min(Bottom, other.Bottom), Math.Max(Right, other.Right), Math.Max(Top, other.Top));

        public PdfBox Expand(double by) => new(Left - by, Bottom - by, Right + by, Top + by);

        public PdfBox Intersection(PdfBox other) => new(Math.Max(Left, other.Left), Math.Max(Bottom, other.Bottom), Math.Min(Right, other.Right), Math.Min(Top, other.Top));

        public bool Intersects(PdfBox other) => Left < other.Right && other.Left < Right && Bottom < other.Top && other.Bottom < Top;

        public double OverlapShare(PdfBox other) => Area <= 0 ? 0 : Intersection(other).Area / Area;
    }

    // One word of a page with the size and visibility of its letters and its place in the content stream.
    internal sealed record PdfWord(PdfBox Box, string Text, double Size, bool Hidden, int Order);

    // One line of a page: its words left to right, or a picture, a figure or a note that a page cannot be shown.
    internal sealed class PdfLine(double bottom, double size, double pageHeight)
    {
        public double Bottom { get; } = bottom;
        public double Size { get; } = size;
        public double PageHeight { get; } = pageHeight;
        public double Top { get; set; } = bottom + size;
        public bool Hidden { get; set; }
        public List<(double Left, double Right, string Text)> Words { get; } = [];
        public double Left => Words.Min(word => word.Left);
        public double Right => Words.Max(word => word.Right);
        public PdfBox Box => new(Left, Bottom, Right, Math.Max(Top, Bottom + Size));
        public string Text => _text ??= string.Join(' ', Words.OrderBy(word => word.Left).Select(word => word.Text));

        // A picture's path among the book's resources; it has no words.
        public string? Image { get; init; }

        // How a picture or drawing is shown, its text alternative and caption, and the page a stand-in points to.
        public string? Figure { get; init; }
        public string? Alt { get; init; }
        public string? Caption { get; init; }
        public int Page { get; init; }
        private string? _text;
    }

    // Reading order and structure of the words on one page: lines, columns and tables.
    internal static partial class PdfLayout
    {
        // A caption starts with its kind and number, or the kind and a colon.
        [GeneratedRegex(@"^\s*(Figure|Figura|Fig|Abbildung|Abb|Bild|Tabelle|Table|Tabla|Tableau|Tabella|Chart|Diagram|Diagrama|Grafik|Gráfico|Graph|Tafel|Plate|Planche|Illustration|Karte|Map|Mapa|Carte|Photo|Foto|Imagen|Image|Schema|Esquema)\.?\s*(\d+|[IVXL]+|:)|^\s*(Source|Quelle|Fuente|Fonte)\s*:", RegexOptions.IgnoreCase)]
        private static partial Regex CaptionStart();

        // Words on one baseline form a line, top to bottom; a hidden recognition layer keeps the order of its stream, which follows the scan's own reading order.
        public static List<PdfLine> Lines(IReadOnlyList<PdfWord> words, double pageHeight)
        {
            var lines = new List<PdfLine>();
            if (words.Count > 0 && words.Count(word => word.Hidden) * 2 > words.Count)
            {
                PdfLine? current = null;
                foreach (var word in words.OrderBy(word => word.Order))
                {
                    var line = current is not null && Math.Abs(current.Bottom - word.Box.Bottom) < Math.Max(word.Size, 1) * 0.4 && word.Box.Left >= current.Right - word.Size ? current : null;
                    if (line is null)
                    {
                        lines.Add(line = new PdfLine(word.Box.Bottom, word.Size, pageHeight) { Hidden = true });
                    }

                    Add(line, word);
                    current = line;
                }

                return lines;
            }

            return [.. Rows(words).Select(row => FromWords(row, pageHeight))];
        }

        // Words on one baseline, top to bottom.
        private static List<List<PdfWord>> Rows(IEnumerable<PdfWord> words)
        {
            var rows = new List<(double Bottom, List<PdfWord> Words)>();
            foreach (var word in words.OrderByDescending(word => word.Box.Bottom).ThenBy(word => word.Box.Left))
            {
                var row = rows.Count > 0 && Math.Abs(rows[^1].Bottom - word.Box.Bottom) < Math.Max(word.Size, 1) * 0.4 ? rows[^1] : default;
                if (row.Words is null)
                {
                    rows.Add(row = (word.Box.Bottom, []));
                }

                row.Words.Add(word);
            }

            return [.. rows.Select(row => row.Words)];
        }

        private static PdfLine FromWords(List<PdfWord> row, double pageHeight)
        {
            var line = new PdfLine(row[0].Box.Bottom, row[0].Size, pageHeight);
            foreach (var word in row)
            {
                Add(line, word);
            }

            return line;
        }

        private static void Add(PdfLine line, PdfWord word)
        {
            line.Words.Add((word.Box.Left, word.Box.Right, word.Text));
            line.Top = Math.Max(line.Top, word.Box.Top);
        }

        // Text in two or three clean columns: the gutters between them, as x positions; none for a single column or an irregular page.
        public static IReadOnlyList<double> Gutters(IReadOnlyList<PdfWord> words, IReadOnlyList<PdfLine> rows, double body, out bool irregular)
        {
            irregular = false;
            if (rows.Count < 10 || words.Count < 40)
            {
                return [];
            }

            var left = words.Min(word => word.Box.Left);
            var right = words.Max(word => word.Box.Right);
            var span = right - left;
            if (span < body * 18)
            {
                return [];
            }

            // How many rows have a word over each point of the page's width.
            var from = (int)Math.Floor(left + span * 0.12);
            var to = (int)Math.Ceiling(right - span * 0.12);
            var cover = new int[Math.Max(1, to - from + 1)];
            foreach (var row in rows)
            {
                var starts = row.Words.OrderBy(word => word.Left).ToArray();
                foreach (var word in starts)
                {
                    for (var x = Math.Max(from, (int)Math.Floor(word.Left)); x <= Math.Min(to, (int)Math.Ceiling(word.Right)); x++)
                    {
                        cover[x - from]++;
                    }
                }
            }

            // A gutter is a clear stripe of at least a line's height; rows crossing it are few.
            var gutters = new List<(double Centre, double Width, int Crossing)>();
            var limit = Math.Max(1, (int)(rows.Count * 0.06));
            for (var index = 0; index < cover.Length; index++)
            {
                if (cover[index] > limit)
                {
                    continue;
                }

                var end = index;
                var crossing = 0;
                while (end < cover.Length && cover[end] <= limit)
                {
                    crossing = Math.Max(crossing, cover[end]);
                    end++;
                }

                if (end - index >= Math.Max(9, body * 1.1))
                {
                    gutters.Add((from + (index + end) / 2.0, end - index, crossing));
                }

                index = end;
            }

            var chosen = new List<double>();
            foreach (var gutter in gutters.OrderByDescending(item => item.Width))
            {
                if (chosen.Count == 2 || chosen.Any(other => Math.Abs(other - gutter.Centre) < span * 0.15))
                {
                    continue;
                }

                // Text stands on both sides in many rows.
                var both = rows.Count(row => row.Words.Any(word => word.Right <= gutter.Centre) && row.Words.Any(word => word.Left >= gutter.Centre));
                var crossed = rows.Count(row => row.Words.Any(word => word.Left < gutter.Centre && word.Right > gutter.Centre));
                if (both >= Math.Max(8, rows.Count * 0.3))
                {
                    chosen.Add(gutter.Centre);
                    irregular |= crossed > rows.Count * 0.04;
                }
            }

            chosen.Sort();
            return chosen;
        }

        // The lines of text in columns: bands between rows spanning the page, each read column by column.
        public static List<PdfLine> ColumnLines(IReadOnlyList<PdfWord> words, IReadOnlyList<double> gutters, double pageHeight)
        {
            int Column(PdfBox box) => gutters.Count(gutter => (box.Left + box.Right) / 2 > gutter);
            bool Crosses(PdfBox box) => gutters.Any(gutter => box.Left < gutter - 1 && box.Right > gutter + 1);

            // Rows across the whole page tell where a spanning heading stands.
            var result = new List<PdfLine>();
            var band = new List<PdfWord>();
            void Flush()
            {
                for (var column = 0; column <= gutters.Count; column++)
                {
                    result.AddRange(Lines([.. band.Where(word => Column(word.Box) == column)], pageHeight));
                }

                band.Clear();
            }

            foreach (var row in Rows(words))
            {
                if (row.Any(word => Crosses(word.Box)))
                {
                    Flush();
                    result.Add(FromWords(row, pageHeight));
                }
                else
                {
                    band.AddRange(row);
                }
            }

            Flush();
            return result;
        }

        // Rows with several cells that line up under each other: a table, as the lines that belong to it (indexes into the rows).
        public static List<(int First, int Last)> Tables(IReadOnlyList<PdfLine> rows, double body, int rules)
        {
            var cells = rows.Select(row => Cells(row, body)).ToArray();
            var tables = new List<(int, int)>();
            var index = 0;
            while (index < rows.Count)
            {
                if (cells[index].Count < 3)
                {
                    index++;
                    continue;
                }

                var last = index;
                var skipped = 0;
                for (var next = index + 1; next < rows.Count && skipped <= 1; next++)
                {
                    if (cells[next].Count >= 3 && Aligned(cells[index..(next + 1)], body))
                    {
                        last = next;
                        skipped = 0;
                    }
                    else
                    {
                        skipped++;
                    }
                }

                var count = last - index + 1;
                var wide = cells[index..(last + 1)].Count(row => row.Count >= 4);
                if (count >= 4 && (wide * 10 >= count * 6 || rules >= 3))
                {
                    tables.Add((index, last));
                }

                index = last + 1;
            }

            return tables;
        }

        // Left edges of the words that start a cell: a word after a wide gap.
        private static List<double> Cells(PdfLine row, double body)
        {
            var starts = new List<double>();
            var words = row.Words.OrderBy(word => word.Left).ToArray();
            var previous = double.NegativeInfinity;
            foreach (var word in words)
            {
                if (word.Left - previous > body * 1.6 || starts.Count == 0)
                {
                    starts.Add(word.Left);
                }

                previous = word.Right;
            }

            return starts;
        }

        // At least two cell edges besides the first appear in most of the rows.
        private static bool Aligned(IEnumerable<List<double>> rows, double body)
        {
            var list = rows.ToArray();
            var edges = list.SelectMany(row => row.Skip(1)).GroupBy(edge => Math.Round(edge / Math.Max(4, body * 0.8)));
            return edges.Count(group => group.Count() * 10 >= list.Length * 6) >= 2;
        }

        public static bool IsCaption(string text) => CaptionStart().IsMatch(text);
    }
}
