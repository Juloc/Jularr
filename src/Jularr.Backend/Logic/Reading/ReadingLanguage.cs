using System.Text.RegularExpressions;

namespace Jularr.Backend.Logic.Reading
{
    // A book's language from its text, where the file names none (a scanned PDF, an EPUB without its tag): kana and kanji, otherwise the most frequent
    // common words; the same lists the Reader uses, so the server's translation and the Reader's agree.
    internal static partial class ReadingLanguage
    {
        private static readonly Dictionary<string, string[]> Stopwords = new(StringComparer.Ordinal)
        {
            ["en"] = ["the", "and", "of", "to", "was", "that", "he", "it", "with", "is"],
            ["de"] = ["der", "die", "und", "das", "nicht", "ist", "ein", "sich", "mit", "zu"],
            ["fr"] = ["le", "la", "les", "et", "des", "est", "une", "que", "pas", "dans"],
            ["es"] = ["el", "la", "que", "de", "los", "las", "una", "por", "con", "para"],
            ["it"] = ["il", "che", "di", "la", "non", "una", "per", "sono", "gli", "del"],
            ["nl"] = ["de", "het", "een", "en", "van", "niet", "dat", "zijn", "met", "voor"],
            ["id"] = ["yang", "dan", "di", "itu", "dengan", "tidak", "ini", "ke", "dari", "untuk"]
        };

        [GeneratedRegex(@"\p{L}+")]
        private static partial Regex Words();

        [GeneratedRegex(@"[\p{IsHiragana}\p{IsKatakana}\p{IsCJKUnifiedIdeographs}]")]
        private static partial Regex Japanese();

        public static string? Guess(string text)
        {
            var letters = text.Count(char.IsLetter);
            if (letters == 0)
            {
                return null;
            }

            if (Japanese().Count(text) > letters * 0.2)
            {
                return "ja";
            }

            var counts = Words().Matches(text.ToLowerInvariant()).GroupBy(match => match.Value).ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
            var best = Stopwords.Select(language => (Language: language.Key, Count: language.Value.Sum(word => counts.GetValueOrDefault(word))))
                .OrderByDescending(language => language.Count).First();
            return best.Count > 3 ? best.Language : null;
        }
    }
}
