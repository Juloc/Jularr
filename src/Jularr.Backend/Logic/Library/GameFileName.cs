using System.Text.RegularExpressions;

namespace Jularr.Backend.Logic.Library
{
    // What the name of a game file says about it: the region and version dump collections put in their names, "(USA, Europe)" and "(Rev 1)" or "[v65536]".
    internal static partial class GameFileName
    {
        private static readonly HashSet<string> Regions = new(StringComparer.OrdinalIgnoreCase)
        {
            "World", "USA", "Europe", "Japan", "Asia", "Australia", "Brazil", "Canada", "China", "Denmark", "Finland", "France", "Germany", "Greece", "Hong Kong", "Italy", "Korea", "Mexico",
            "Netherlands", "Norway", "Poland", "Portugal", "Russia", "Scandinavia", "Spain", "Sweden", "Taiwan", "UK", "Unknown", "Argentina", "India", "Ireland", "Austria", "Switzerland"
        };

        public static (string? Region, string? Version) Facts(string relativePath)
        {
            var name = Path.GetFileNameWithoutExtension(relativePath.Replace('\\', '/').Split('/')[^1]);
            string? region = null;
            string? version = null;
            foreach (Match group in Parenthesized().Matches(name))
            {
                var text = group.Groups[1].Value.Trim();
                if (region is null && text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries) is { Length: > 0 } parts && parts.All(Regions.Contains))
                {
                    region = string.Join(", ", parts);
                }
                else if (version is null && Revision().IsMatch(text))
                {
                    version = text;
                }
            }

            if (version is null && Bracketed().Match(name) is { Success: true } bracket)
            {
                version = "v" + bracket.Groups[1].Value;
            }

            if (version is null && Bare().Match(name) is { Success: true } bare)
            {
                version = "v" + bare.Groups[1].Value;
            }

            return (region, version);
        }

        [GeneratedRegex(@"\(([^)]*)\)")]
        private static partial Regex Parenthesized();

        [GeneratedRegex(@"^(?:v\d+(?:\.\d+)*[a-z]?|Rev\s?[0-9A-Z]+)$", RegexOptions.IgnoreCase)]
        private static partial Regex Revision();

        [GeneratedRegex(@"\[v(\d+(?:\.\d+)*)\]", RegexOptions.IgnoreCase)]
        private static partial Regex Bracketed();

        [GeneratedRegex(@"(?<![\w.])v(\d+(?:\.\d+){1,3})(?![\w])", RegexOptions.IgnoreCase)]
        private static partial Regex Bare();
    }
}
