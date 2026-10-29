using SkiaSharp;

namespace Jularr.Backend.Logic.Reading
{
    // Smart Pages: a scanned page in its own layout, its text set anew. The page as PDFium draws it with every line the recognizer read filled with the
    // paper around it, and the text in blocks at the places of the original, each word's look measured on the scan (serif or sans, weight, slant, width,
    // size, colour), so the Reader sets it, or its translation, with its own fonts into the same boxes. Labels inside pictures are text like any other.
    internal static class PdfSmartPages
    {
        // The text-free page is kept this many pixels on its longer side.
        private const int ImageSide = 1600;
        private const int ImageQuality = 80;
        private const double MinimumConfidence = 0.5;

        public static PdfSmartPage? Build(SKBitmap drawn, RecognizedPage read, int number)
        {
            using var rgba = PdfScanAnalysis.Rgba(drawn);
            var (w, h) = (rgba.Width, rgba.Height);
            var pixels = rgba.GetPixelSpan().ToArray();

            // The lines to set: text the recognizer was sure of, and labels; unsure marks inside pictures stay part of them.
            var lines = new List<Line>();
            foreach (var recognized in read.Lines.Where(line => line.Kind is null or RecognizedLine.Label && line.Confidence >= MinimumConfidence && line.Text.Any(char.IsLetterOrDigit)))
            {
                var box = Clamp(new SKRectI((int)(recognized.Left * w), (int)(recognized.Top * h), (int)Math.Ceiling(recognized.Right * w), (int)Math.Ceiling(recognized.Bottom * h)), w, h);
                if (box.Width < 3 || box.Height < 3)
                {
                    continue;
                }

                var line = new Line(box, recognized.Kind == RecognizedLine.Label);
                var texts = recognized.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var spans = recognized.Words is { } edges && edges.Length == texts.Length * 2 ? edges : Spread(recognized, texts);
                var paper = PdfScanAnalysis.Ring(pixels, w, h, box);
                for (var index = 0; index < texts.Length; index++)
                {
                    var word = Clamp(new SKRectI((int)(spans[index * 2] * w), box.Top, (int)Math.Ceiling(spans[index * 2 + 1] * w), box.Bottom), w, h);
                    var style = Measure(pixels, w, h, word, paper, texts[index].Length);
                    line.Words.Add(new Word(texts[index], style?.Left ?? word.Left) { Style = style });
                }

                Looks(line);
                lines.Add(line);
            }

            if (lines.Count == 0)
            {
                return null;
            }

            var blocks = Blocks(lines, w, h);

            // The text-free page: every line box, with room for accents and descenders, filled with the paper around it; the page's long rules stay.
            var original = (byte[])pixels.Clone();
            PdfScanAnalysis.Erase(pixels, w, h, [.. lines.Select(line => Reach(pixels, w, h, Clamp(Grow(line.Box, Math.Max(2, line.Box.Height * 0.12)), w, h)))], Math.Max(8, w / 100));
            Rules(original, pixels, w, h);
            return new PdfSmartPage(number, Encode(pixels, w, h), Math.Round((double)w / h, 4), blocks);
        }

