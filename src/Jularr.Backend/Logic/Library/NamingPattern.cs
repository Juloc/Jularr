using System.Globalization;
using System.Text.RegularExpressions;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;

namespace Jularr.Backend.Logic.Library
{
    // Sonarr-like tokens in braces, numbers padded as in {Season:00}; an empty token takes its brackets with it.
    internal static partial class NamingPattern
    {
        public static readonly IReadOnlyList<string> Tokens = ["Title", "Year", "Season", "Episode", "Episode Title", "Quality", "Release Group", "Edition", "Proper"];

        public static void Validate(MediaType mediaType, string pattern, string field)
        {
            var names = Token().Matches(pattern).Select(match => match.Groups["name"].Value).ToArray();
            if (names.Any(name => !Tokens.Contains(name, StringComparer.OrdinalIgnoreCase)) || Token().Replace(pattern, "").IndexOfAny(['{', '}']) >= 0)
            {
                throw new DataValidationException(field, "naming_unknown_token");
            }

            string[] required = mediaType == MediaType.Series ? ["Title", "Season", "Episode"] : ["Title"];
            if (required.Any(name => !names.Contains(name, StringComparer.OrdinalIgnoreCase)))
            {
                throw new DataValidationException(field, "naming_incomplete");
            }

            if (pattern.Split('/').Any(segment => segment.Trim() is "" or "." or ".."))
            {
                throw new DataValidationException(field, "naming_invalid_path");
            }
        }

        // A path stem below the library root.
        public static string Apply(string pattern, NamingValues values) => string.Join('/', pattern.Split('/').Select(segment =>
        {
            var text = Token().Replace(segment, match => Value(match.Groups["name"].Value, match.Groups["format"].Value, values));
            text = EmptyBrackets().Replace(text, "");
            return LibraryNaming.SafeName(DanglingSeparator().Replace(text.Trim(), "").Trim());
        }));

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
    }

    internal sealed record NamingValues(string Title, int? Year, decimal? Season, decimal? Episode, string? EpisodeTitle, string? Quality, string? ReleaseGroup, string? Edition, int Revision);
}
