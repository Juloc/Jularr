namespace Jularr.Backend.Logic.Acquisition
{
    // A release in the language of an edition that is switched off is left out, unless a monitored edition has that language too.
    // A search for one edition wants only its language. A release that names no language always passes.
    internal static class EditionLanguageFilter
    {
        public static bool Rejects(IReadOnlyList<string> candidateLanguages, IReadOnlyList<string> monitored, IReadOnlyList<string> unmonitored, string? focus)
        {
            var languages = candidateLanguages.Select(Normalize).Where(language => language.Length > 0).Distinct().ToArray();
            if (languages.Length == 0)
            {
                return false;
            }

            if (Normalize(focus) is { Length: > 0 } wanted)
            {
                return !languages.Contains(wanted);
            }

            var wantedLanguages = monitored.Select(Normalize).ToHashSet();
            var unwanted = unmonitored.Select(Normalize).ToHashSet();
            return !languages.Any(wantedLanguages.Contains) && languages.Any(unwanted.Contains);
        }

        // "de", "deu" and "ger" are German; a code nobody knows stays as it is.
        private static string Normalize(string? language) => Media.LanguageCodes.ToTwoLetter(language?.Trim()) ?? language?.Trim().ToLowerInvariant() ?? "";
    }
}