        // Rules between columns and under headings run far beyond any line of text: their ink, longer than a sixth of the page in one direction and thin in
        // the other, is put back where erasing a line crossed it.
        private static void Rules(byte[] original, byte[] pixels, int w, int h)
        {
            var paper = PdfScanAnalysis.Ring(original, w, h, new SKRectI(w / 2 - 2, h - 12, w / 2 + 2, h - 8));
            var ink = new bool[w * h];
            for (var index = 0; index < w * h; index++)
            {
                var at = index * 4;
                ink[index] = Math.Abs(original[at] - paper[0]) + Math.Abs(original[at + 1] - paper[1]) + Math.Abs(original[at + 2] - paper[2]) > 150;
            }

            var (tall, wide, thin) = (h / 6, w / 6, Math.Max(3, w / 200));
            void Keep(int x, int y)
            {
                var at = (y * w + x) * 4;
                (pixels[at], pixels[at + 1], pixels[at + 2]) = (original[at], original[at + 1], original[at + 2]);
            }

            for (var x = 0; x < w; x++)
            {
                for (int y = 0, run = 0; y <= h; y++)
                {
                    if (y < h && ink[y * w + x])
                    {
                        run++;
                        continue;
                    }

                    // A run is a rule where the ink beside it ends within a few pixels: thin, unlike a picture's dark area.
                    if (run >= tall && Thin(ink, w, x, y - run / 2, 1, 0, thin))
                    {
                        for (var back = y - run; back < y; back++)
                        {
                            Keep(x, back);
                        }
                    }

                    run = 0;
                }
            }

            for (var y = 0; y < h; y++)
            {
                for (int x = 0, run = 0; x <= w; x++)
                {
                    if (x < w && ink[y * w + x])
                    {
                        run++;
                        continue;
                    }

                    if (run >= wide && Thin(ink, w, x - run / 2, y, 0, 1, Math.Max(3, h / 200)))
                    {
                        for (var back = x - run; back < x; back++)
                        {
                            Keep(back, y);
                        }
                    }

                    run = 0;
                }
            }
        }

