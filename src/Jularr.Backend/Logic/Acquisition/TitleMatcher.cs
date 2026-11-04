using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Logic.Acquisition;

namespace Jularr.Backend.Logic.Acquisition
{
    internal enum TitleComparison
    {
        Different,
        OtherYear,
        Same
    }

    // The shared title tests: `Matches` finds a title inside a longer one (metadata lookups); `Compare` decides whether a release names exactly a series, as Sonarr's parser does.
    internal static partial class TitleMatcher
    {
        private static readonly HashSet<string> IgnoredWords = new(StringComparer.OrdinalIgnoreCase)
        {
            "the", "a", "an", "of", "and", "der", "die", "das", "und"
        };

        public static bool Matches(string requested, string candidate)
        {
            var wanted = SignificantWords(requested);
            if (wanted.Count == 0)
            {
                return false;
            }

            var actual = SignificantWords(candidate);
            return wanted.All(actual.Contains);
        }

        // Whether the candidate carries the full title of the requested one or of any of its aliases.
        public static bool MatchesAny(IEnumerable<string> requested, string candidate) =>
            requested.Any(title => Matches(title, candidate));

        public static HashSet<string> SignificantWords(string value) =>
            value.Split([' ', '.', '_', '-', ':', ',', '(', ')', '[', ']', '\'', '"', '!', '?', '&', '/'], StringSplitOptions.RemoveEmptyEntries)
                .Select(word => word.ToLowerInvariant())
                .Where(word => word.Length > 1 && !IgnoredWords.Contains(word))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
    
        // The words of a title as Sonarr cleans them: no accents or case, "&" is "and", apostrophes vanish, any other mark separates, a leading article is dropped.
        public static string Key(string value)
        {
            var decomposed = value.Normalize(NormalizationForm.FormD).Where(character => CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark);
            var words = new StringBuilder();
            foreach (var character in string.Concat(decomposed).ToLowerInvariant().Replace("&", " and ").Replace("'", string.Empty).Replace("\u2019", string.Empty))
            {
                words.Append(char.IsLetterOrDigit(character) ? character : ' ');
            }

            var tokens = words.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length > 1 && tokens[0] is "the" or "a" or "an")
            {
                tokens = tokens[1..];
            }

            // Letters spelled apart by dots ("S.W.A.T.") are one word.
            return string.Concat(tokens);
        }

        // A title and the year it states at its end ("Doctor Who 2005", "Doctor Who (2005)"); a year alone is no year.
        public static (string Title, int? Year) SplitYear(string value)
        {
            var match = TrailingYear().Match(value.Trim());
            return match.Success && match.Index > 0 ? (value.Trim()[..match.Index].TrimEnd(' ', '(', '[', '.', '-', '_'), int.Parse(match.Groups["year"].Value, CultureInfo.InvariantCulture)) : (value.Trim(), null);
        }

        [GeneratedRegex(@"[\s.(\[_-]+(?<year>(?:19|20)\d{2})[)\]]?$")]
        private static partial Regex TrailingYear();

        // Whether a release names exactly one of the titles asked for. Words of its own after the title (another series, a spin-off, a sequel) make it another title.
        // A year the release adds must be the target's year, give or take one, or the title it names is another series with the same name.
        public static TitleComparison Compare(IEnumerable<string> requested, string candidate, int? targetYear)
        {
            var (name, candidateYear) = SplitYear(candidate);
            var key = Key(name);
            var otherYear = false;
            foreach (var title in requested)
            {
                if (key.Length == 0 || Key(title).Length == 0)
                {
                    continue;
                }

                var (requestedName, requestedYear) = SplitYear(title);
                if (Key(title) == Key(candidate))
                {
                    return TitleComparison.Same;
                }

                if (Key(requestedName) != key)
                {
                    continue;
                }

                var expected = requestedYear ?? targetYear;
                if (candidateYear is { } stated && expected is { } wanted && Math.Abs(stated - wanted) > 1)
                {
                    otherYear = true;
                    continue;
                }

                return TitleComparison.Same;
            }

            return otherYear ? TitleComparison.OtherYear : TitleComparison.Different;
        }
    }
}

