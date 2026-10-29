using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SkiaSharp;

namespace Jularr.Integrations.Logic.Ocr
{
    // PP-OCRv5 on the CPU with a bounded share of its cores: the detection model marks text lines, the recognition model reads each. Book scans stand upright, so a line is an axis-aligned box.
    public sealed class OcrEngine : IDisposable
    {
        // The detection input stays within this side; book text keeps enough pixels to be found.
        private const int DetectionSide = 1600;
        private const float PixelThreshold = 0.3f;
        private const float BoxThreshold = 0.6f;
        private const float UnclipRatio = 1.6f;
        private const int LineHeight = 48;
        private const int MaximumLineWidth = 3200;
        private const int MaximumPixels = 60_000_000;

        private static readonly float[] Mean = [0.485f, 0.456f, 0.406f];
        private static readonly float[] Deviation = [0.229f, 0.224f, 0.225f];

        private readonly InferenceSession _detection;
        private readonly InferenceSession _recognition;
        private readonly string[] _characters;

        public OcrEngine(string detectionModel, string recognitionModel)
        {
            using var options = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL, IntraOpNumThreads = Math.Clamp(Environment.ProcessorCount / 4, 1, 4) };
            _detection = new InferenceSession(detectionModel, options);
            _recognition = new InferenceSession(recognitionModel, options);

            // The recognition model carries its characters, one per line; class 0 is the CTC blank, and the space is the empty last one or follows the list.
            _characters = _recognition.ModelMetadata.CustomMetadataMap.TryGetValue("character", out var characters)
                ? characters.Split('\n')
                : throw new InvalidDataException("The recognition model names no characters.");
        }

        // Text lines of a page picture with their boxes in its pixels, top to bottom.
        public OcrPage Read(byte[] picture)
        {
            using var decoded = SKBitmap.Decode(picture) ?? throw new InvalidDataException("The page picture cannot be decoded.");
            return Read(decoded);
        }

        // Text lines of an upright page with their boxes in its pixels and the place of every character, top to bottom.
        public OcrPage Read(SKBitmap picture)
        {
            if ((long)picture.Width * picture.Height > MaximumPixels)
            {
                throw new InvalidDataException("The page picture is too large.");
            }

            // The pixels in red, green, blue order, converted from whatever order the picture has.
            using var page = new SKBitmap(new SKImageInfo(picture.Width, picture.Height, SKColorType.Rgba8888, SKAlphaType.Premul));
            using (var pixmap = picture.PeekPixels())
            {
                if (!pixmap.ReadPixels(page.Info, page.GetPixels(), page.RowBytes))
                {
                    throw new InvalidDataException("The page picture cannot be read.");
                }
            }
            var lines = new List<OcrLine>();
            foreach (var region in Join(Detect(page)))
            {
                // A region parted at a gutter is read both ways; the surer reading wins (a line over a photograph has no gutter, only a mottled ground).
                var read = new List<(SKRectI Box, (string Text, float Confidence, IReadOnlyList<OcrCharacter> Characters) Read)> { (region, Recognize(page, region)) };
                if (Split(page, [region]) is { Count: > 1 } parts)
                {
                    var apart = parts.Select(part => (Box: part, Read: Recognize(page, part))).ToList();
                    var letters = apart.Sum(each => each.Read.Text.Trim().Length);
                    if (letters > 0 && apart.All(each => each.Read.Confidence >= SurePart)
                        && apart.Sum(each => each.Read.Confidence * each.Read.Text.Trim().Length) / letters >= read[0].Read.Confidence)
                    {
                        read = apart;
                    }
                }

                foreach (var (box, (text, confidence, characters)) in read)
                {
                    if (text.Trim() is { Length: > 0 } trimmed)
                    {
                        lines.Add(new OcrLine(box.Left, box.Top, box.Right, box.Bottom, trimmed, confidence) { Characters = characters });
                    }
                }
            }

            return new OcrPage(page.Width, page.Height, [.. lines.OrderBy(line => line.Top).ThenBy(line => line.Left)]);
        }

