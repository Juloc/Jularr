using System.Text.Json;
using System.Text.Json.Serialization;
using Jularr.Integrations.Logic.Ocr;
using UglyToad.PdfPig.Content;

namespace Jularr.Backend.Logic.Reading
{
    // The text a recognizer found on one page of a scan, as the lines in fractions of the page (kept beside the PDF, never in it) and the plausibility of their words.
    internal sealed record RecognizedPage(string Reason, double Quality, IReadOnlyList<RecognizedLine> Lines)
    {
        // Lines read; a page without ink; a page that already has text; a picture the server cannot decode, which the original shows.
        public const string Recognized = "recognized";
        public const string Blank = "blank";
        public const string HasText = "text";
        public const string Undecodable = "undecodable";

        private const double MinimumConfidence = 0.5;

        // The lines the recognizer was sure of as words of the page in points, in reading order, like a recognition layer's.
        public IReadOnlyList<PdfWord> Words(PdfBox page)
        {
            var words = new List<PdfWord>();
            foreach (var line in Lines.Where(line => line.Confidence >= MinimumConfidence).OrderBy(line => line.Top).ThenBy(line => line.Left))
            {
                var box = new PdfBox(page.Left + line.Left * page.Width, page.Top - line.Bottom * page.Height, page.Left + line.Right * page.Width, page.Top - line.Top * page.Height);
                words.Add(new PdfWord(box, line.Text, box.Height * 0.7, true, words.Count));
            }

            return words;
        }

        // A recognizer that is unsure of most of what it found on a page found no text there but a picture, a drawing or a stain.
        public bool IsUncertain => Lines.Count >= 3 && Lines.Count(line => line.Confidence >= MinimumConfidence) < Lines.Count * 0.6;

        public static string Serialize(IReadOnlyList<RecognizedLine> lines) => JsonSerializer.Serialize(lines);

        public static IReadOnlyList<RecognizedLine> Parse(string json) => JsonSerializer.Deserialize<RecognizedLine[]>(json) ?? [];
    }

    internal sealed record RecognizedLine(
        [property: JsonPropertyName("l")] double Left,
        [property: JsonPropertyName("t")] double Top,
        [property: JsonPropertyName("r")] double Right,
        [property: JsonPropertyName("b")] double Bottom,
        [property: JsonPropertyName("s")] string Text,
        [property: JsonPropertyName("c")] double Confidence);

    // Reads one page of a scanned PDF: the picture that covers it, turned upright as the page shows it, goes to the recognizer.
    internal static class PdfScanPages
    {
        private const double ScanCoverage = 0.5;
        private const int MaximumPictureBytes = 40 * 1024 * 1024;

        // Pictures that take fewer bytes than this on a whole page are blank paper.
        private const int BlankScanBytes = 8000;

        public static RecognizedPage Read(Page page, Func<byte[], int, OcrPage> recognize)
        {
            if (page.GetWords().Any(word => !string.IsNullOrWhiteSpace(word.Text)))
            {
                return new RecognizedPage(RecognizedPage.HasText, 1, []);
            }

            var area = page.CropBox.Bounds.Width * page.CropBox.Bounds.Height;
            var scan = page.GetImages().Where(image => !image.IsImageMask && image.BoundingBox.Width * image.BoundingBox.Height >= area * ScanCoverage)
                .MaxBy(image => image.BoundingBox.Width * image.BoundingBox.Height);
            if (scan is null)
            {
                return new RecognizedPage(RecognizedPage.Blank, 1, []);
            }

            byte[]? bytes;
            try
            {
                bytes = scan.TryGetPng(out var png) ? png : scan.RawMemory.Span is [0xFF, 0xD8, ..] ? scan.RawMemory.ToArray() : null;
            }
            catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException or ArgumentException or IndexOutOfRangeException or InvalidDataException)
            {
                bytes = null;
            }

            if (bytes is null || bytes.Length > MaximumPictureBytes)
            {
                return new RecognizedPage(scan.RawMemory.Length < BlankScanBytes ? RecognizedPage.Blank : RecognizedPage.Undecodable, 1, []);
            }

            OcrPage read;
            try
            {
                read = recognize(bytes, page.Rotation.Value);
            }
            catch (InvalidDataException)
            {
                return new RecognizedPage(RecognizedPage.Undecodable, 1, []);
            }

            var lines = read.Lines.Select(line => new RecognizedLine(Round((double)line.Left / read.Width), Round((double)line.Top / read.Height), Round((double)line.Right / read.Width),
                Round((double)line.Bottom / read.Height), line.Text, Math.Round(line.Confidence, 3))).ToArray();
            var quality = PdfQuality.Score(lines.Where(line => line.Confidence >= 0.5).SelectMany(line => line.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries)));
            return new RecognizedPage(lines.Length == 0 ? RecognizedPage.Blank : RecognizedPage.Recognized, Math.Round(quality, 3), lines);
        }

        private static double Round(double fraction) => Math.Round(Math.Clamp(fraction, 0, 1), 4);
    }
}
