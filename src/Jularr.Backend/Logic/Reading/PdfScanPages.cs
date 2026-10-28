using System.Text.Json;
using System.Text.Json.Serialization;
using Jularr.Integrations.Logic.Ocr;
using Jularr.Integrations.Logic.Pdf;
using SkiaSharp;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;

namespace Jularr.Backend.Logic.Reading
{
    // What a recognizer found on one page of a scan, as lines and pictures in fractions of the page (kept beside the PDF, never in it) and the plausibility of its words.
    internal sealed record RecognizedPage(string Reason, double Quality, IReadOnlyList<RecognizedLine> Lines)
    {
        // Lines read; a page without ink; a page that already has visible text; a page the server cannot draw, which the original shows.
        public const string Recognized = "recognized";
        public const string Blank = "blank";
        public const string HasText = "text";
        public const string Undecodable = "undecodable";

        private const double MinimumConfidence = 0.5;

        private IEnumerable<RecognizedLine> Text => Lines.Where(line => line.Kind is null);

        // The running text the recognizer was sure of as words of the page in points, in reading order, like a recognition layer's; labels inside pictures stay with them.
        public IReadOnlyList<PdfWord> Words(PdfBox page)
        {
            var words = new List<PdfWord>();
            foreach (var line in Text.Where(line => line.Confidence >= MinimumConfidence).OrderBy(line => line.Top).ThenBy(line => line.Left))
            {
                words.Add(new PdfWord(Box(line, page), line.Text, Box(line, page).Height * 0.7, true, words.Count));
            }

            return words;
        }

        // A recognizer that is unsure of most of what it found on a page found no text there but a picture, a drawing or a stain.
        public bool IsUncertain => Text.Count() >= 3 && Text.Count(line => line.Confidence >= MinimumConfidence) < Text.Count() * 0.6;

        private static PdfBox Box(RecognizedLine line, PdfBox page) =>
            new(page.Left + line.Left * page.Width, page.Top - line.Bottom * page.Height, page.Left + line.Right * page.Width, page.Top - line.Top * page.Height);

        public static string Serialize(IReadOnlyList<RecognizedLine> lines) => JsonSerializer.Serialize(lines);

        public static IReadOnlyList<RecognizedLine> Parse(string json) => JsonSerializer.Deserialize<RecognizedLine[]>(json) ?? [];
    }

    // A line of text in fractions of the page, or (by its kind) a label inside a picture or the picture itself.
    internal sealed record RecognizedLine(
        [property: JsonPropertyName("l")] double Left,
        [property: JsonPropertyName("t")] double Top,
        [property: JsonPropertyName("r")] double Right,
        [property: JsonPropertyName("b")] double Bottom,
        [property: JsonPropertyName("s")] string Text,
        [property: JsonPropertyName("c")] double Confidence,
        [property: JsonPropertyName("k"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Kind = null)
    {
        public const string Label = "label";
        public const string Picture = "picture";
    }

    // Reads one page of a scanned PDF: drawn by PDFium as it shows (scans in JPEG 2000 or JBIG2 too), read by the recognizer and analysed for its pictures; the
    // scan's own hidden recognition layer, where it has one, is a second opinion. A page with visible text is not a scan.
    internal static class PdfScanPages
    {
        private const double ScanCoverage = 0.5;

        // Pictures that take fewer bytes than this on a whole page are blank paper.
        private const int BlankScanBytes = 8000;

        // Scans are read at about 300 dpi, their longer side within these pixels.
        private const int MinimumSide = 1600;
        private const int MaximumSide = 3300;

        public static RecognizedPage Read(Page page, PdfRendering rendering, Func<SKBitmap, OcrPage> recognize)
        {
            var words = page.GetWords().Where(word => !string.IsNullOrWhiteSpace(word.Text)).ToList();
            if (words.Any(word => word.Letters.Any(letter => letter.RenderingMode != TextRenderingMode.Neither)))
            {
                return new RecognizedPage(RecognizedPage.HasText, 1, []);
            }

            // Without a hidden recognition layer, a scan of a few bytes is blank paper; layered scans (a background, the letters as a mask) are small but full.
            var area = page.CropBox.Bounds.Width * page.CropBox.Bounds.Height;
            var scans = page.GetImages().Where(image => !image.IsImageMask && image.BoundingBox.Width * image.BoundingBox.Height >= area * ScanCoverage).ToList();
            if (scans.Count == 0 || (words.Count == 0 && scans.Sum(image => (long)image.RawMemory.Length) < BlankScanBytes))
            {
                return new RecognizedPage(RecognizedPage.Blank, 1, []);
            }

            OcrPage read;
            IReadOnlyList<RecognizedLine> lines;
            try
            {
                using var drawn = rendering.Render(page.Number, (int)Math.Clamp(Math.Round(Math.Max(page.Width, page.Height) * 300 / 72), MinimumSide, MaximumSide));
                read = recognize(drawn);
                lines = PdfScanAnalysis.Analyse(drawn, read, rendering.Words(page.Number));
            }
            catch (InvalidDataException)
            {
                return new RecognizedPage(RecognizedPage.Undecodable, 1, []);
            }

            var text = lines.Where(line => line.Kind is null && line.Confidence >= 0.5).ToList();
            var quality = PdfQuality.Score(text.SelectMany(line => line.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries)));
            return new RecognizedPage(lines.Count == 0 ? RecognizedPage.Blank : RecognizedPage.Recognized, Math.Round(quality, 3), lines);
        }
    }
}
