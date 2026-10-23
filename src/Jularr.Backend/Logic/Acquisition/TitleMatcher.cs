using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Logic.Acquisition;

namespace Jularr.Backend.Logic.Acquisition
{
    // The shared title test of release identity.
    internal static class TitleMatcher
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
    }
}