        // Large type is detected word by word: pieces side by side on the same rows become their line again, so it is read in one go and in order. Only pieces
        // a word space apart (against the smaller one's height) join; a wider gap (the gutter to another column, small type beside large) keeps them apart.
        internal static List<SKRectI> Join(List<SKRectI> boxes)
        {
            var lines = boxes.OrderBy(box => box.Left).ToList();
            for (var merged = true; merged;)
            {
                merged = false;
                for (var first = 0; first < lines.Count && !merged; first++)
                {
                    for (var second = first + 1; second < lines.Count && !merged; second++)
                    {
                        var (one, other) = (lines[first], lines[second]);
                        var overlap = Math.Min(one.Bottom, other.Bottom) - Math.Max(one.Top, other.Top);
                        var gap = Math.Max(one.Left, other.Left) - Math.Min(one.Right, other.Right);
                        var low = Math.Min(one.Height, other.Height);
                        if (overlap < low * 0.7f || gap > low * 0.6f)
                        {
                            continue;
                        }

                        lines[first] = SKRectI.Union(one, other);
                        lines.RemoveAt(second);
                        merged = true;
                    }
                }
            }

            return lines;
        }

        // Every part of a region read apart must be read this surely, or the region is read whole.
        private const float SurePart = 0.75f;

        // A detected region may run over the gutter between two columns (a rule in it joins them): a stretch without ink half again as wide as the type is
        // high, or half that with a rule in it, parts it, and each part keeps its whole lines, so small type beside large is read at its own size.
        internal static List<SKRectI> Split(SKBitmap page, List<SKRectI> boxes)
        {
            var result = new List<SKRectI>();
            var pixels = page.GetPixelSpan();
            foreach (var box in boxes)
            {
                var (w, h) = (box.Width, box.Height);
                if (w < h * 3 || h < 8)
                {
                    result.Add(box);
                    continue;
                }

                // Ink: far from the region's commonest brightness.
                var light = new int[w * h];
                for (var y = 0; y < h; y++)
                {
                    for (var x = 0; x < w; x++)
                    {
                        var at = ((box.Top + y) * page.Width + box.Left + x) * 4;
                        light[y * w + x] = (pixels[at] * 3 + pixels[at + 1] * 6 + pixels[at + 2]) / 10;
                    }
                }

                var ground = light.Order().ElementAt(light.Length / 2);
                var columns = new int[w];
                for (var index = 0; index < light.Length; index++)
                {
                    columns[index % w] += Math.Abs(light[index] - ground) > 60 ? 1 : 0;
                }

                // The height of the type in a stretch of columns: its rows of dense ink, not the stray ends of a line above or below.
                int Tall(int from, int to)
                {
                    var rows = new int[h];
                    for (var y = 0; y < h; y++)
                    {
                        for (var x = Math.Max(0, from); x < Math.Min(w, to); x++)
                        {
                            rows[y] += Math.Abs(light[y * w + x] - ground) > 60 ? 1 : 0;
                        }
                    }

                    var dense = rows.Max() * 0.15;
                    var top = Array.FindIndex(rows, count => count > dense);
                    return top < 0 ? 0 : Array.FindLastIndex(rows, count => count > dense) - top + 1;
                }

                // The detector grows its regions well beyond the letters: the type's own height is the measure.
                var type = Math.Max(8, Tall(0, w));
                var blank = columns.Select(count => count <= Math.Max(1, h / 30)).ToArray();
                var rule = new bool[w];
                // A rule: a thin stroke over the region's whole height with space on both sides, unlike a letter's stem.
                var (thin, aside) = (Math.Max(3, type / 8), Math.Max(2, type / 5));
                bool Clear(int from, int to) => from >= 0 && to <= w && Enumerable.Range(from, to - from).All(x => columns[x] <= Math.Max(1, h / 30));
                for (int x = 0; x < w;)
                {
                    var end = x;
                    while (end < w && !blank[end]) end++;
                    if (end > x && end - x <= thin && columns[x..end].Max() >= type * 0.4 && Clear(x - aside, x) && Clear(end, end + aside))
                    {
                        for (var each = x; each < end; each++) (blank[each], rule[each]) = (true, true);
                    }

                    x = end == x ? x + 1 : end;
                }

                // A gutter: empty half again wider than the type is high (wider than a spaced word gap), or with a rule in it, half that.
                var cuts = new List<(int From, int To)>();
                for (int x = 0; x < w;)
                {
                    var end = x;
                    while (end < w && blank[end]) end++;
                    if (x > 0 && end < w && (end - x >= type * 1.5 || end - x >= type * 0.5 && rule[x..end].Any(each => each)))
                    {
                        cuts.Add((x, end));
                    }

                    x = end == x ? x + 1 : end;
                }

                if (cuts.Count == 0)
                {
                    result.Add(box);
                    continue;
                }

                var from = 0;
                foreach (var (start, end) in cuts.Append((w, w)))
                {
                    // The part's own line: of its bands of inked rows (small type beside large may show the ends of the lines above and below), the one
                    // nearest the region's middle.
                    var inked = new bool[h];
                    for (var y = 0; y < h; y++)
                    {
                        var count = 0;
                        for (var x = from; x < start; x++)
                        {
                            count += Math.Abs(light[y * w + x] - ground) > 60 ? 1 : 0;
                        }

                        inked[y] = count > Math.Max(1, (start - from) / 200);
                    }

                    // Every whole line of the part (a band clear of the region's edges, where lines above and below show their ends); else the band nearest
                    // the middle.
                    var bands = new List<(int Top, int Bottom)>();
                    for (var y = 0; y < h;)
                    {
                        if (!inked[y])
                        {
                            y++;
                            continue;
                        }

                        var last = y;
                        while (last + 1 < h && (inked[last + 1] || last + 2 < h && inked[last + 2])) last++;
                        if (last - y >= 4)
                        {
                            bands.Add((y, last));
                        }

                        y = last + 1;
                    }

                    var whole = bands.Where(band => band.Top > 1 && band.Bottom < h - 2).ToList();
                    if (whole.Count == 0 && bands.Count > 0)
                    {
                        whole.Add(bands.MinBy(band => band.Top > h / 2 ? band.Top - h / 2 : band.Bottom < h / 2 ? h / 2 - band.Bottom : 0));
                    }

                    foreach (var (top, bottom) in whole)
                    {
                        var margin = Math.Max(2, (bottom - top) / 8);
                        result.Add(new SKRectI(box.Left + from, Math.Max(box.Top, box.Top + top - margin), box.Left + start, Math.Min(box.Bottom, box.Top + bottom + margin + 1)));
                    }

                    from = end;
                }
            }

            return result;
        }

