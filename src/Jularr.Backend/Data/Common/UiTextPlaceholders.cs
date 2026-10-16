using System.Text.RegularExpressions;

namespace Jularr.Backend.Data.Common
{
    internal static partial class UiTextPlaceholders
    {
        [GeneratedRegex(@"(?<!\{)\{([A-Za-z][A-Za-z0-9_]*)\}(?!\})", RegexOptions.CultureInvariant)]
        private static partial Regex NamedPlaceholders();

        internal static void RequireMatching(string englishText, string translatedText)
        {
            if (!Match(englishText, translatedText))
            {
                throw new DataValidationException("Text", "invalid_placeholders");
            }
        }

        // The same named and numbered placeholders, in any order.
        internal static bool Match(string englishText, string translatedText)
        {
            static IEnumerable<string> Names(string text) => NamedPlaceholders().Matches(text).Select(match => match.Groups[1].Value)
                .Concat(NumberedPlaceholders().Matches(text).Select(match => match.Groups[1].Value)).Order(StringComparer.Ordinal);
            return Names(englishText).SequenceEqual(Names(translatedText), StringComparer.Ordinal);
        }

        [GeneratedRegex(@"(?<!\{)\{(\d+)(?:[,:][^}]*)?\}(?!\})", RegexOptions.CultureInvariant)]
        private static partial Regex NumberedPlaceholders();
    }
}
