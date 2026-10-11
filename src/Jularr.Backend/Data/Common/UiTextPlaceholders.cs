using System.Text.RegularExpressions;

namespace Jularr.Backend.Data.Common
{
    internal static partial class UiTextPlaceholders
    {
        [GeneratedRegex(@"(?<!\{)\{([A-Za-z][A-Za-z0-9_]*)\}(?!\})", RegexOptions.CultureInvariant)]
        private static partial Regex NamedPlaceholders();

        internal static void RequireMatching(string englishText, string translatedText)
        {
            var original = NamedPlaceholders().Matches(englishText)
                .Select(match => match.Groups[1].Value).Order(StringComparer.Ordinal);
            var translated = NamedPlaceholders().Matches(translatedText)
                .Select(match => match.Groups[1].Value).Order(StringComparer.Ordinal);

            if (!original.SequenceEqual(translated, StringComparer.Ordinal))
            {
                throw new DataValidationException("Text", "invalid_placeholders");
            }
        }
    }
}
