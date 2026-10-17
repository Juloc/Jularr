using System.Text.RegularExpressions;

namespace Jularr.Backend.Logic.Acquisition
{
    // Words of book and Manga release names: tokens, years and stated languages.
    internal static partial class ReleaseWords
    {
        private static readonly Dictionary<string, string> Languages = new(StringComparer.OrdinalIgnoreCase)
        {
            ["english"] = "EN", ["german"] = "DE", ["deutsch"] = "DE", ["french"] = "FR", ["francais"] = "FR", ["spanish"] = "ES", ["espanol"] = "ES", ["italian"] = "IT", ["japanese"] = "JA"
        };

        [GeneratedRegex(@"^(?:1[5-9]|20)\d{2}$")]
        public static partial Regex YearToken();

        public static string[] Tokens(string value) =>
            value.Split([' ', '.', '_', '-', ':', ',', '(', ')', '[', ']', '{', '}', '\'', '"', '!', '?', '&', '/', '#', '+', '~'], StringSplitOptions.RemoveEmptyEntries);

        public static HashSet<string> Words(string value, IReadOnlySet<string> noise) =>
            Tokens(value).Select(word => word.ToLowerInvariant()).Where(word => !noise.Contains(word)).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Untagged releases are English, or a book in the language it was written in.
        public static string[] ReadLanguages(string title, string untagged = "EN") =>
            Tokens(title).Select(token => Languages.GetValueOrDefault(token)).OfType<string>()
                .Concat(CatalogLanguage().Matches(title).Select(match => match.Groups[1].Value.ToUpperInvariant()))
                .Distinct().DefaultIfEmpty(untagged).ToArray();

        // Direct catalogs append their explicit language immediately before the ebook format; short title words are not language tags.
        [GeneratedRegex(@"\(([a-z]{2})(?:-[a-z]{2})?\)(?=\s*\((?:epub|pdf|azw3|mobi|cbz|cbr)\)\s*$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
        private static partial Regex CatalogLanguage();
    }
}