        // Whether the ink at a point is at most this many pixels thick across the run (dx, dy is the direction across).
        private static bool Thin(bool[] ink, int w, int x, int y, int dx, int dy, int limit)
        {
            var h = ink.Length / w;
            var thickness = 1;
            foreach (var step in new[] { -1, 1 })
            {
                for (var (cx, cy) = (x + dx * step, y + dy * step); cx >= 0 && cy >= 0 && cx < w && cy < h && ink[cy * w + cx]; (cx, cy) = (cx + dx * step, cy + dy * step))
                {
                    if (++thickness > limit)
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        // A box the recognizer drew may end inside the first or last letter of large type: it reaches on sideways while ink joins on, at most a line's height,
        // and never into a rule (a column inked over the whole height of the box that runs on above and below it, unlike a letter's stem).
        private static SKRectI Reach(byte[] pixels, int w, int h, SKRectI box)
        {
            var paper = PdfScanAnalysis.Ring(pixels, w, h, box);
            bool Inked(int x, int y) => y >= 0 && y < h && (y * w + x) * 4 is var index
                && Math.Abs(pixels[index] - paper[0]) + Math.Abs(pixels[index + 1] - paper[1]) + Math.Abs(pixels[index + 2] - paper[2]) > 150;
            int Ink(int x)
            {
                var count = 0;
                for (var y = box.Top; y <= box.Bottom; y++)
                {
                    count += Inked(x, y) ? 1 : 0;
                }

                return count;
            }

            bool Rule(int x) => Ink(x) > box.Height * 0.8 && Inked(x, box.Top - box.Height / 3) && Inked(x, box.Bottom + box.Height / 3);

            var (gap, limit) = (Math.Max(2, box.Height / 4), box.Height);
            int Edge(int from, int step)
            {
                var (edge, blank) = (from, 0);
                for (var x = from + step; x >= 0 && x < w && Math.Abs(x - from) <= limit && blank < gap; x += step)
                {
                    if (Rule(x))
                    {
                        break;
                    }

                    if (Ink(x) > 0)
                    {
                        (edge, blank) = (x, 0);
                    }
                    else
                    {
                        blank++;
                    }
                }

                return edge;
            }

            return new SKRectI(Edge(box.Left, -1), box.Top, Edge(box.Right, 1), box.Bottom);
        }

        // Words without their places share the line by their letters.
        private static double[] Spread(RecognizedLine line, string[] texts)
        {
            var letters = texts.Sum(text => text.Length + 1.0);
            var spans = new double[texts.Length * 2];
            var left = line.Left;
            for (var index = 0; index < texts.Length; index++)
            {
                var width = (line.Right - line.Left) * (texts[index].Length + 1) / letters;
                (spans[index * 2], spans[index * 2 + 1]) = (left, left + width * texts[index].Length / (texts[index].Length + 1));
                left += width;
            }

            return spans;
        }

        // Single words measure noisily: words of one look in a line share their measurements. A new colour or a jump in stroke weight (the bold "Rule No. 4:"
        // before the text) starts another look. A word's own height is the least reliable part, so stems are set against the size of the words of its colour.
        private static void Looks(Line line)
        {
            var measured = line.Words.Where(word => word.Style is not null).ToList();
            foreach (var word in measured)
            {
                var kin = measured.Where(other => other.Style!.Ink.Distance(word.Style!.Ink) < 120 && other.Text.Length >= 3).ToList();
                if (kin.Count > 0)
                {
                    word.Style!.Ratio = word.Style.Stem / Mid(kin, style => style.Size);
                }
            }

            var looks = new List<List<Word>>();
            foreach (var word in measured)
            {
                var look = looks.LastOrDefault();
                if (look is null || look[^1].Style!.Ink.Distance(word.Style!.Ink) >= 90
                    || (word.Text.Count(char.IsLetter) >= 3 && (Math.Abs(Mid(look, style => style.Ratio) - word.Style.Ratio) > 0.06 || Math.Abs(Mid(look, style => style.Contrast) - word.Style.Contrast) > 0.2)))
                {
                    looks.Add([word]);
                }
                else
                {
                    look.Add(word);
                }
            }

            foreach (var look in looks)
            {
                var shared = new Style
                {
                    Size = Mid(look, style => style.Size), Ratio = Mid(look, style => style.Ratio), Contrast = Mid(look, style => style.Contrast), Slant = Mid(look, style => style.Slant),
                    Foot = Mid(look, style => style.Foot), Advance = Mid(look, style => style.Advance), Ink = look.MaxBy(word => word.Text.Length)!.Style!.Ink
                };
                foreach (var word in look)
                {
                    word.Style = shared;
                }
            }

            line.Ink = line.Words.LastOrDefault(word => word.Style is not null)?.Style!.Ink ?? new Rgb(0, 0, 0);
        }

        // A line joins the block right above it in its column (columns side by side are read line by line across) at a steady spacing, in nearly one size and
        // colour; an indented line opens a new block.
        private static bool Joins(Block block, Line line)
        {
            var previous = block.Last;
            var size = Size(previous, first: false);
            return line.Box.Top >= previous.Box.Top
                && line.Box.Top - previous.Box.Bottom < previous.Box.Height * 0.6
                && line.Box.Left < previous.Box.Right && line.Box.Right > previous.Box.Left
                && Math.Abs(Size(line, first: true) - size) < size * 0.4
                && Math.Abs(line.Left - block.Margin) < size * 4
                && line.Ink.Distance(previous.Ink) < 120
                && !(line.Left - block.Margin > size * 0.6 && line.Left - block.Margin < size * 4);
        }

        // Lines in one column at a steady spacing make one block; a short single line of a picture is its label, labels in one row are a series.
        private static List<PdfSmartBlock> Blocks(List<Line> lines, int w, int h)
        {
            var body = lines.Select(line => (double)line.Box.Height).Order().ElementAt(lines.Count / 2);
            var blocks = new List<Block>();
            foreach (var line in lines.OrderBy(line => line.Box.Top).ThenBy(line => line.Box.Left))
            {
                // The most recent block whose last line stands right above this one.
                var block = blocks.LastOrDefault(candidate => Joins(candidate, line));
                if (block is null)
                {
                    block = new Block(line);
                    blocks.Add(block);
                }
                else
                {
                    block.Area = SKRectI.Union(block.Area, line.Box);
                    block.Margin = Math.Min(block.Margin, line.Left);
                    block.Last = line;
                    block.IsLabel = false;
                }

                foreach (var word in line.Words)
                {
                    var style = word.Style ?? new Style { Size = line.Box.Height * 0.7 };
                    var last = block.Runs.LastOrDefault();
                    var hyphen = last is not null && last.Text.EndsWith('-') && word == line.Words[0];
                    if (last is not null && last.Serif == style.Serif && last.Italic == style.Italic && last.Ink.Distance(style.Ink) < 90 && Math.Abs(last.Weight - style.Weight) <= 100
                        && Math.Abs(last.Size - style.Size / h) < 0.2 * last.Size)
                    {
                        last.Text = hyphen ? last.Text[..^1] + word.Text : last.Text + " " + word.Text;
                    }
                    else
                    {
                        if (hyphen)
                        {
                            last!.Text = last.Text[..^1];
                        }

                        block.Runs.Add(new Run
                        {
                            Text = (block.Runs.Count == 0 || hyphen ? "" : " ") + word.Text, Serif = style.Serif, Weight = style.Weight, Italic = style.Italic, Stretch = style.Stretch,
                            Size = style.Size / h, Ink = style.Ink
                        });
                    }
                }

                block.Lines++;
            }

            foreach (var each in blocks)
            {
                Settle(each.Runs);
                each.IsLabel &= string.Concat(each.Runs.Select(run => run.Text)).Split(' ', StringSplitOptions.RemoveEmptyEntries).Length <= 3;
            }

            // Labels side by side are one series (ALWAYS / SOMETIMES / NEVER): one word alone measures poorly, together they agree on face, weight and size.
            var series = new Dictionary<Block, int>();
            foreach (var label in blocks.Where(each => each.IsLabel))
            {
                var row = blocks.Where(other => other.IsLabel && Math.Abs(other.Area.MidY - label.Area.MidY) < body / 2 && other.Ink.Distance(label.Ink) < 90).ToList();
                var runs = row.SelectMany(other => other.Runs).ToList();
                var weights = runs.SelectMany(run => Enumerable.Repeat(run.Weight, Letters(run))).Order().ToList();
                var sizes = runs.Select(run => run.Size).Order().ToList();
                var serif = runs.Where(run => run.Serif).Sum(Letters) > runs.Where(run => !run.Serif).Sum(Letters);
                foreach (var run in label.Runs)
                {
                    (run.Serif, run.Weight, run.Size) = (serif, weights[(weights.Count - 1) / 2], sizes[(sizes.Count - 1) / 2]);
                }

                series[label] = row.Min(blocks.IndexOf) + 1;
            }

            // A few words alone (labels, capitals with wide bars) cannot tell serif from sans: a longer text of their colour and nearly their weight on the page can.
            var longer = blocks.SelectMany(each => each.Runs).Where(run => run.Text.Count(char.IsLetter) >= 12).ToList();
            foreach (var run in blocks.Where(each => each.Runs.Sum(Letters) < 12).SelectMany(each => each.Runs))
            {
                if (longer.Where(other => other.Ink.Distance(run.Ink) < 90 && Math.Abs(other.Weight - run.Weight) <= 200).MaxBy(other => other.Text.Length) is { } kin)
                {
                    run.Serif = kin.Serif;
                }
            }

            // Text that flows around a picture runs on in the block right below, set further in or out: it continues the sentence, so it is translated with it.
            var follows = blocks.Select(each => blocks.FindLastIndex(blocks.IndexOf(each), other => other != each && Continues(other, each))).ToList();
            return [.. blocks.Select((each, index) =>
            {
                var text = string.Concat(each.Runs.Select(run => run.Text)).Trim();
                var (left, top, width, height) = ((double)each.Area.Left / w, (double)each.Area.Top / h, (double)each.Area.Width / w, (double)each.Area.Height / h);
                return new PdfSmartBlock(Math.Round(left, 4), Math.Round(top, 4), Math.Round(width, 4), Math.Round(height, 4), each.Lines,
                    Math.Round(each.Runs.MaxBy(run => run.Text.Length)?.Size ?? height / each.Lines, 5), series.GetValueOrDefault(each), each.IsLabel,
                    // A short line at the very top or bottom is a running head or a page number.
                    each.Lines == 1 && text.Length < 40 && (top < 0.07 || top + height > 0.93),
                    [.. each.Runs.Select(run => new PdfSmartRun(run.Text, run.Serif, run.Weight, run.Italic, run.Stretch, Math.Round(run.Size, 5), run.Ink.Hex))])
                {
                    Follows = follows[index]
                };
            })];
        }

        // A block continues another whose sentence is not over where it starts right below it, overlapping it sideways, in its colour and nearly its size.
        private static bool Continues(Block before, Block after)
        {
            var text = string.Concat(before.Runs.Select(run => run.Text)).TrimEnd();
            var line = before.Area.Height / (double)Math.Max(1, before.Lines);
            var (one, other) = (before.Runs.MaxBy(run => run.Text.Length)?.Size ?? 0, after.Runs.MaxBy(run => run.Text.Length)?.Size ?? 0);
            // A word broken at the line's end or a sentence going on in small letters runs on whatever the measured size or a label's look says.
            var next = string.Concat(after.Runs.Select(run => run.Text)).TrimStart();
            var runsOn = text.EndsWith('-') || next.Length > 0 && char.IsLower(next[0]);
            return text.Length > 0 && !".!?:”\"".Contains(text[^1])
                && after.Area.Top >= before.Area.Bottom - line * 0.5 && after.Area.Top - before.Area.Bottom < line * 0.8
                && after.Area.Left < before.Area.Right && after.Area.Right > before.Area.Left && before.Ink.Distance(after.Ink) < 120
                && (runsOn || !before.IsLabel && !after.IsLabel && Math.Abs(one - other) <= Math.Max(one, other) * 0.25);
        }

        private static int Letters(Run run) => Math.Max(1, run.Text.Count(char.IsLetter));

        // The size of the text at a line's start or end.
        private static double Size(Line line, bool first)
        {
            var words = line.Words.Where(word => word.Style is not null).ToList();
            return words.Count == 0 ? line.Box.Height / 2.0 : (first ? words[0] : words[^1]).Style!.Size;
        }

        // A block's runs measured line by line disagree a little: a few words take the look of the longer run of their colour beside them, runs of one colour
        // and nearly one weight agree on serif and weight by their letters, and equal runs merge.
        private static void Settle(List<Run> runs)
        {
            bool Kin(Run one, Run other) => one.Ink.Distance(other.Ink) < 90;
            for (var index = 0; index < runs.Count; index++)
            {
                if (runs[index].Text.Count(char.IsLetter) >= 12)
                {
                    continue;
                }

                var near = new[] { index > 0 ? runs[index - 1] : null, index + 1 < runs.Count ? runs[index + 1] : null }
                    .Where(run => run is not null && Kin(run, runs[index]) && Letters(run) > Letters(runs[index])).MaxBy(run => Letters(run!));
                if (near is not null && Math.Abs(near.Weight - runs[index].Weight) <= 200)
                {
                    (runs[index].Serif, runs[index].Weight, runs[index].Italic) = (near.Serif, near.Weight, near.Italic);
                }
            }

            foreach (var run in runs)
            {
                var group = runs.Where(other => Kin(other, run) && Math.Abs(other.Weight - run.Weight) <= 200 && other.Italic == run.Italic).ToList();
                var weights = group.OrderBy(other => other.Weight).SelectMany(other => Enumerable.Repeat(other.Weight, Letters(other))).ToList();
                (run.Serif, run.Weight) = (group.Where(other => other.Serif).Sum(Letters) >= group.Where(other => !other.Serif).Sum(Letters), weights[weights.Count / 2]);
            }

            // Runs of one look in a block are one size: the measurement of a line is noisy, the block's text is not.
            var sizes = runs.ToDictionary(run => run, run => runs.Where(other => Kin(other, run) && other.Serif == run.Serif && other.Weight == run.Weight && other.Italic == run.Italic)
                .OrderBy(other => other.Size).SelectMany(other => Enumerable.Repeat(other.Size, Letters(other))).ToList());
            foreach (var run in runs)
            {
                run.Size = sizes[run][sizes[run].Count / 2];
            }

            for (var index = runs.Count - 1; index > 0; index--)
            {
                var (one, other) = (runs[index - 1], runs[index]);
                if (one.Serif == other.Serif && one.Weight == other.Weight && one.Italic == other.Italic && Kin(one, other) && Math.Abs(one.Size - other.Size) <= 0.2 * one.Size)
                {
                    one.Text += other.Text;
                    runs.RemoveAt(index);
                }
            }
        }

        // A word's look from its ink: size from cap or x-height, weight from the stems, serif from the stroke contrast or the feet at the baseline, slant from
        // the shear that stands the letters upright best, width from the advance per letter, colour from the darkest ink.
        private static Style? Measure(byte[] pixels, int pageWidth, int pageHeight, SKRectI box, int[] paper, int characters)
        {
            var (w, h) = (box.Width, box.Height);
            if (w < 3 || h < 3)
            {
                return null;
            }

            var distance = new int[w * h];
            var most = 0;
            for (var y = 0; y < h; y++)
            {
                for (var x = 0; x < w; x++)
                {
                    var index = ((box.Top + y) * pageWidth + box.Left + x) * 4;
                    var value = Math.Abs(pixels[index] - paper[0]) + Math.Abs(pixels[index + 1] - paper[1]) + Math.Abs(pixels[index + 2] - paper[2]);
                    distance[y * w + x] = value;
                    most = Math.Max(most, value);
                }
            }

            if (most < 90)
            {
                return null;
            }

            // Only letters of this line: ink connected to the box's middle rows; what a generous box catches of the lines above and below is left out.
            var limit = Math.Max(90, most * 0.45);
            var ink = new bool[w * h];
            var queue = new Queue<int>();
            for (var y = (int)(h * 0.4); y <= (int)(h * 0.6); y++)
            {
                for (var x = 0; x < w; x++)
                {
                    if (distance[y * w + x] > limit && !ink[y * w + x])
                    {
                        ink[y * w + x] = true;
                        queue.Enqueue(y * w + x);
                    }
                }
            }

            while (queue.Count > 0)
            {
                var at = queue.Dequeue();
                var (x, y) = (at % w, at / w);
                foreach (var (nx, ny) in new[] { (x - 1, y), (x + 1, y), (x, y - 1), (x, y + 1) })
                {
                    if (nx >= 0 && ny >= 0 && nx < w && ny < h && !ink[ny * w + nx] && distance[ny * w + nx] > limit)
                    {
                        ink[ny * w + nx] = true;
                        queue.Enqueue(ny * w + nx);
                    }
                }
            }

            var rows = new int[h];
            var (left, right) = (w, -1);
            for (var y = 0; y < h; y++)
            {
                for (var x = 0; x < w; x++)
                {
                    if (ink[y * w + x])
                    {
                        rows[y]++;
                        (left, right) = (Math.Min(left, x), Math.Max(right, x));
                    }
                }
            }

            if (right < 0)
            {
                return null;
            }

            // The x-height band is the dense middle; a rule across the box is no letter.
            var full = (right - left + 1) * 0.85;
            var peak = rows.Where(count => count < full).DefaultIfEmpty(rows.Max()).Max();
            if (peak == 0)
            {
                return null;
            }

            var xTop = Array.FindIndex(rows, count => count >= peak * 0.4 && count < full);
            var baseline = Array.FindLastIndex(rows, count => count >= peak * 0.35 && count < full);
            if (xTop < 0 || baseline <= xTop)
            {
                return null;
            }

            var top = xTop;
            while (top > 0 && rows[top - 1] > 0)
            {
                top--;
            }

            double cap = baseline - top + 1, xHeight = Math.Max(1, baseline - xTop + 1);
            var size = Math.Max(cap, xHeight * 1.45);

            // Stems: horizontal ink runs through the middle of the x-height; hairlines: short vertical runs (bars, bowls, serifs); feet: runs at the baseline.
            var stems = Runs(ink, w, left, right, xTop + (int)(xHeight / 4), baseline - (int)(xHeight / 4), horizontal: true, int.MaxValue);
            var hairs = Runs(ink, w, left, right, top, baseline, horizontal: false, (int)(xHeight * 0.45));
            var stem = Middle(stems);
            var feet = Runs(ink, w, left, right, Math.Max(0, baseline - (int)Math.Ceiling(stem)), baseline, horizontal: true, int.MaxValue);

            // Slant: the shear whose column profile is the most peaked.
            var (slant, best) = (0.0, double.MinValue);
            for (var shear = -0.1; shear <= 0.41; shear += 0.05)
            {
                var columns = new double[w + h];
                for (var y = top; y <= baseline; y++)
                {
                    for (var x = left; x <= right; x++)
                    {
                        if (ink[y * w + x])
                        {
                            columns[Math.Clamp((int)(x - shear * (baseline - y)) + h / 2, 0, w + h - 1)]++;
                        }
                    }
                }

                var score = columns.Sum(value => value * value);
                if (score > best)
                {
                    (best, slant) = (score, shear);
                }
            }

            var (red, green, blue, dark) = (0L, 0L, 0L, 0);
            for (var y = 0; y < h; y++)
            {
                for (var x = 0; x < w; x++)
                {
                    if (distance[y * w + x] >= most * 0.7)
                    {
                        var index = ((box.Top + y) * pageWidth + box.Left + x) * 4;
                        (red, green, blue, dark) = (red + pixels[index], green + pixels[index + 1], blue + pixels[index + 2], dark + 1);
                    }
                }
            }

            return new Style
            {
                Size = size, Left = box.Left + left, Stem = stem, Ratio = stem / size, Contrast = Middle(hairs) / stem, Slant = slant, Foot = Middle(feet) / stem,
                Advance = (right - left + 1.0) / Math.Max(1, characters) / size, Ink = new Rgb((int)(red / dark), (int)(green / dark), (int)(blue / dark))
            };
        }

        private static List<int> Runs(bool[] ink, int w, int left, int right, int from, int to, bool horizontal, int shorterThan)
        {
            var runs = new List<int>();
            if (horizontal)
            {
                for (var y = from; y <= to; y++)
                {
                    for (int x = left, run = 0; x <= right + 1; x++)
                    {
                        if (x <= right && ink[y * w + x])
                        {
                            run++;
                        }
                        else
                        {
                            if (run > 0 && run < shorterThan)
                            {
                                runs.Add(run);
                            }

                            run = 0;
                        }
                    }
                }
            }
            else
            {
                for (var x = left; x <= right; x++)
                {
                    for (int y = from, run = 0; y <= to + 1; y++)
                    {
                        if (y <= to && ink[y * w + x])
                        {
                            run++;
                        }
                        else
                        {
                            if (run > 0 && run < shorterThan)
                            {
                                runs.Add(run);
                            }

                            run = 0;
                        }
                    }
                }
            }

            return runs;
        }

        // Stems are only a few pixels wide: the mean of the middle half of the runs is finer than their median.
        private static double Middle(List<int> values) => values.Count == 0 ? 1 : values.Order().Skip(values.Count / 4).Take(Math.Max(1, values.Count / 2)).Average();

        private static double Mid(List<Word> words, Func<Style, double> value) => words.Select(word => value(word.Style!)).Order().ElementAt((words.Count - 1) / 2);

        private static SKRectI Grow(SKRectI box, double by) => new((int)(box.Left - by), (int)(box.Top - by), (int)Math.Ceiling(box.Right + by), (int)Math.Ceiling(box.Bottom + by));

        private static SKRectI Clamp(SKRectI box, int w, int h) => new(Math.Clamp(box.Left, 0, w - 1), Math.Clamp(box.Top, 0, h - 1), Math.Clamp(box.Right, 0, w - 1), Math.Clamp(box.Bottom, 0, h - 1));

        private static byte[] Encode(byte[] pixels, int w, int h)
        {
            using var page = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul));
            pixels.CopyTo(page.GetPixelSpan());
            var scale = Math.Min(1.0, (double)ImageSide / Math.Max(w, h));
            using var small = scale < 1
                ? page.Resize(new SKImageInfo(Math.Max(1, (int)Math.Round(w * scale)), Math.Max(1, (int)Math.Round(h * scale)), SKColorType.Rgba8888, SKAlphaType.Premul), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear))
                    ?? throw new InvalidDataException("The page cannot be scaled.")
                : page.Copy();
            using var image = SKImage.FromBitmap(small);
            using var data = image.Encode(SKEncodedImageFormat.Jpeg, ImageQuality);
            return data.ToArray();
        }

