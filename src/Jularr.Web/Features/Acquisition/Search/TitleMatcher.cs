namespace Jularr.Web.Features.Acquisition.Search;

/// <summary>
/// The shared title test of release identity: every significant word of the requested title has to occur in the title a release
/// carries. It is deliberately a hard identity gate and not a similarity score, so a release of another title is never rescued by a
/// good profile score. Media types with stronger evidence (canonical numbering, creator) add to it, never replace it.
/// </summary>
public static class TitleMatcher
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

    /// <summary>Whether the candidate carries the full title of the requested one or of any of its aliases.</summary>
    public static bool MatchesAny(IEnumerable<string> requested, string candidate) =>
        requested.Any(title => Matches(title, candidate));

    public static HashSet<string> SignificantWords(string value) =>
        value.Split(
                [' ', '.', '_', '-', ':', ',', '(', ')', '[', ']', '\'', '"', '!', '?', '&', '/'],
                StringSplitOptions.RemoveEmptyEntries)
            .Select(word => word.ToLowerInvariant())
            .Where(word => word.Length > 1 && !IgnoredWords.Contains(word))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
}
