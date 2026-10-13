using System.Collections.Frozen;
using System.Globalization;

namespace Jularr.Frontend.Framework
{
    // Track languages come as ISO codes ("de", "ger", "deu"); people read their native names.
    public static class LanguageLabel
    {
        // Bibliographic ISO 639-2 codes that differ from the terminology codes .NET knows.
        private static readonly FrozenDictionary<string, string> Bibliographic = new Dictionary<string, string>
        {
            ["alb"] = "sqi", ["arm"] = "hye", ["baq"] = "eus", ["bur"] = "mya", ["chi"] = "zho", ["cze"] = "ces", ["dut"] = "nld", ["fre"] = "fra", ["geo"] = "kat",
            ["ger"] = "deu", ["gre"] = "ell", ["ice"] = "isl", ["mac"] = "mkd", ["mao"] = "mri", ["may"] = "msa", ["per"] = "fas", ["rum"] = "ron", ["slo"] = "slk",
            ["tib"] = "bod", ["wel"] = "cym"
        }.ToFrozenDictionary();

        private static readonly FrozenDictionary<string, CultureInfo> ThreeLetter = CultureInfo.GetCultures(CultureTypes.NeutralCultures)
            .Where(culture => culture.ThreeLetterISOLanguageName.Length == 3)
            .GroupBy(culture => culture.ThreeLetterISOLanguageName, StringComparer.OrdinalIgnoreCase)
            .ToFrozenDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        public static string? Name(string? code)
        {
            if (string.IsNullOrWhiteSpace(code) || code.Equals("und", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var key = Bibliographic.GetValueOrDefault(code.ToLowerInvariant(), code);
            if (ThreeLetter.TryGetValue(key, out var culture))
            {
                return Capitalize(culture.NativeName);
            }

            try
            {
                return Capitalize(CultureInfo.GetCultureInfo(code).NativeName);
            }
            catch (CultureNotFoundException)
            {
                return code;
            }
        }

        private static string Capitalize(string name) => name.Length == 0 ? name : char.ToUpper(name[0], CultureInfo.InvariantCulture) + name[1..];
    }
}
