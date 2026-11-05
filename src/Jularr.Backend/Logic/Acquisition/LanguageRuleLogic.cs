using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Logic.Acquisition;

namespace Jularr.Backend.Logic.Acquisition
{
    // The add-ons of a language (release groups and words, for every media type): what a release in that language gains or loses by its group and its words.
    internal static class LanguageRuleLogic
    {
        public const int GroupPoints = 50;
        public const int WordPoints = 25;

        // The points of a release and what blocks it: only the rules of the languages it is in count. A word is a whole word of the release name, a group the release's group in any case.
        public static (IReadOnlyList<ScorePoint> Points, SelectionReason? Blocked) Apply(IReadOnlyList<LanguageRule> rules, IReadOnlyList<string> languages, ReleaseInfo? parsed, string title)
        {
            var points = new List<ScorePoint>();
            SelectionReason? blocked = null;
            if (rules.Count == 0 || languages.Count == 0)
            {
                return (points, null);
            }

            var words = ReleaseWords.Tokens(title).Select(word => word.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
            var groups = ReleaseGroupLogic.Split(parsed?.ReleaseGroup).ToArray();
            foreach (var rule in rules.Where(rule => languages.Contains(rule.Language, StringComparer.OrdinalIgnoreCase)))
            {
                foreach (var group in rule.PreferredGroups.Where(preferred => groups.Contains(preferred, StringComparer.OrdinalIgnoreCase)))
                {
                    points.Add(new ScorePoint("LanguageGroupPreferred", group, GroupPoints));
                }

                foreach (var word in rule.PreferredWords.Where(preferred => Matches(words, preferred)))
                {
                    points.Add(new ScorePoint("LanguageWordPreferred", word, WordPoints));
                }

                blocked ??= rule.BlockedGroups.FirstOrDefault(group => groups.Contains(group, StringComparer.OrdinalIgnoreCase)) is { } blockedGroup
                    ? new SelectionReason(SelectionReasonKind.Profile, "LanguageGroupBlocked", blockedGroup)
                    : rule.BlockedWords.FirstOrDefault(word => Matches(words, word)) is { } blockedWord ? new SelectionReason(SelectionReasonKind.Profile, "LanguageWordBlocked", blockedWord) : null;
            }

            return (points, blocked);
        }

        // Every word of the entry (an entry of several words is a phrase) is a word of the name.
        private static bool Matches(HashSet<string> words, string entry)
        {
            var wanted = ReleaseWords.Tokens(entry).Select(word => word.ToLowerInvariant()).ToArray();
            return wanted.Length > 0 && wanted.All(words.Contains);
        }
    }

    internal sealed record LanguageRule(string Language, string[] PreferredGroups, string[] BlockedGroups, string[] PreferredWords, string[] BlockedWords);
}