        private readonly record struct Rgb(int Red, int Green, int Blue)
        {
            public int Distance(Rgb other) => Math.Abs(Red - other.Red) + Math.Abs(Green - other.Green) + Math.Abs(Blue - other.Blue);

            public string Hex => $"#{Red:x2}{Green:x2}{Blue:x2}";
        }

        private sealed class Style
        {
            public double Size { get; set; }
            public double Left { get; set; }
            public double Stem { get; set; } = 1;
            public double Ratio { get; set; } = 0.13;
            public double Contrast { get; set; } = 0.5;
            public double Slant { get; set; }
            public double Foot { get; set; } = 1.3;
            public double Advance { get; set; } = 0.62;
            public Rgb Ink { get; set; }

            // Serif faces have thin hairlines against their stems; blurry small print shows no hairlines, but its stems still stand on wider feet.
            public bool Serif => Contrast < 0.6 || Foot >= 1.16;

            public int Weight => (int)Math.Clamp(Math.Round((400 + (Ratio - 0.12) * 4000) / 100) * 100, 300, 900);
            public bool Italic => Slant >= 0.15;
            public int Stretch => (int)Math.Clamp(Math.Round(Advance / 0.62 * 100 / 5) * 5, 75, 125);
        }

        private sealed class Word(string text, double left)
        {
            public string Text { get; } = text;
            public double Left { get; } = left;
            public Style? Style { get; set; }
        }

