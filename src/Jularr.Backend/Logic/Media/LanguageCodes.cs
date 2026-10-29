using System.Collections.Frozen;
using System.Globalization;
using System.Text.Json;
using Jularr.Backend.Data.Logic.Media;

namespace Jularr.Backend.Logic.Media
{
    // ISO 639 codes to their two-letter form, including the bibliographic forms subtitle files and streams use.
    internal static class LanguageCodes
    {
        private static readonly FrozenDictionary<string, string> TwoLetter = CultureInfo.GetCultures(CultureTypes.NeutralCultures)
            .Where(culture => culture.TwoLetterISOLanguageName.Length == 2 && culture.TwoLetterISOLanguageName != "iv")
            .SelectMany(culture => new[] { (culture.TwoLetterISOLanguageName, culture.TwoLetterISOLanguageName), (culture.ThreeLetterISOLanguageName, culture.TwoLetterISOLanguageName) })
            .Concat([("alb", "sq"), ("arm", "hy"), ("baq", "eu"), ("bur", "my"), ("chi", "zh"), ("cze", "cs"), ("dut", "nl"), ("fre", "fr"), ("geo", "ka"), ("ger", "de"), ("gre", "el"),
                ("ice", "is"), ("mac", "mk"), ("mao", "mi"), ("may", "ms"), ("per", "fa"), ("rum", "ro"), ("slo", "sk"), ("tib", "bo"), ("wel", "cy")])
            .DistinctBy(pair => pair.Item1, StringComparer.OrdinalIgnoreCase)
            .ToFrozenDictionary(pair => pair.Item1, pair => pair.Item2, StringComparer.OrdinalIgnoreCase);

        private static readonly FrozenDictionary<string, string> ThreeLetter = CultureInfo.GetCultures(CultureTypes.NeutralCultures)
            .Where(culture => culture.TwoLetterISOLanguageName.Length == 2 && culture.TwoLetterISOLanguageName != "iv" && culture.ThreeLetterISOLanguageName.Length == 3)
            .DistinctBy(culture => culture.TwoLetterISOLanguageName, StringComparer.OrdinalIgnoreCase)
            .ToFrozenDictionary(culture => culture.TwoLetterISOLanguageName, culture => culture.ThreeLetterISOLanguageName, StringComparer.OrdinalIgnoreCase);

        // "de", "deu", "ger" and "de-AT" are German; anything else is unknown.
        public static string? ToTwoLetter(string? code) => code is null ? null : TwoLetter.GetValueOrDefault(code.Split('-', '_')[0]);

        // The terminology form TheTVDB uses: "de" and "de-AT" are "deu".
        public static string? ToThreeLetter(string? code) => ToTwoLetter(code) is { } two ? ThreeLetter.GetValueOrDefault(two) : null;

        // Card SQL compares indexed facts without maintaining another ISO mapping.
        internal static LanguageAliasesData Aliases { get; } = new(JsonSerializer.Serialize(TwoLetter));
    }
}
