using System.Text.RegularExpressions;

namespace Jularr.Backend.Logic.Acquisition
{
    // The languages a video release name states behind its title, as Sonarr's and Radarr's language parser reads them; an untagged release is English.
    internal static partial class ReleaseLanguages
    {
        private static readonly (Regex Pattern, string Language)[] Words =
        [
            (German(), "de"), (French(), "fr"), (Spanish(), "es"), (Italian(), "it"), (Dutch(), "nl"), (Japanese(), "ja"), (Korean(), "ko"),
            (Chinese(), "zh"), (Russian(), "ru"), (Polish(), "pl"), (Portuguese(), "pt"), (Turkish(), "tr"), (Hindi(), "hi"), (Swedish(), "sv"),
            (Danish(), "da"), (Norwegian(), "no"), (Finnish(), "fi"), (Czech(), "cs"), (Hungarian(), "hu"), (Greek(), "el"), (Arabic(), "ar"),
            (Hebrew(), "he"), (Thai(), "th"), (Vietnamese(), "vi"), (Ukrainian(), "uk"), (Indonesian(), "id"), (English(), "en")
        ];

        public static IReadOnlyList<string> Read(string tail, IReadOnlyList<string> bracketed)
        {
            var languages = bracketed.Select(language => language.ToLowerInvariant()).ToList();
            foreach (var (pattern, language) in Words)
            {
                if (!languages.Contains(language) && pattern.IsMatch(tail))
                {
                    languages.Add(language);
                }
            }

            return languages.Count > 0 ? languages : ["en"];
        }

        [GeneratedRegex(@"\b(?:german|deutsch|ger)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
        private static partial Regex German();

        [GeneratedRegex(@"\b(?:french|truefrench|vostfr|vff|vfq|vf2|fre)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
        private static partial Regex French();

        [GeneratedRegex(@"\b(?:spanish|castellano|latino|espanol|spa)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
        private static partial Regex Spanish();

        [GeneratedRegex(@"\b(?:italian|ita)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
        private static partial Regex Italian();

        [GeneratedRegex(@"\b(?:dutch|flemish|nlsub)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
        private static partial Regex Dutch();

        [GeneratedRegex(@"\b(?:japanese|jap|jpn)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
        private static partial Regex Japanese();

        [GeneratedRegex(@"\b(?:korean|kor)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
        private static partial Regex Korean();

        [GeneratedRegex(@"\b(?:chinese|mandarin|cantonese)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
        private static partial Regex Chinese();

        [GeneratedRegex(@"\b(?:russian|rus)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
        private static partial Regex Russian();

        [GeneratedRegex(@"\b(?:polish|pldub|plsub|pol)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
        private static partial Regex Polish();

        [GeneratedRegex(@"\b(?:portuguese|dublado)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
        private static partial Regex Portuguese();

        [GeneratedRegex(@"\b(?:turkish|tur)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
        private static partial Regex Turkish();

        [GeneratedRegex(@"\bhindi\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
        private static partial Regex Hindi();

        [GeneratedRegex(@"\b(?:swedish|swe)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
        private static partial Regex Swedish();

        [GeneratedRegex(@"\bdanish\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
        private static partial Regex Danish();

        [GeneratedRegex(@"\bnorwegian\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
        private static partial Regex Norwegian();

        [GeneratedRegex(@"\bfinnish\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
        private static partial Regex Finnish();

        [GeneratedRegex(@"\bczech\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
        private static partial Regex Czech();

        [GeneratedRegex(@"\b(?:hungarian|hun)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
        private static partial Regex Hungarian();

        [GeneratedRegex(@"\bgreek\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
        private static partial Regex Greek();

        [GeneratedRegex(@"\barabic\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
        private static partial Regex Arabic();

        [GeneratedRegex(@"\bhebrew\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
        private static partial Regex Hebrew();

        [GeneratedRegex(@"\bthai\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
        private static partial Regex Thai();

        [GeneratedRegex(@"\bvietnamese\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
        private static partial Regex Vietnamese();

        [GeneratedRegex(@"\b(?:ukrainian|ukr)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
        private static partial Regex Ukrainian();

        [GeneratedRegex(@"\bindonesian\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
        private static partial Regex Indonesian();

        [GeneratedRegex(@"\b(?:english|eng)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
        private static partial Regex English();
    }
}
