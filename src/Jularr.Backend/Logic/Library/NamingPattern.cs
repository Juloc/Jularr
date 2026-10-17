using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Library;
using Jularr.Backend.Data.Enums.Media;

namespace Jularr.Backend.Logic.Library
{
    // One naming engine for completed downloads and already scanned files.
    internal static partial class NamingPattern
    {
        public static void Validate(MediaType mediaType, string pattern, string field)
        {
            var tokens = Token().Matches(pattern);
            var names = tokens.Select(match => Normalize(match.Groups["name"].Value)).ToArray();
            if (names.Any(name => !NamingTokens.Supported.Contains(name, StringComparer.OrdinalIgnoreCase))
                || Token().Replace(pattern, "").IndexOfAny(['{', '}']) >= 0
                || tokens.Any(match => !ValidFormat(Normalize(match.Groups["name"].Value), match.Groups["format"].Value)))
            {
                throw new DataValidationException(field, "naming_unknown_token");
            }

            var hasTitle = names.Any(name => name.Equals("Title", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith(mediaType == MediaType.Series ? "Series " : "Movie ", StringComparison.OrdinalIgnoreCase)
                && name.Contains("Title", StringComparison.OrdinalIgnoreCase));
            if (!hasTitle || mediaType == MediaType.Series
                && (!names.Contains("Season", StringComparer.OrdinalIgnoreCase) || !names.Contains("Episode", StringComparer.OrdinalIgnoreCase)))
            {
                throw new DataValidationException(field, "naming_incomplete");
            }

            var parts = pattern.Split('/');
            var file = parts[^1];
            if (parts.Any(part => part.Trim() is "" or "." or "..")
                || mediaType == MediaType.Series && !file.Contains("Episode", StringComparison.OrdinalIgnoreCase)
                || mediaType == MediaType.Movie && !Token().Matches(file).Select(match => Normalize(match.Groups["name"].Value))
                    .Any(name => name.Contains("Title", StringComparison.OrdinalIgnoreCase)))
            {
                throw new DataValidationException(field, "naming_invalid_path");
            }
        }

        public static string Join(string? folder, string file) => string.IsNullOrWhiteSpace(folder) ? file : folder.Trim().Trim('/') + "/" + file.Trim();

        public static (string? Folder, string File) Split(string pattern) =>
            pattern.LastIndexOf('/') is var slash and >= 0 ? (pattern[..slash], pattern[(slash + 1)..]) : (null, pattern);

        public static string Example(MediaType mediaType, string pattern) => Apply(pattern, mediaType == MediaType.Series
            ? new NamingValues("Example Show", 2020, 1, 2, "The Second", "WEBDL-1080p", "GRP", null, 1) { MediaTypeId = MediaType.Series }
            : new NamingValues("Example Movie", 1999, null, null, null, "Bluray-1080p", "GRP", null, 1)) + ".mkv";

        public static bool Distinguishes(string pattern) =>
            Token().Matches(pattern.Split('/')[^1]).Any(match => Normalize(match.Groups["name"].Value).ToLowerInvariant() is
                "quality" or "quality full" or "quality title" or "release group" or "edition" or "edition tags");

        public static string Apply(string pattern, NamingValues values)
        {
            var segments = new List<string>();
            var parts = pattern.Split('/');
            for (var index = 0; index < parts.Length; index++)
            {
                var literal = Tidy(Token().Replace(parts[index], match => Render(match, values).Length > 0 ? match.Value : ""));
                if (literal.Length == 0 && index < parts.Length - 1)
                {
                    continue;
                }

                var text = Token().Replace(literal, match => Render(match, values));
                segments.Add(LibraryNaming.SafeName(TextLimit.Cut(text, 200)!));
            }

            return string.Join('/', segments);
        }

        private static string Render(Match match, NamingValues values)
        {
            var value = Value(Normalize(match.Groups["name"].Value), match.Groups["format"].Value, values);
            if (value.Length == 0)
            {
                return "";
            }

            if (match.Groups["name"].Value.Contains('.'))
            {
                value = Regex.Replace(value, @"\s+", ".");
            }

            value = LibraryNaming.SafeName(value);
            return match.Groups["prefix"].Value switch
            {
                "[" when match.Groups["suffix"].Value == "]" => "[" + value + "]",
                "-" => "-" + value,
                "." => "." + value,
                _ => value
            };
        }

        private static bool ValidFormat(string name, string format)
        {
            if (format.Length == 0)
            {
                return true;
            }

            if (name is "Season" or "Episode")
            {
                return format.All(character => character == '0');
            }

            if (name == "ProviderId")
            {
                return Namespace().IsMatch(format);
            }

            if (name == "Custom Format")
            {
                return format.Length is > 0 and <= 128 && !format.Contains('/');
            }

            if (name is "MediaInfo Full" or "MediaInfo AudioLanguages" or "MediaInfo AudioLanguagesAll" or "MediaInfo SubtitleLanguages")
            {
                return LanguageFilter().IsMatch(format);
            }

            if (name.Contains("Title", StringComparison.OrdinalIgnoreCase) || name is "Release Group" or "Edition Tags")
            {
                return int.TryParse(format, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var count)
                    && count != 0 && Math.Abs((long)count) <= 200
                    || name.Contains("Title", StringComparison.OrdinalIgnoreCase) && format.Length == 2 && format.All(char.IsLetter);
            }

            return false;
        }

        private static string Value(string name, string format, NamingValues values)
        {
            var localized = format.Length == 2 && values.LocalizedTitles?.GetValueOrDefault(format.ToLowerInvariant()) is { } translated
                ? translated : values.Title;
            var title = name.Contains("Clean", StringComparison.OrdinalIgnoreCase) ? Clean(localized) : localized;
            var year = values.Year?.ToString(CultureInfo.InvariantCulture) ?? "";
            var finalTitle = name.Contains("The", StringComparison.OrdinalIgnoreCase) ? MoveThe(title) : title;
            var value = name switch
            {
                "Title" or "Movie Title" or "Movie CleanTitle" or "Movie TitleThe" or "Movie CleanTitleThe"
                    or "Series Title" or "Series CleanTitle" or "Series TitleWithoutYear" or "Series CleanTitleWithoutYear"
                    or "Series TitleThe" or "Series CleanTitleThe" or "Series TitleTheWithoutYear" or "Series CleanTitleTheWithoutYear" => finalTitle,
                "Series TitleYear" or "Series TitleTheYear" => finalTitle + (year.Length > 0 ? " (" + year + ")" : ""),
                "Series CleanTitleYear" or "Series CleanTitleTheYear" => finalTitle + (year.Length > 0 ? " " + year : ""),
                "Movie OriginalTitle" => values.OriginalTitle ?? "",
                "Movie CleanOriginalTitle" => values.OriginalTitle is { } original ? Clean(original) : "",
                "Movie TitleFirstCharacter" or "Series TitleFirstCharacter" => title.Length > 0 ? title[..1] : "",
                "Movie Collection" => values.Collection ?? "",
                "Movie CollectionThe" => values.Collection is { } collection ? MoveThe(collection) : "",
                "Movie CleanCollectionThe" => values.Collection is { } cleaned ? MoveThe(Clean(cleaned)) : "",
                "Movie Certification" => values.Certification ?? "",
                "Episode Title" => values.EpisodeTitle ?? "",
                "Year" or "Release Year" or "Series Year" => year,
                "Season" => Number(values.Season, format),
                "Episode" => Number(values.Episode, format),
                "Quality" or "Quality Title" => values.Quality ?? "",
                "Quality Full" => (values.Quality ?? "") + (values.Revision > 1 ? " Proper" : ""),
                "Release Group" => values.ReleaseGroup ?? "",
                "Edition" or "Edition Tags" => values.Edition ?? "",
                "Proper" => values.Revision > 1 ? "Proper" : "",
                "Original Title" => values.OriginalReleaseTitle ?? "",
                "Original Filename" => values.OriginalFilename ?? "",
                "Custom Formats" => values.CustomFormats is null ? "" : string.Join(' ', values.CustomFormats.Values),
                "Custom Format" => values.CustomFormats?.GetValueOrDefault(format) ?? "",
                "MediaInfo Simple" or "MediaInfo Full" or "MediaInfo AudioCodec" or "MediaInfo AudioChannels"
                    or "MediaInfo AudioLanguages" or "MediaInfo AudioLanguagesAll" or "MediaInfo SubtitleLanguages"
                    or "MediaInfo VideoCodec" => values.MediaInfo?.GetValueOrDefault(name) ?? "",
                "ImdbId" => Id(values, "imdb"),
                "TmdbId" => Id(values, values.MediaTypeId == MediaType.Series ? "tmdb_tv" : "tmdb_movie"),
                "TvdbId" => Id(values, "tvdb"),
                "TvMazeId" => Id(values, "tvmaze"),
                "AnilistId" => Id(values, "anilist"),
                "MusicBrainzId" => Id(values, "musicbrainz"),
                "OpenLibraryId" => Id(values, "openlibrary"),
                "IgdbId" => Id(values, "igdb"),
                "GutenbergId" => Id(values, "gutenberg"),
                "IsbnId" => Id(values, "isbn"),
                "PlexId" => Id(values, "plex"),
                "ProviderId" => Id(values, format),
                _ => ""
            };

            if (format.Length > 0 && name is "MediaInfo Full" or "MediaInfo AudioLanguages" or "MediaInfo AudioLanguagesAll" or "MediaInfo SubtitleLanguages")
            {
                value = FilterLanguages(value, format);
            }

            return format.Length > 0 && int.TryParse(format, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var limit)
                && (name.Contains("Title", StringComparison.OrdinalIgnoreCase) || name is "Release Group" or "Edition Tags")
                ? TruncateUtf8(value, limit)
                : value;
        }

        private static string FilterLanguages(string value, string format)
        {
            var exclusions = format.StartsWith("-", StringComparison.Ordinal);
            var placeholders = format.EndsWith("+", StringComparison.Ordinal);
            var permitted = format.TrimStart('-').TrimEnd('+')
                .Split(exclusions ? '-' : '+', StringSplitOptions.RemoveEmptyEntries)
                .Select(code => code.ToUpperInvariant()).ToHashSet(StringComparer.Ordinal);
            return LanguageGroup().Replace(value, match =>
            {
                var codes = match.Groups["codes"].Value.Split('+');
                var accepted = codes.Where(code => exclusions ? !permitted.Contains(code) : permitted.Contains(code)).ToList();
                if (placeholders && codes.Length > accepted.Count)
                {
                    accepted.AddRange(Enumerable.Repeat("--", codes.Length - accepted.Count));
                }

                return accepted.Count == 0 ? "" : "[" + string.Join('+', accepted) + "]";
            }).Trim();
        }

        private static string Id(NamingValues values, string @namespace) => values.ProviderIds?.GetValueOrDefault(@namespace) ?? "";

        private static string Normalize(string name) => name.Trim().Replace('.', ' ');

        private static string Clean(string title) => Regex.Replace(title, @"[^\p{L}\p{N} \-]", "").Trim();

        private static string MoveThe(string title) => title.StartsWith("The ", StringComparison.OrdinalIgnoreCase)
            ? title[4..] + ", " + title[..3] : title;

        private static string TruncateUtf8(string text, int limit)
        {
            var max = Math.Abs(limit);
            if (Encoding.UTF8.GetByteCount(text) <= max)
            {
                return text;
            }

            const string dots = "...";
            if (max <= 3)
            {
                return dots[..max];
            }

            var runes = text.EnumerateRunes().Select(rune => rune.ToString()).ToArray();
            var accepted = new List<string>();
            var length = 3;
            foreach (var part in limit > 0 ? runes : runes.Reverse())
            {
                var bytes = Encoding.UTF8.GetByteCount(part);
                if (length + bytes > max)
                {
                    break;
                }

                accepted.Add(part);
                length += bytes;
            }

            if (limit < 0)
            {
                accepted.Reverse();
            }

            return limit > 0 ? string.Concat(accepted) + dots : dots + string.Concat(accepted);
        }

        private static string Tidy(string text)
        {
            text = InnerSpace().Replace(text, "$1$2");
            text = EmptyBrackets().Replace(text, "");
            text = RepeatedSeparator().Replace(text, " -");
            return DanglingSeparator().Replace(Spaces().Replace(text, " ").Trim(), "").Trim();
        }

        private static string Number(decimal? value, string format) =>
            value?.ToString((format.Length > 0 ? format : "0") + ".###", CultureInfo.InvariantCulture) ?? "";

        [GeneratedRegex(@"\{(?<prefix>[\[.-]?)(?<name>[A-Za-z][A-Za-z .]*?)(?<suffix>\]?)(?::(?<format>[^{}]+))?\}")]
        private static partial Regex Token();

        [GeneratedRegex(@"^[a-z_]{2,16}$", RegexOptions.IgnoreCase)]
        private static partial Regex Namespace();

        [GeneratedRegex(@"^(?:[A-Za-z]{2}(?:[+-][A-Za-z]{2})*[+]?|-[A-Za-z]{2}(?:-[A-Za-z]{2})*)$")]
        private static partial Regex LanguageFilter();

        [GeneratedRegex(@"\[(?<codes>[A-Z]{2}(?:\+[A-Z]{2})*)\]")]
        private static partial Regex LanguageGroup();

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

    internal sealed record NamingValues(string Title, int? Year, decimal? Season, decimal? Episode, string? EpisodeTitle, string? Quality, string? ReleaseGroup, string? Edition, int Revision)
    {
        public MediaType MediaTypeId { get; init; } = MediaType.Movie;
        public IReadOnlyDictionary<string, string>? ProviderIds { get; init; }
        public string? OriginalTitle { get; init; }
        public string? OriginalFilename { get; init; }
        public string? OriginalReleaseTitle { get; init; }
        public string? Collection { get; init; }
        public string? Certification { get; init; }
        public IReadOnlyDictionary<string, string>? MediaInfo { get; init; }
        public IReadOnlyDictionary<string, string>? CustomFormats { get; init; }
        public IReadOnlyDictionary<string, string>? LocalizedTitles { get; init; }
    }
}
