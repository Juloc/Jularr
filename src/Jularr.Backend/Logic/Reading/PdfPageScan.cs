using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Graphics;

namespace Jularr.Backend.Logic.Reading
{
    // What one page holds, read once: its words, its pictures and how much of it is drawn with vector paths.
    internal sealed class PdfPageScan
    {
        // A drawing needs several shapes of some size; rules and bullets are not drawings.
        private const int MaximumPaths = 20000;
        private const double MinimumShape = 4;
        private const double RuleThickness = 1.6;

        public PdfPageScan(Page page)
        {
            Number = page.Number;
            var bounds = page.CropBox.Bounds;
            Box = new PdfBox(bounds.Left, bounds.Bottom, bounds.Right, bounds.Top);
            Rotation = page.Rotation.Value;

            // Words in stream order; a recognition layer is text drawn invisibly.
            var order = 0;
            foreach (var word in page.GetWords())
            {
                if (string.IsNullOrWhiteSpace(word.Text) || word.Letters.Count == 0)
                {
                    continue;
                }

                var size = word.Letters.Average(letter => letter.PointSize);
                var hidden = word.Letters.All(letter => letter.RenderingMode == TextRenderingMode.Neither);
                Words.Add(new PdfWord(new PdfBox(word.BoundingBox.Left, word.BoundingBox.Bottom, word.BoundingBox.Right, word.BoundingBox.Top), word.Text, size, hidden, order++));
            }

            foreach (var image in page.GetImages())
            {
                if (!image.IsImageMask)
                {
                    Pictures.Add(new PdfPicture(new PdfBox(image.BoundingBox.Left, image.BoundingBox.Bottom, image.BoundingBox.Right, image.BoundingBox.Top), image));
                }
            }

            ReadDrawing(page);
        }

        public int Number { get; }
        public PdfBox Box { get; }
        public int Rotation { get; }
        public List<PdfWord> Words { get; } = [];
        public List<PdfPicture> Pictures { get; } = [];

        // Shapes besides rules and backgrounds, the rules, the box around the shapes, and whether the page has more paths than are looked at.
        public int Shapes { get; private set; }
        public int Rules { get; private set; }
        public PdfBox? ShapeBounds { get; private set; }
        public bool TooComplex { get; private set; }
        public bool IsHidden => Words.Count > 0 && Words.Count(word => word.Hidden) * 10 >= Words.Count * 8;
        public int Characters => Words.Sum(word => word.Text.Length);

        private void ReadDrawing(Page page)
        {
            IReadOnlyList<PdfPath> paths;
            try
            {
                paths = page.Paths;
            }
            catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException or ArgumentException or IndexOutOfRangeException or InvalidCastException)
            {
                return;
            }

            if (paths.Count > MaximumPaths)
            {
                TooComplex = true;
                return;
            }

            foreach (var path in paths)
            {
                if (path.IsClipping && !path.IsFilled && !path.IsStroked || path.GetBoundingRectangle() is not { } rectangle)
                {
                    continue;
                }

                var bounds = new PdfBox(rectangle.Left, rectangle.Bottom, rectangle.Right, rectangle.Top);
                var (width, height) = (bounds.Width, bounds.Height);
                if (bounds.Area >= Box.Area * 0.9 && path.IsFilled)
                {
                    continue;
                }

                if (Math.Min(width, height) < RuleThickness && Math.Max(width, height) >= 8)
                {
                    Rules++;
                }
                else if (Math.Max(width, height) >= MinimumShape)
                {
                    Shapes++;
                    ShapeBounds = ShapeBounds is { } known ? known.Union(bounds) : bounds;
                }
            }
        }
    }

    // A picture of the page and the first sign that it cannot be read: a format PdfPig does not decode.
    internal sealed class PdfPicture(PdfBox box, IPdfImage image)
    {
        private bool _read;
        private (byte[] Bytes, string MediaType, string Extension)? _decoded;

        public PdfBox Box { get; } = box;
        public IPdfImage Image { get; } = image;

        // PNG where PdfPig decodes the picture, JPEG as stored; none for what a browser cannot show either, such as JPEG 2000 or JBIG2.
        public (byte[] Bytes, string MediaType, string Extension)? Decoded
        {
            get
            {
                if (!_read)
                {
                    _read = true;
                    try
                    {
                        _decoded = Image.TryGetPng(out var png) ? (png, "image/png", ".png")
                            : Image.RawMemory.Span is [0xFF, 0xD8, ..] ? (Image.RawMemory.ToArray(), "image/jpeg", ".jpg")
                            : null;
                    }
                    catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException or ArgumentException or IndexOutOfRangeException or InvalidDataException)
                    {
                        _decoded = null;
                    }
                }

                return _decoded;
            }
        }

        // The bytes the picture takes in the file, a hint whether a scanned page is more than a blank.
        public int StoredBytes => Image.RawMemory.Length;

        // The filter name for the report: what stops the picture from being shown.
        public string Unreadable => Image.ImageDictionary.Data.TryGetValue("Filter", out var filter) ? filter.ToString()!.TrimStart('/') : "none";
    }
}