        private List<SKRectI> Detect(SKBitmap page)
        {
            var scale = Math.Min(1f, (float)DetectionSide / Math.Max(page.Width, page.Height));
            var width = Math.Max(32, (int)Math.Round(page.Width * scale / 32) * 32);
            var height = Math.Max(32, (int)Math.Round(page.Height * scale / 32) * 32);
            using var resized = page.Resize(new SKImageInfo(width, height, SKColorType.Rgba8888), new SKSamplingOptions(SKFilterMode.Linear));

            // Channels in blue, green, red order, normalized like the models were trained.
            var input = new DenseTensor<float>([1, 3, height, width]);
            var values = input.Buffer.Span;
            var pixels = resized.GetPixelSpan();
            var plane = height * width;
            for (var index = 0; index < plane; index++)
            {
                values[index] = (pixels[index * 4 + 2] / 255f - Mean[0]) / Deviation[0];
                values[plane + index] = (pixels[index * 4 + 1] / 255f - Mean[1]) / Deviation[1];
                values[2 * plane + index] = (pixels[index * 4] / 255f - Mean[2]) / Deviation[2];
            }

            using var results = _detection.Run([NamedOnnxValue.CreateFromTensor(_detection.InputMetadata.Keys.First(), input)]);
            var map = results.First().AsEnumerable<float>().ToArray();

            // Connected regions above the pixel threshold whose mean score is high enough are lines, grown back to their full size.
            var seen = new bool[height * width];
            var boxes = new List<SKRectI>();
            var queue = new Queue<int>();
            var (scaleX, scaleY) = ((float)page.Width / width, (float)page.Height / height);
            for (var start = 0; start < seen.Length; start++)
            {
                if (seen[start] || map[start] <= PixelThreshold)
                {
                    continue;
                }

                int left = int.MaxValue, top = int.MaxValue, right = 0, bottom = 0, count = 0;
                float score = 0;
                seen[start] = true;
                queue.Enqueue(start);
                while (queue.Count > 0)
                {
                    var index = queue.Dequeue();
                    var (y, x) = (index / width, index % width);
                    (left, top, right, bottom) = (Math.Min(left, x), Math.Min(top, y), Math.Max(right, x), Math.Max(bottom, y));
                    score += map[index];
                    count++;
                    foreach (var next in (ReadOnlySpan<int>)[index - 1, index + 1, index - width, index + width])
                    {
                        if (next >= 0 && next < seen.Length && !seen[next] && (next / width == y || next % width == x) && map[next] > PixelThreshold)
                        {
                            seen[next] = true;
                            queue.Enqueue(next);
                        }
                    }
                }

                var (boxWidth, boxHeight) = (right - left + 1, bottom - top + 1);
                if (Math.Min(boxWidth, boxHeight) < 3 || score / count < BoxThreshold)
                {
                    continue;
                }

                var grow = boxWidth * boxHeight * UnclipRatio / (2f * (boxWidth + boxHeight));
                boxes.Add(new SKRectI(
                    Math.Clamp((int)((left - grow) * scaleX), 0, page.Width - 1),
                    Math.Clamp((int)((top - grow) * scaleY), 0, page.Height - 1),
                    Math.Clamp((int)Math.Ceiling((right + 1 + grow) * scaleX), 1, page.Width),
                    Math.Clamp((int)Math.Ceiling((bottom + 1 + grow) * scaleY), 1, page.Height)));
            }

            return boxes;
        }

