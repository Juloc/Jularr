using System.Text.RegularExpressions;
using Jularr.Integrations.Logic.Ocr;
using Jularr.Integrations.Logic.Pdf;
using SkiaSharp;

namespace Jularr.Backend.Logic.Reading
{
    // One scanned page as the recognizer read it, made into what a reader needs: its lines (where the recognizer was unsure, the scan's own recognition layer
    // at the same place wins; quotes it dropped come back from there) and its illustrations. The text is erased from a small copy of the page, filled with
    // the paper around each line; what is left in large connected areas is a picture. Short lines in a picture's rows are its labels, not running text.
    internal static partial class PdfScanAnalysis
    {
        // The work copy for erasing and finding pictures is about this many pixels high.
        private const int WorkHeight = 1000;
        private const float UnsureWord = 0.85f;

        // A line read with less certainty than this as a whole gives way to the layer where the layer reads plausibly and holds more.
        private const double UnsureLine = 0.8;
        private const double PlausibleLayer = 0.8;

        public static IReadOnlyList<RecognizedLine> Analyse(SKBitmap page, OcrPage read, IReadOnlyList<PdfLayerWord> layer)
        {
            var (width, height) = ((double)read.Width, (double)read.Height);

            // Words the page uses more than once: where the recognizer read a word the page has nowhere else and the layer one it has, the layer is right ("Ir" for "It").
            var common = read.Lines.SelectMany(line => line.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Select(Key).Where(word => word.Length > 0)
                .GroupBy(word => word).ToDictionary(group => group.Key, group => group.Count());
            var lines = read.Lines.Select(line =>
            {
                var (text, spans) = Text(line, layer, width, height, common);
                return new RecognizedLine(Round(line.Left / width), Round(line.Top / height), Round(line.Right / width), Round(line.Bottom / height), text, Math.Round(line.Confidence, 3),
                    Words: spans);
            }).Where(line => line.Text.Length > 0).ToList();
            lines = Layered(lines, layer, width, height);

            using var work = Small(page);
            var pictures = Pictures(work, lines);

            // The recognizer's boxes are grown well beyond the letters; the reading layout needs the line's own height and edges.
            using (var rgba = Rgba(page))
            {
                var pixels = rgba.GetPixelSpan().ToArray();
                lines = [.. lines.Select(line => Tight(pixels, rgba.Width, rgba.Height, line))];
            }

            // Labels: short text in the rows of a picture (ALWAYS / SOMETIMES / NEVER under the drawings).
            var body = lines.Count == 0 ? 0.02 : lines.Select(line => line.Bottom - line.Top).Order().ElementAt(lines.Count / 2);
            var result = lines.Select(line => line.Text.Length < 30 && pictures.Any(picture => (line.Top + line.Bottom) / 2 > picture.Top - body / 2
                && (line.Top + line.Bottom) / 2 < picture.Bottom + body / 2) ? line with { Kind = RecognizedLine.Label } : line).ToList();

            // A row of figures with a label between two of them is one illustration, its faint middle figure included.
            var labels = result.Where(line => line.Kind == RecognizedLine.Label).ToList();
            pictures = Merge(pictures, (one, other) => labels.Any(label => (label.Left + label.Right) / 2 > Math.Min(one.Right, other.Right)
                && (label.Left + label.Right) / 2 < Math.Max(one.Left, other.Left) && (label.Top + label.Bottom) / 2 > Math.Max(one.Top, other.Top) - body
                && (label.Top + label.Bottom) / 2 < Math.Min(one.Bottom, other.Bottom) + body));
            result.AddRange(pictures);
            return result;
        }

        // A line the recognizer was unsure of as a whole (a box it drew across two columns, small type beside large) takes the scan's own layer where that
        // reads plausibly and holds more: the layer's words in its box that no surer line holds, as lines column by column and row by row.
        private static List<RecognizedLine> Layered(List<RecognizedLine> lines, IReadOnlyList<PdfLayerWord> layer, double width, double height)
        {
            bool Inside(PdfLayerWord word, RecognizedLine line) => (word.Left + word.Right) / 2 is var x && x >= line.Left && x <= line.Right
                && (word.Top + word.Bottom) / 2 is var y && y >= line.Top && y <= line.Bottom;
            var sure = lines.Where(line => line.Confidence >= UnsureLine).ToList();
            var result = new List<RecognizedLine>();
            foreach (var line in lines)
            {
                var words = line.Confidence >= UnsureLine ? [] : layer.Where(word => Inside(word, line) && !sure.Any(other => Inside(word, other))).ToList();
                if (words.Count < 2 || PdfQuality.Score(words.Select(word => word.Text)) < PlausibleLayer
                    || words.Sum(word => Letters(word.Text).Length) <= Letters(line.Text).Length)
                {
                    result.Add(line);
                    continue;
                }

                // Lines: a word joins the line it shares its rows with and stands a word space from (against the smaller of the two), so columns, and small
                // type beside large, stay apart.
                var rows = new List<List<PdfLayerWord>>();
                foreach (var word in words.OrderBy(word => word.Left))
                {
                    var row = rows.FirstOrDefault(row => row[^1] is var last && Math.Min(last.Bottom - last.Top, word.Bottom - word.Top) * height is var low
                        && (Math.Min(last.Bottom, word.Bottom) - Math.Max(last.Top, word.Top)) * height >= low * 0.5 && (word.Left - last.Right) * width <= low * 1.2);
                    if (row is null)
                    {
                        rows.Add([word]);
                    }
                    else
                    {
                        row.Add(word);
                    }
                }

                foreach (var row in rows.OrderBy(row => row.Min(word => word.Top)))
                {
                    result.Add(new RecognizedLine(Round(row.Min(word => word.Left)), Round(row.Min(word => word.Top)), Round(row.Max(word => word.Right)),
                        Round(row.Max(word => word.Bottom)), string.Join(' ', row.Select(word => Typeset(word.Text))), UnsureLine,
                        Words: [.. row.SelectMany(word => new[] { Round(word.Left), Round(word.Right) })]));
                }
            }

            return result;
        }

        // A recognized line's text: its words, each checked against the scan's own layer where the recognizer was unsure of it, and where each word stands
        // (its left and right edge in fractions of the page, word after word), so Smart Pages can measure each word's look.
        private static (string Text, double[]? Spans) Text(OcrLine line, IReadOnlyList<PdfLayerWord> layer, double width, double height, IReadOnlyDictionary<string, int> common)
        {
            if (line.Characters.Count == 0)
            {
                return (Typeset(line.Text.Trim()), null);
            }

            var pitch = line.Characters.Count > 1 ? (line.Characters[^1].Center - line.Characters[0].Center) / (line.Characters.Count - 1) : line.Right - line.Left;
            var words = new List<string>();
            var spans = new List<(double Left, double Right)>();
            var group = new List<OcrCharacter>();
            void End()
            {
                if (group.Count == 0)
                {
                    return;
                }

                var word = Typeset(Inner(string.Concat(group.Select(character => character.Text))));
                var (left, right) = (Math.Max(line.Left, group[0].Center - pitch * 0.7) / width, Math.Min(line.Right, group[^1].Center + pitch * 0.7) / width);
                var (top, bottom) = (line.Top / height, line.Bottom / height);
                var other = layer.Where(candidate => Overlap(candidate, left, top, right, bottom) > 0.5).Select(candidate => Typeset(candidate.Text)).FirstOrDefault();
                if (other is not null && other != word)
                {
                    // Unsure: the layer's reading. Sure, but the same letters with more marks: the quotes and dashes the recognizer dropped. A word with a capital
                    // inside ("conFess") or one the page has nowhere else while it uses the layer's often ("Ir" for "It", "Tuts" for "This" at a large initial).
                    if (group.Average(character => character.Probability) < UnsureWord
                        || (other.Length > word.Length && Letters(other) == Letters(word))
                        || (Key(other) == Key(word) && word.Skip(1).Any(char.IsUpper) && word.Skip(1).Any(char.IsLower))
                        || (common.GetValueOrDefault(Key(word)) <= 1 && common.GetValueOrDefault(Key(other)) >= 2 && Math.Abs(Key(other).Length - Key(word).Length) <= 2)
                        // The recognizer reads a doubled character once ("16" for "116", "wil" for "will"); the layer's word with it is right.
                        || (Key(other).Length > Key(word).Length && Single(Key(other)) == Single(Key(word))))
                    {
                        word = other;
                    }
                }

                // A closing mark read apart from its word ("No ;") joins it, as do other marks where the layer prints them together; one the layer's word brought is not doubled.
                if (word.All(char.IsPunctuation) && words.Count > 0 && (word.All(",.;:!?”’".Contains) || words[^1].EndsWith(word, StringComparison.Ordinal)
                    || layer.Any(candidate => Letters(candidate.Text) == Letters(words[^1]) && candidate.Text.EndsWith(word, StringComparison.Ordinal) && Overlap(candidate, left, top, right, bottom) > 0)))
                {
                    words[^1] = words[^1].EndsWith(word, StringComparison.Ordinal) ? words[^1] : words[^1] + word;
                    spans[^1] = (spans[^1].Left, Math.Max(spans[^1].Right, right));
                }
                else
                {
                    words.Add(word);
                    spans.Add((left, right));
                }

                group.Clear();
            }

            foreach (var character in line.Characters)
            {
                if (character.Text == " ")
                {
                    End();
                }
                else
                {
                    group.Add(character);
                }
            }

            End();

            // An opening quote read apart from its word ("“ A") belongs to it; a quote read twice ("””") is one.
            for (var index = words.Count - 2; index >= 0; index--)
            {
                if (words[index] is "“" or "‘")
                {
                    words[index] += words[index + 1];
                    words.RemoveAt(index + 1);
                    spans[index] = (spans[index].Left, spans[index + 1].Right);
                    spans.RemoveAt(index + 1);
                }
            }

            return (Closed(Doubled().Replace(string.Join(' ', words), "$1")), [.. spans.SelectMany(span => new[] { Round(span.Left), Round(span.Right) })]);
        }

        [GeneratedRegex(@"([“”‘’])\1+")]
        private static partial Regex Doubled();

        private static string Letters(string word) => new([.. word.Where(char.IsLetterOrDigit)]);

        private static string Key(string word) => Letters(word).ToLowerInvariant();

        // A word with each run of one character as that character once.
        private static string Single(string word) => new([.. word.Where((character, index) => index == 0 || character != word[index - 1])]);

        // A double quote opened on the line and closed by what the recognizer read as a single one after the sentence's mark ("…found it!’ he") closes as double.
        private static string Closed(string line)
        {
            if (line.Count(character => character == '“') <= line.Count(character => character == '”'))
            {
                return line;
            }

            var index = line.LastIndexOf('’');
            return index > 0 && ".,!?".Contains(line[index - 1]) && line.LastIndexOf('‘') < line.IndexOf('“') ? line[..index] + '”' + line[(index + 1)..] : line;
        }

        // A capital between small letters ("conFess") is a misread small one; names such as McDonald keep theirs.
        private static string Inner(string word)
        {
            var characters = word.ToCharArray();
            for (var index = 1; index < characters.Length - 1; index++)
            {
                if (char.IsUpper(characters[index]) && char.IsLower(characters[index - 1]) && char.IsLower(characters[index + 1])
                    && !word.StartsWith("Mc", StringComparison.Ordinal) && !word.StartsWith("Mac", StringComparison.Ordinal))
                {
                    characters[index] = char.ToLowerInvariant(characters[index]);
                }
            }

            return new string(characters);
        }

        // A text line's box around its own letters: the dense band of its x-height from the box's middle, widened for ascenders and descenders, and the
        // columns that hold ink in it. A box without a clear band stays as it was.
        private static RecognizedLine Tight(byte[] pixels, int w, int h, RecognizedLine line)
        {
            var box = new SKRectI((int)(line.Left * w), (int)(line.Top * h), Math.Min(w - 1, (int)(line.Right * w)), Math.Min(h - 1, (int)(line.Bottom * h)));
            if (box.Width < 4 || box.Height < 4)
            {
                return line;
            }

            var paper = Ring(pixels, w, h, box);
            bool Ink(int x, int y)
            {
                var index = (y * w + x) * 4;
                return Math.Abs(pixels[index] - paper[0]) + Math.Abs(pixels[index + 1] - paper[1]) + Math.Abs(pixels[index + 2] - paper[2]) > 150;
            }

            var rows = new int[box.Height + 1];
            for (var y = 0; y <= box.Height; y++)
            {
                for (var x = box.Left; x <= box.Right; x++)
                {
                    rows[y] += Ink(x, box.Top + y) ? 1 : 0;
                }
            }

            // The densest row of the box's middle half and the rows around it that keep a sixth of its ink.
            var middle = Enumerable.Range(box.Height / 4, box.Height / 2 + 1).MaxBy(y => rows[y]);
            if (rows[middle] == 0)
            {
                return line;
            }

            var (first, last) = (middle, middle);
            while (first > 0 && rows[first - 1] * 6 >= rows[middle])
            {
                first--;
            }

            while (last < box.Height && rows[last + 1] * 6 >= rows[middle])
            {
                last++;
            }

            var band = last - first + 1;
            var (top, bottom) = (Math.Max(0, first - band * 0.35), Math.Min(box.Height, last + band * 0.3));
            int left = box.Right, right = box.Left;
            for (var y = first; y <= last; y++)
            {
                for (var x = box.Left; x <= box.Right; x++)
                {
                    if (Ink(x, box.Top + y))
                    {
                        (left, right) = (Math.Min(left, x), Math.Max(right, x));
                    }
                }
            }

            return right <= left ? line : line with
            {
                Left = Round((double)left / w), Top = Round((box.Top + top) / h), Right = Round((right + 1.0) / w), Bottom = Round((box.Top + bottom + 1) / h)
            };
        }

        // The share of the smaller box that both cover.
        private static double Overlap(PdfLayerWord word, double left, double top, double right, double bottom)
        {
            var (width, height) = (Math.Min(word.Right, right) - Math.Max(word.Left, left), Math.Min(word.Bottom, bottom) - Math.Max(word.Top, top));
            var smaller = Math.Min((word.Right - word.Left) * (word.Bottom - word.Top), (right - left) * (bottom - top));
            return width <= 0 || height <= 0 || smaller <= 0 ? 0 : width * height / smaller;
        }

        // The recognizer reads straight quotes; print has curly ones: opening at a word's start, closing and apostrophes elsewhere.
        internal static string Typeset(string word)
        {
            var characters = word.ToCharArray();
            for (var index = 0; index < characters.Length; index++)
            {
                if (characters[index] == '"')
                {
                    characters[index] = index == 0 ? '“' : '”';
                }
                else if (characters[index] == '\'')
                {
                    characters[index] = index == 0 && characters.Length > 1 && char.IsLetter(characters[1]) ? '‘' : '’';
                }
            }

            return new string(characters);
        }

        // A picture of the drawn page as a JPEG, the scan's paper tint (the paper around it) taken out so it sits on the reader's own paper. Grey and paper-like
        // pixels lose the tint; coloured ones only brighten, so a cool paper does not turn blue shirts yellow.
        public static byte[] Cut(SKBitmap page, RecognizedLine picture)
        {
            var rect = new SKRectI((int)(picture.Left * page.Width), (int)(picture.Top * page.Height), (int)Math.Ceiling(picture.Right * page.Width), (int)Math.Ceiling(picture.Bottom * page.Height));
            rect.Intersect(new SKRectI(0, 0, page.Width, page.Height));
            if (rect.Width < 2 || rect.Height < 2)
            {
                return [];
            }

            using var rgba = Rgba(page);
            var pixels = rgba.GetPixelSpan().ToArray();
            var paper = Ring(pixels, rgba.Width, rgba.Height, rect);
            var even = 255.0 / Math.Max(1, paper.Max());

            // A drawing in black ink on tinted paper (the scan's uneven tint would show as a colour cast) comes out in grey on white paper.
            var (coloured, counted) = (0, 0);
            for (var y = 0; y < rect.Height; y += 3)
            {
                for (var x = 0; x < rect.Width; x += 3)
                {
                    var index = ((rect.Top + y) * rgba.Width + rect.Left + x) * 4;
                    var balanced = Enumerable.Range(0, 3).Select(channel => pixels[index + channel] * 255.0 / Math.Max(1, paper[channel])).ToArray();
                    coloured += balanced.Max() - balanced.Min() > 60 ? 1 : 0;
                    counted++;
                }
            }

            var grey = coloured < counted * 0.03;
            using var crop = new SKBitmap(rect.Width, rect.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
            var target = crop.GetPixelSpan();
            for (var y = 0; y < rect.Height; y++)
            {
                for (var x = 0; x < rect.Width; x++)
                {
                    var (from, to) = (((rect.Top + y) * rgba.Width + rect.Left + x) * 4, (y * rect.Width + x) * 4);
                    if (grey)
                    {
                        var light = (byte)Math.Min(255, (pixels[from] * 0.299 + pixels[from + 1] * 0.587 + pixels[from + 2] * 0.114) * 255
                            / Math.Max(1, paper[0] * 0.299 + paper[1] * 0.587 + paper[2] * 0.114));
                        (target[to], target[to + 1], target[to + 2], target[to + 3]) = (light, light, light, 255);
                        continue;
                    }

                    var (most, least) = (Math.Max(pixels[from], Math.Max(pixels[from + 1], pixels[from + 2])), Math.Min(pixels[from], Math.Min(pixels[from + 1], pixels[from + 2])));
                    var colour = most == 0 ? 0 : Math.Clamp((most - least) / (most * 0.25), 0, 1);
                    for (var channel = 0; channel < 3; channel++)
                    {
                        var factor = (1 - colour) * 255.0 / Math.Max(1, paper[channel]) + colour * even;
                        target[to + channel] = (byte)Math.Min(255, pixels[from + channel] * factor);
                    }

                    target[to + 3] = 255;
                }
            }

            using var image = SKImage.FromBitmap(crop);
            using var data = image.Encode(SKEncodedImageFormat.Jpeg, 86);
            return data.ToArray();
        }

        private static double Round(double fraction) => Math.Round(Math.Clamp(fraction, 0, 1), 4);

        // The page's pixels in red, green, blue, alpha order, converted (a copy of the bitmap would keep PDFium's blue-first bytes).
        internal static SKBitmap Rgba(SKBitmap page)
        {
            var rgba = new SKBitmap(new SKImageInfo(page.Width, page.Height, SKColorType.Rgba8888, SKAlphaType.Premul));
            using var pixmap = page.PeekPixels();
            if (!pixmap.ReadPixels(rgba.Info, rgba.GetPixels(), rgba.RowBytes))
            {
                rgba.Dispose();
                throw new InvalidDataException("The page's pixels cannot be read.");
            }

            return rgba;
        }

        private static SKBitmap Small(SKBitmap page)
        {
            var scale = Math.Min(1.0, (double)WorkHeight / page.Height);
            var info = new SKImageInfo(Math.Max(1, (int)Math.Round(page.Width * scale)), Math.Max(1, (int)Math.Round(page.Height * scale)), SKColorType.Rgba8888, SKAlphaType.Premul);
            return page.Resize(info, new SKSamplingOptions(SKFilterMode.Linear)) ?? throw new InvalidDataException("The page cannot be scaled.");
        }

        // The pictures of the page: its lines erased, then large connected areas that are not paper, in fractions of the page.
        private static List<RecognizedLine> Pictures(SKBitmap work, IReadOnlyList<RecognizedLine> lines)
        {
            var (w, h) = (work.Width, work.Height);
            var pixels = work.GetPixelSpan().ToArray();
            var boxes = lines.Select(line =>
            {
                var grow = (line.Bottom - line.Top) * h * 0.06;
                return new SKRectI(Math.Max(0, (int)(line.Left * w - grow)), Math.Max(0, (int)(line.Top * h - grow)), Math.Min(w - 1, (int)Math.Ceiling(line.Right * w + grow)),
                    Math.Min(h - 1, (int)Math.Ceiling(line.Bottom * h + grow)));
            }).Where(box => box.Width > 0 && box.Height > 0).ToList();
            Erase(pixels, w, h, boxes, 8);
            return Regions(pixels, w, h);
        }

        // Fills the boxes from the top down with the paper around them; the paper is smoothed across this many columns either side.
        internal static void Erase(byte[] pixels, int w, int h, IReadOnlyList<SKRectI> boxes, int reach)
        {
            // Pixels still covered by letters of lines not yet erased are not paper to sample.
            var covered = new int[w * h];
            foreach (var box in boxes)
            {
                Cover(covered, w, box, 1);
            }

            foreach (var box in boxes.OrderBy(box => box.Top))
            {
                Cover(covered, w, box, -1);
                Fill(pixels, covered, w, h, box, reach);
            }
        }

        private static void Cover(int[] covered, int w, SKRectI box, int by)
        {
            for (var y = box.Top; y <= box.Bottom; y++)
            {
                for (var x = box.Left; x <= box.Right; x++)
                {
                    covered[y * w + x] += by;
                }
            }
        }

        // A line box filled column by column, blending the paper above it into the paper below it (smoothed across neighbouring columns), so tints and
        // gradients carry on; pixels of lines not yet erased are not sampled.
        private static void Fill(byte[] pixels, int[] covered, int w, int h, SKRectI box, int reach)
        {
            var band = Math.Max(2, box.Height / 8);
            var columns = box.Width + 1;
            var above = new int[columns * 3];
            var below = new int[columns * 3];
            var ring = Ring(pixels, w, h, box);
            for (var column = 0; column < columns; column++)
            {
                var x = box.Left + column;
                // Paper only: a rule or a bar right above or below the line (another ink than the paper around it) is not drawn into it.
                var up = Paper(Median(pixels, covered, w, h, x, box.Top - band - 1, box.Top - 1), ring);
                var down = Paper(Median(pixels, covered, w, h, x, box.Bottom + 1, box.Bottom + band + 1), ring);
                var (a, b) = (up ?? down ?? ring, down ?? up ?? ring);
                for (var channel = 0; channel < 3; channel++)
                {
                    above[column * 3 + channel] = a[channel];
                    below[column * 3 + channel] = b[channel];
                }
            }

            (above, below) = (Smooth(above, columns, reach), Smooth(below, columns, reach));
            for (var y = box.Top; y <= box.Bottom; y++)
            {
                var t = (double)(y - box.Top) / Math.Max(1, box.Height);
                for (var column = 0; column < columns; column++)
                {
                    var index = (y * w + box.Left + column) * 4;
                    for (var channel = 0; channel < 3; channel++)
                    {
                        pixels[index + channel] = (byte)(above[column * 3 + channel] + (below[column * 3 + channel] - above[column * 3 + channel]) * t);
                    }
                }
            }
        }

        private static int[]? Paper(int[]? sample, int[] ring) =>
            sample is not null && Math.Abs(sample[0] - ring[0]) + Math.Abs(sample[1] - ring[1]) + Math.Abs(sample[2] - ring[2]) <= 120 ? sample : null;

        private static int[]? Median(byte[] pixels, int[] covered, int w, int h, int x, int from, int to)
        {
            var samples = new List<int>[] { [], [], [] };
            for (var y = Math.Max(0, from); y <= Math.Min(h - 1, to); y++)
            {
                for (var sx = Math.Max(0, x - 2); sx <= Math.Min(w - 1, x + 2); sx++)
                {
                    if (covered[y * w + sx] > 0)
                    {
                        continue;
                    }

                    for (var channel = 0; channel < 3; channel++)
                    {
                        samples[channel].Add(pixels[(y * w + sx) * 4 + channel]);
                    }
                }
            }

            return samples[0].Count < 3 ? null : [.. samples.Select(channel => channel.Order().ElementAt(channel.Count / 2))];
        }

        // The median colour of a thin ring just outside the box: the paper the letters stood on.
        internal static int[] Ring(byte[] pixels, int w, int h, SKRectI box)
        {
            var samples = new List<int>[] { [], [], [] };
            var ring = Math.Max(2, box.Height / 6);
            void Add(int x, int y)
            {
                if (x >= 0 && y >= 0 && x < w && y < h)
                {
                    for (var channel = 0; channel < 3; channel++)
                    {
                        samples[channel].Add(pixels[(y * w + x) * 4 + channel]);
                    }
                }
            }

            for (var x = box.Left - ring; x <= box.Right + ring; x += 2)
            {
                Add(x, box.Top - ring);
                Add(x, box.Bottom + ring);
            }

            for (var y = box.Top; y <= box.Bottom; y += 2)
            {
                Add(box.Left - ring, y);
                Add(box.Right + ring, y);
            }

            return samples[0].Count == 0 ? [255, 255, 255] : [.. samples.Select(channel => channel.Order().ElementAt(channel.Count / 2))];
        }

        private static int[] Smooth(int[] row, int columns, int reach)
        {
            var smooth = new int[row.Length];
            for (var column = 0; column < columns; column++)
            {
                var (from, to) = (Math.Max(0, column - reach), Math.Min(columns - 1, column + reach));
                for (var channel = 0; channel < 3; channel++)
                {
                    var sum = 0;
                    for (var other = from; other <= to; other++)
                    {
                        sum += row[other * 3 + channel];
                    }

                    smooth[column * 3 + channel] = sum / (to - from + 1);
                }
            }

            return smooth;
        }

        // Connected areas of ink or colour on a coarse grid; small specks, rules and the scan's dark page edge are no pictures.
        private static List<RecognizedLine> Regions(byte[] pixels, int w, int h)
        {
            var cell = Math.Max(3, h / 150);
            var (gw, gh) = (w / cell, h / cell);
            var paper = Ring(pixels, w, h, new SKRectI(w / 2 - 2, h - 8, w / 2 + 2, h - 6));
            var busy = new bool[gw, gh];
            for (var gy = 0; gy < gh; gy++)
            {
                for (var gx = 0; gx < gw; gx++)
                {
                    var differing = 0;
                    for (var y = gy * cell; y < gy * cell + cell; y++)
                    {
                        for (var x = gx * cell; x < gx * cell + cell; x++)
                        {
                            var index = (y * w + x) * 4;
                            if (Math.Abs(pixels[index] - paper[0]) + Math.Abs(pixels[index + 1] - paper[1]) + Math.Abs(pixels[index + 2] - paper[2]) > 90)
                            {
                                differing++;
                            }
                        }
                    }

                    busy[gx, gy] = differing * 16 >= cell * cell * 3;
                }
            }

            var seen = new bool[gw, gh];
            var found = new List<RecognizedLine>();
            var queue = new Queue<(int X, int Y)>();
            for (var gy = 0; gy < gh; gy++)
            {
                for (var gx = 0; gx < gw; gx++)
                {
                    if (!busy[gx, gy] || seen[gx, gy])
                    {
                        continue;
                    }

                    var (minX, maxX, minY, maxY, count) = (gx, gx, gy, gy, 0);
                    seen[gx, gy] = true;
                    queue.Enqueue((gx, gy));
                    while (queue.Count > 0)
                    {
                        var (x, y) = queue.Dequeue();
                        count++;
                        (minX, maxX, minY, maxY) = (Math.Min(minX, x), Math.Max(maxX, x), Math.Min(minY, y), Math.Max(maxY, y));
                        for (var dy = -2; dy <= 2; dy++)
                        {
                            for (var dx = -2; dx <= 2; dx++)
                            {
                                var (nx, ny) = (x + dx, y + dy);
                                if (nx >= 0 && ny >= 0 && nx < gw && ny < gh && !seen[nx, ny] && busy[nx, ny])
                                {
                                    seen[nx, ny] = true;
                                    queue.Enqueue((nx, ny));
                                }
                            }
                        }
                    }

                    var (bw, bh) = (maxX - minX + 1, maxY - minY + 1);
                    var edge = (minX == 0 || maxX == gw - 1) && bw < gw * 0.1;
                    if (bw > gw * 0.18 && bh > gh * 0.08 && count > bw * bh * 0.25 && !edge)
                    {
                        found.Add(new RecognizedLine(Round((double)minX / gw), Round((double)minY / gh), Round((double)(maxX + 1) / gw), Round((double)(maxY + 1) / gh), "", 1,
                            RecognizedLine.Picture));
                    }
                }
            }

            return Merge(found, (_, _) => false);
        }

        // Parts of one illustration come apart where its lines are faint: parts that overlap, figures side by side in one row and a part just below another
        // are one picture, and those the caller joins.
        private static List<RecognizedLine> Merge(List<RecognizedLine> pictures, Func<RecognizedLine, RecognizedLine, bool> joins)
        {
            for (var merged = true; merged;)
            {
                merged = false;
                for (var first = 0; first < pictures.Count && !merged; first++)
                {
                    for (var second = first + 1; second < pictures.Count && !merged; second++)
                    {
                        var (one, other) = (pictures[first], pictures[second]);
                        var across = Math.Min(one.Right, other.Right) - Math.Max(one.Left, other.Left);
                        var down = Math.Min(one.Bottom, other.Bottom) - Math.Max(one.Top, other.Top);
                        var row = down > Math.Min(one.Bottom - one.Top, other.Bottom - other.Top) * 0.5 && across > -0.1;
                        var column = across > Math.Min(one.Right - one.Left, other.Right - other.Left) * 0.5 && down > -0.03;
                        if (!(across > 0 && down > 0) && !row && !column && !joins(one, other))
                        {
                            continue;
                        }

                        pictures[first] = one with
                        {
                            Left = Math.Min(one.Left, other.Left), Top = Math.Min(one.Top, other.Top), Right = Math.Max(one.Right, other.Right), Bottom = Math.Max(one.Bottom, other.Bottom)
                        };
                        pictures.RemoveAt(second);
                        merged = true;
                    }
                }
            }

            return pictures;
        }
    }
}
