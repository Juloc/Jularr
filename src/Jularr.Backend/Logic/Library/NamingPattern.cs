using System.Globalization;
using System.Text.RegularExpressions;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;

namespace Jularr.Backend.Logic.Library
{
    // Sonarr-like tokens in braces, numbers padded as in {Season:00}; an empty token takes its brackets with it.
    internal static partial class NamingPattern
    {
        private static readonly IReadOnlyList<string> Tokens = ["Title", "Year", "Season", "Episode", "Episode Title", "Quality", "Release Group", "Edition", "Proper"];

        public static void Validate(MediaType mediaType, string pattern, string field)
        {
            // Only season and episode take a number format.
            var tokens = Token().Matches(pattern);
            var names = tokens.Select(match => match.Groups["name"].Value).ToArray();
            if (names.Any(name => !Tokens.Contains(name, StringComparer.OrdinalIgnoreCase)) || Token().Replace(pattern, "").IndexOfAny(['{', '}']) >= 0
                || tokens.Any(match => match.Groups["format"].Success && match.Groups["name"].Value.ToLowerInvariant() is not ("season" or "episode")))
            {
                throw new DataValidationException(field, "naming_unknown_token");
            }

            string[] required = mediaType == MediaType.Series ? ["Title", "Season", "Episode"] : ["Title"];
            if (required.Any(name => !names.Contains(name, StringComparer.OrdinalIgnoreCase)))
            {
                throw new DataValidationException(field, "naming_incomplete");
            }

            // The file itself is always named by what it is.
            var file = pattern.Split('/')[^1];
            if (pattern.Split('/').Any(segment => segment.Trim() is "" or "." or "..")
                || !file.Contains(mediaType == MediaType.Series ? "{Episode" : "{Title}", StringComparison.OrdinalIgnoreCase))
            {
                throw new DataValidationException(field, "naming_invalid_path");
            }
        }

        // A path stem below the library root; a folder left empty drops out.
        public static string Apply(string pattern, NamingValues values)
        {
            var segments = new List<string>();
            var parts = pattern.Split('/');
            for (var index = 0; index < parts.Length; index++)
            {
                // Empty placeholders leave first, with their brackets and separators; the values never pass through that cleanup.
                var literal = Tidy(Token().Replace(parts[index], match => Value(match.Groups["name"].Value, match.Groups["format"].Value, values).Length > 0 ? match.Value : ""));
                if (literal.Length == 0 && index < parts.Length - 1)
                {
                    continue;
                }

                var text = Token().Replace(literal, match => LibraryNaming.SafeName(Value(match.Groups["name"].Value, match.Groups["format"].Value, values)));
                segments.Add(LibraryNaming.SafeName(TextLimit.Cut(text, 200)!));
            }

            return string.Join('/', segments);
        }

        private static string Tidy(string text)
        {
            text = InnerSpace().Replace(text, "$1$2");
            text = EmptyBrackets().Replace(text, "");
            text = RepeatedSeparator().Replace(text, " -");
            return DanglingSeparator().Replace(Spaces().Replace(text, " ").Trim(), "").Trim();
        }

        private static string Value(string name, string format, NamingValues values) => name.ToLowerInvariant() switch
        {
            "title" => values.Title,
            "year" => values.Year?.ToString(CultureInfo.InvariantCulture) ?? "",
            "season" => Number(values.Season, format),
            "episode" => Number(values.Episode, format),
            "episode title" => values.EpisodeTitle ?? "",
            "quality" => values.Quality ?? "",
            "release group" => values.ReleaseGroup ?? "",
            "edition" => values.Edition ?? "",
            "proper" => values.Revision > 1 ? "Proper" : "",
            _ => ""
        };

        // Half units such as 1.5 keep their fraction.
        private static string Number(decimal? value, string format) => value?.ToString((format.Length > 0 ? format : "0") + ".###", CultureInfo.InvariantCulture) ?? "";

        [GeneratedRegex(@"\{(?<name>[A-Za-z ]+)(?::(?<format>0+))?\}")]
        private static partial Regex Token();

        [GeneratedRegex(@"\(\s*\)|\[\s*\]|\{\s*\}")]
        private static partial Regex EmptyBrackets();

        [GeneratedRegex(@"(?:\s+[-.]+)+$|^(?:[-.]+\s+)+")]
        private static partial Regex DanglingSeparator();

        [GeneratedRegex(@"([\[(])\s+|\s+([\])])")]
        private static partial Regex InnerSpace();

        [GeneratedRegex(@"\s+-(?:\s+-)+(?=\s|$)")]
        private static partial Regex RepeatedSeparator();

        [GeneratedRegex(@"\s{2,}")]
        private static partial Regex Spaces();
    }

    internal sealed record NamingValues(string Title, int? Year, decimal? Season, decimal? Episode, string? EpisodeTitle, string? Quality, string? ReleaseGroup, string? Edition, int Revision);
}
