using System.Text;
using System.Text.RegularExpressions;

namespace Jularr.Backend.Logic.Reading
{
    // A plain text book: paragraphs between blank lines (or one per line where it has none), chapters where a line names one.
    internal static partial class TextBook
    {
        // A heading line: a kind of division and its number, a lone front-matter word, or a Markdown heading.
        [GeneratedRegex(@"^((Kapitel|Chapter|Chapitre|Cap[ií]tulo|Capitolo|Teil|Part|Partie|Parte|Buch|Book|Livre|Libro)\s+(\d{1,3}|[IVXLC]{1,7})\b[.:]?(\s.{1,80})?|Prolog|Prologue|Prólogo|Epilog|Epilogue|Epílogo|Vorwort|Nachwort|Einleitung|Foreword|Preface|Prefacio|Introduction|Introducción|Introduzione)\.?$", RegexOptions.IgnoreCase)]
        private static partial Regex Heading();

        [GeneratedRegex(@"^(#{1,3})\s+(\S.{0,200})$")]
        private static partial Regex MarkdownHeading();

        [GeneratedRegex(@"\n[ \t]*\n")]
        private static partial Regex BlankLine();

        [GeneratedRegex(@"[ \t]+")]
        private static partial Regex Spaces();

        static TextBook()
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        }

        public static ConvertedBook Read(byte[] bytes)
        {
            var text = Decode(bytes).Replace("\r\n", "\n").Replace('\r', '\n');
            var book = new ConvertedBook();

            // Hard-wrapped text has paragraphs between blank lines; text without blank lines has one per line.
            var paragraphs = BlankLine().Matches(text).Count >= 2 ? BlankLine().Split(text) : text.Split('\n');
            foreach (var paragraph in paragraphs)
            {
                var lines = paragraph.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                if (lines.Length == 0)
                {
                    continue;
                }

                // A short paragraph of one line that names a chapter starts one.
                var line = Spaces().Replace(lines[0], " ");
                if (lines.Length == 1 && MarkdownHeading().Match(line) is { Success: true } markdown)
                {
                    var level = markdown.Groups[1].Length;
                    book.Chapter(markdown.Groups[2].Value.Trim(), level - 1);
                    book.Add("<h" + level + ">" + BookConversion.Escape(markdown.Groups[2].Value.Trim()) + "</h" + level + ">");
                    continue;
                }

                if (lines.Length == 1 && line.Length <= 100 && Heading().IsMatch(line))
                {
                    book.Chapter(line, 0);
                    book.Add("<h2>" + BookConversion.Escape(line) + "</h2>");
                    continue;
                }

                book.Add("<p>" + BookConversion.Escape(Spaces().Replace(string.Join(' ', lines), " ")) + "</p>");
            }

            return book;
        }

        // Text by its byte order mark, else UTF-8 where it is valid, else Windows-1252.
        public static string Decode(byte[] bytes)
        {
            if (bytes is [0xEF, 0xBB, 0xBF, ..]) return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
            if (bytes is [0xFF, 0xFE, ..]) return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
            if (bytes is [0xFE, 0xFF, ..]) return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
            try
            {
                return new UTF8Encoding(false, true).GetString(bytes);
            }
            catch (DecoderFallbackException)
            {
                return CodePage(1252).GetString(bytes);
            }
        }

        // A Windows code page by its number; unknown ones read as Windows-1252.
        public static Encoding CodePage(int number)
        {
            try
            {
                return number == 65001 ? new UTF8Encoding(false) : Encoding.GetEncoding(number);
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
            {
                return Encoding.GetEncoding(1252);
            }
        }
    }
}