        private sealed class Line(SKRectI box, bool isLabel)
        {
            public SKRectI Box { get; } = box;
            public bool IsLabel { get; } = isLabel;
            public List<Word> Words { get; } = [];
            public Rgb Ink { get; set; }
            public double Left => Words.Count == 0 ? Box.Left : Words[0].Left;
        }

        private sealed class Run
        {
            public string Text { get; set; } = "";
            public bool Serif { get; set; }
            public int Weight { get; set; }
            public bool Italic { get; set; }
            public int Stretch { get; set; }
            public double Size { get; set; }
            public Rgb Ink { get; set; }
        }

        private sealed class Block(Line first)
        {
            public SKRectI Area { get; set; } = first.Box;
            public double Margin { get; set; } = first.Left;
            public Line Last { get; set; } = first;
            public bool IsLabel { get; set; } = first.IsLabel;
            public int Lines { get; set; }
            public List<Run> Runs { get; } = [];
            public Rgb Ink => Runs.Count == 0 ? default : Runs.MaxBy(run => run.Text.Length)!.Ink;
        }
    }

    // A page set anew: the text-free page as a JPEG (its width against its height) and its blocks.
    internal sealed record PdfSmartPage(int Number, byte[] Image, double Ratio, IReadOnlyList<PdfSmartBlock> Blocks);

    // A block in fractions of the page, its lines, the size of its main text against the page's height, the series of labels it belongs to (0 for none),
    // whether it is a label or a running head or page number, and its runs.
    internal sealed record PdfSmartBlock(double Left, double Top, double Width, double Height, int Lines, double Base, int Series, bool IsLabel, bool IsRunning, IReadOnlyList<PdfSmartRun> Runs)
    {
        // The block whose sentence this one continues (text flowing around a picture), -1 for none.
        public int Follows { get; init; } = -1;
    }

    // Text in one look: serif or sans, weight, italic, width in percent, size against the page's height and colour.
    internal sealed record PdfSmartRun(string Text, bool Serif, int Weight, bool Italic, int Stretch, double Size, string Ink);
}
