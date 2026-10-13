using System.Text.RegularExpressions;

namespace Jularr.Backend.Logic.Acquisition
{
    // Words of book and Manga release names: tokens, years and stated languages.
    internal static partial class ReleaseWords
    {
        private static readonly Dictionary<string, string> Languages = new(StringComparer.OrdinalIgnoreCase)
        {
            ["german"] = "DE", ["deutsch"] = "DE", ["french"] = "FR", ["francais"] = "FR", ["spanish"] = "ES", ["espanol"] = "ES", ["italian"] = "IT", ["japanese"] = "JA"
        };

        [GeneratedRegex(@"^(?:1[5-9]|20)\d{2}$")]
        public static partial Regex YearToken();

        public static string[] Tokens(string value) =>
            value.Split([' ', '.', '_', '-', ':', ',', '(', ')', '[', ']', '{', '}', '\'', '"', '!', '?', '&', '/', '#', '+', '~'], StringSplitOptions.RemoveEmptyEntries);

        public static HashSet<string> Words(string value, IReadOnlySet<string> noise) =>
            Tokens(value).Select(word => word.ToLowerInvariant()).Where(word => !noise.Contains(word)).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Untagged releases are English.
        public static string[] ReadLanguages(string title) =>
            Tokens(title).Select(token => Languages.GetValueOrDefault(token)).OfType<string>().Distinct().DefaultIfEmpty("EN").ToArray();
    }
}
