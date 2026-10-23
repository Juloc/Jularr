using System.Globalization;
using Jularr.Frontend.Framework;

namespace Jularr.Frontend.Pages.Admin.Work
{
    // A language of a file as a flag, its ISO code or its name; flags are fixed-colour icons that no theme changes.
    internal static class WorkLanguages
    {
        public enum Mode
        {
            Flags,
            Iso,
            Names
        }

        private static readonly Dictionary<string, string> TwoLetter = new(StringComparer.OrdinalIgnoreCase)
        {
            ["jpn"] = "ja", ["eng"] = "en", ["ger"] = "de", ["deu"] = "de", ["fre"] = "fr", ["fra"] = "fr", ["spa"] = "es", ["ita"] = "it", ["dut"] = "nl", ["nld"] = "nl", ["pol"] = "pl", ["por"] = "pt",
            ["rus"] = "ru", ["kor"] = "ko", ["chi"] = "zh", ["zho"] = "zh"
        };

        private static readonly HashSet<string> Flags = ["ja", "en", "de", "fr", "es", "it", "nl", "pl", "pt", "ru", "ko", "zh"];

        // The flag of a locale's language as a field icon, the globe where the set has none.
        public static IUiComponent FlagIcon(string locale, FrontendAssets assets, int size = 16)
        {
            var code = Code(locale);
            return new Icon(Flags.Contains(code) ? "flag-" + code : "globe", assets, size);
        }

        public static string Code(string value)
        {
            var code = value.Trim().ToLowerInvariant();
            return TwoLetter.TryGetValue(code, out var two) ? two : code.Length > 2 && code[2] is '-' or '_' ? code[..2] : code;
        }

        public static string Name(string code)
        {
            try
            {
                return CultureInfo.GetCultureInfo(code).DisplayName;
            }
            catch (CultureNotFoundException)
            {
                return code.ToUpperInvariant();
            }
        }

        public static IUiComponent Render(string code, Mode mode, FrontendAssets assets)
        {
            var name = Name(code);
            if (mode == Mode.Flags && Flags.Contains(code))
            {
                return new Span(new Icon("flag-" + code, assets, 22, "1.8")).Class("work-flag").Attribute("title", name).Attribute("role", "img").Attribute("aria-label", name);
            }

            return new Span(new ValueText(mode == Mode.Names ? name : code.ToUpperInvariant())).Class("work-language");
        }
    }
}