        private (string Text, float Confidence, IReadOnlyList<OcrCharacter> Characters) Recognize(SKBitmap page, SKRectI box)
        {
            if (box.Width < 2 || box.Height < 2)
            {
                return ("", 0, []);
            }

            var width = Math.Clamp((int)Math.Ceiling(LineHeight * (float)box.Width / box.Height), 16, MaximumLineWidth);
            using var crop = new SKBitmap(box.Width, box.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
            if (!page.ExtractSubset(crop, box))
            {
                return ("", 0, []);
            }

            using var line = crop.Resize(new SKImageInfo(width, LineHeight, SKColorType.Rgba8888), new SKSamplingOptions(SKFilterMode.Linear));
            var input = new DenseTensor<float>([1, 3, LineHeight, width]);
            var values = input.Buffer.Span;
            var pixels = line.GetPixelSpan();
            var plane = LineHeight * width;
            for (var index = 0; index < plane; index++)
            {
                values[index] = pixels[index * 4 + 2] / 127.5f - 1;
                values[plane + index] = pixels[index * 4 + 1] / 127.5f - 1;
                values[2 * plane + index] = pixels[index * 4] / 127.5f - 1;
            }

            using var results = _recognition.Run([NamedOnnxValue.CreateFromTensor(_recognition.InputMetadata.Keys.First(), input)]);
            var tensor = results.First().AsTensor<float>();
            var (steps, classes) = (tensor.Dimensions[1], tensor.Dimensions[2]);
            var output = tensor.ToArray();

            // Greedy CTC: the likeliest class per step, blanks and repeats dropped.
            var text = new System.Text.StringBuilder();
            var characters = new List<OcrCharacter>();
            float sum = 0;
            var kept = 0;
            var previous = 0;
            for (var step = 0; step < steps; step++)
            {
                var (best, probability) = (0, float.MinValue);
                for (var index = 0; index < classes; index++)
                {
                    if (output[step * classes + index] > probability)
                    {
                        (best, probability) = (index, output[step * classes + index]);
                    }
                }

                if (best != 0 && best != previous)
                {
                    var character = best - 1 < _characters.Length ? _characters[best - 1] : "";
                    text.Append(character.Length == 0 ? " " : character);

                    // A step's centre in the page: where the character stands, for words of their own.
                    characters.Add(new OcrCharacter(character.Length == 0 ? " " : character, box.Left + (step + 0.5f) / steps * box.Width, probability));
                    sum += probability;
                    kept++;
                }

                previous = best;
            }

            return (text.ToString(), kept == 0 ? 0 : sum / kept, characters);
        }

        public void Dispose()
        {
            _detection.Dispose();
            _recognition.Dispose();
        }
    }

    // A recognized line: its box in the picture's pixels, its text and the mean probability of its characters.
    public sealed record OcrLine(int Left, int Top, int Right, int Bottom, string Text, float Confidence)
    {
        public IReadOnlyList<OcrCharacter> Characters { get; init; } = [];
    }

    // A recognized character, the horizontal centre of where it stands in the picture's pixels and its probability.
    public sealed record OcrCharacter(string Text, float Center, float Probability);

    public sealed record OcrPage(int Width, int Height, IReadOnlyList<OcrLine> Lines);
}
