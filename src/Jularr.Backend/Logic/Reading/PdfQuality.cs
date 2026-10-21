namespace Jularr.Backend.Logic.Reading
{
    internal enum PdfPageKind : byte
    {
        Blank = 0,
        Text = 1,
        Figure = 2,
        Placeholder = 3
    }

    // What the analysis found on one page, for the book's judgement and for the evaluation report.
    internal sealed record PdfPageReport(int Number, PdfPageKind Kind, string Reason, int Characters, int Words, int Pictures, int UnreadablePictures, int Shapes, int Columns, bool IsHidden, double TextQuality);

    // Whether a book is fit to read as reflowed text, and why not.
    internal sealed record PdfVerdict(bool IsReadable, string Reason);

    // Plausible text against recognition noise, and the book-level gate for a derived EPUB.
    internal static class PdfQuality
    {
        private const string Vowels = "aeiouyàáâãäåæèéêëìíîïòóôõöøùúûüœAEIOUYÀÁÂÃÄÅÆÈÉÊËÌÍÎÏÒÓÔÕÖØÙÚÛÜŒ";

        // The share of words that look like words; 1 for clean text, low for the noise a scan's recognizer makes of a picture.
        public static double Score(IEnumerable<string> words)
        {
            var total = 0;
            var junk = 0;
            foreach (var word in words)
            {
                var token = word.Trim('.', ',', ';', ':', '!', '?', '"', '\'', '(', ')', '[', ']', '«', '»', '“', '”', '‘', '’', '-', '–', '—', '…');
                if (token.Length == 0)
                {
                    continue;
                }

                total++;
                if (token.All(char.IsDigit))
                {
                    continue;
                }

                // Mostly symbols, or no vowel in a word that is no acronym, is noise.
                var letters = token.Count(char.IsLetter);
                var acronym = token.Length <= 5 && token.All(character => !char.IsLetter(character) || char.IsUpper(character));
                if (letters * 2 < token.Length || !token.Any(Vowels.Contains) && !acronym)
                {
                    junk++;
                }
            }

            return total == 0 ? 1 : 1 - (double)junk / total;
        }

        // Text read out of a picture (a scan's recognition layer, a recognizer) is shown as reading text only where it holds together: null when it does, else why not.
        // A page with little text and noise is an illustration with a few labels; a full page tolerates the odd split word. Lines that run through each other are a layout reflow cannot reconstruct.
        public static string? Unreliable(IReadOnlyList<string> words, IReadOnlyList<PdfLine> lines, double pageWidth)
        {
            // A recognizer's line holds several words.
            var tokens = words.SelectMany(word => word.Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToArray();
            if (tokens.Length == 0)
            {
                return null;
            }

            if (Score(tokens) < (tokens.Length < 80 ? 0.9 : 0.7) || tokens.Length >= 15 && tokens.Average(token => token.Length) < 2.5)
            {
                return "noise";
            }

            return PdfLayout.IsInterleaved(lines, pageWidth) ? "columns" : null;
        }

        // A book is a text book when its pages mostly read as text; pictures and drawings are kept as figures, pages that only the original shows are not allowed.
        public static PdfVerdict Judge(IReadOnlyList<PdfPageReport> pages, bool forEbook)
        {
            var counted = pages.Where(page => page.Kind != PdfPageKind.Blank).ToArray();
            if (counted.Length == 0)
            {
                return new PdfVerdict(false, "no_text");
            }

            var text = counted.Where(page => page.Kind == PdfPageKind.Text).ToArray();
            var characters = text.Sum(page => page.Characters);
            if (text.Length == 0 || characters < Math.Max(200, counted.Length * 20))
            {
                return new PdfVerdict(false, "no_text");
            }

            var placeholders = counted.Count(page => page.Kind == PdfPageKind.Placeholder);
            if (placeholders * 2 > counted.Length)
            {
                return new PdfVerdict(false, "pictures");
            }

            var quality = text.Sum(page => page.TextQuality * page.Characters) / characters;
            if (quality < 0.7)
            {
                return new PdfVerdict(false, "garbled");
            }

            if (!forEbook)
            {
                return new PdfVerdict(true, "text");
            }

            // A file kept beside the PDF stays to digital text with every page shown; recognized scans and pages left to the original are not for it.
            if (placeholders > 0)
            {
                return new PdfVerdict(false, "original_pages");
            }

            if (text.Count(page => page.IsHidden || page.Reason == "recognized") * 20 > text.Length)
            {
                return new PdfVerdict(false, "scan");
            }

            if (counted.Count(page => page.Kind == PdfPageKind.Figure) * 2 > counted.Length)
            {
                return new PdfVerdict(false, "pictures");
            }

            return new PdfVerdict(true, "text");
        }
    }
}
