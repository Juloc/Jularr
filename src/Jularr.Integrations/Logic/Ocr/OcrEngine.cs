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
        public OcrPage Read(byte[] picture) => Read(picture, 0);

        // A page the PDF shows turned clockwise by this many degrees is read upright; the boxes are in the upright picture's pixels.
        public OcrPage Read(byte[] picture, int rotation)
        {
            using var decoded = SKBitmap.Decode(picture) ?? throw new InvalidDataException("The page picture cannot be decoded.");
            if ((long)decoded.Width * decoded.Height > MaximumPixels)
            {
                throw new InvalidDataException("The page picture is too large.");
            }

            using var copy = decoded.ColorType == SKColorType.Rgba8888 ? decoded.Copy() : decoded.Copy(SKColorType.Rgba8888);
            using var turned = rotation % 360 == 0 ? null : Turn(copy, ((rotation % 360) + 360) % 360);
            var page = turned ?? copy;
            var lines = new List<OcrLine>();
            foreach (var box in Detect(page))
            {
                var (text, confidence) = Recognize(page, box);
                if (text.Trim() is { Length: > 0 } trimmed)
                {
                    lines.Add(new OcrLine(box.Left, box.Top, box.Right, box.Bottom, trimmed, confidence));
                }
            }

            return new OcrPage(page.Width, page.Height, [.. lines.OrderBy(line => line.Top).ThenBy(line => line.Left)]);
        }

        // The picture turned clockwise by a multiple of 90 degrees.
        private static SKBitmap Turn(SKBitmap source, int degrees)
        {
            var sideways = degrees is 90 or 270;
            var turned = new SKBitmap(sideways ? source.Height : source.Width, sideways ? source.Width : source.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
            using var canvas = new SKCanvas(turned);
            canvas.Translate(turned.Width / 2f, turned.Height / 2f);
            canvas.RotateDegrees(degrees);
            canvas.Translate(-source.Width / 2f, -source.Height / 2f);
            canvas.DrawBitmap(source, 0, 0, SKSamplingOptions.Default);
            return turned;
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

        private (string Text, float Confidence) Recognize(SKBitmap page, SKRectI box)
        {
            if (box.Width < 2 || box.Height < 2)
            {
                return ("", 0);
            }

            var width = Math.Clamp((int)Math.Ceiling(LineHeight * (float)box.Width / box.Height), 16, MaximumLineWidth);
            using var crop = new SKBitmap(box.Width, box.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
            if (!page.ExtractSubset(crop, box))
            {
                return ("", 0);
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
                    sum += probability;
                    kept++;
                }

                previous = best;
            }

            return (text.ToString(), kept == 0 ? 0 : sum / kept);
        }

        public void Dispose()
        {
            _detection.Dispose();
            _recognition.Dispose();
        }
    }

    // A recognized line: its box in the picture's pixels, its text and the mean probability of its characters.
    public sealed record OcrLine(int Left, int Top, int Right, int Bottom, string Text, float Confidence);

    public sealed record OcrPage(int Width, int Height, IReadOnlyList<OcrLine> Lines);
}
