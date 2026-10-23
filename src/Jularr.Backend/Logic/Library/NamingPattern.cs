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
            var pieces = Parse(pattern);
            if (pieces.Any(piece => piece.Name is null || !ValidFormat(piece.Name, piece.Format))
                || Piece().Replace(pattern, "").IndexOfAny(['{', '}']) >= 0)
            {
                throw new DataValidationException(field, "naming_unknown_token");
            }

            var names = pieces.Select(piece => piece.Name!).ToArray();
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
            var fileNames = Parse(file).Select(piece => piece.Name).ToArray();
            if (parts.Any(part => part.Trim() is "" or "." or "..")
                || mediaType == MediaType.Series && !fileNames.Contains("Episode", StringComparer.OrdinalIgnoreCase)
                || mediaType == MediaType.Movie && !fileNames.Any(name => name?.Contains("Title", StringComparison.OrdinalIgnoreCase) == true))
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
            Parse(pattern.Split('/')[^1]).Any(piece => piece.Name?.ToLowerInvariant() is
                "quality" or "quality full" or "quality title" or "release group" or "edition" or "edition tags");

        // The same syntax as the *arr file name builders: {prefix Token.Separator:format suffix}, a token's case chooses the output case,
        // {{ and }} are braces, and a group like {imdb-{ImdbId}} stays literal and disappears with an empty token.
        public static string Apply(string pattern, NamingValues values)
        {
            var segments = new List<string>();
            var parts = pattern.Split('/');
            for (var index = 0; index < parts.Length; index++)
            {
                // Season 0 goes to its own folder instead of the season folder.
                if (values.Season == 0 && values.Rules.SpecialsFolder is { Length: > 0 } specials && index < parts.Length - 1
                    && Parse(parts[index]).Any(piece => piece.Name == "Season"))
                {
                    segments.Add(LibraryNaming.SafeName(specials));
                    continue;
                }

                // Empty tokens drop out with the brackets and separators around them; what stays is rendered afterwards,
                // so values keep whatever characters they have.
                var rendered = new List<string>();
                var literal = Tidy(Piece().Replace(parts[index], match =>
                {
                    var text = Render(ToPiece(match), values);
                    if (text is null)
                    {
                        return "";
                    }

                    rendered.Add(text);
                    return Marker + (rendered.Count - 1).ToString(CultureInfo.InvariantCulture) + Marker;
                }));
                if (literal.Length == 0 && index < parts.Length - 1)
                {
                    continue;
                }

                var text = Placed().Replace(literal, match => rendered[int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture)]);
                segments.Add(LibraryNaming.SafeName(TextLimit.Cut(text, 200)!));
            }

            return string.Join('/', segments);
        }

        // Private-use characters stand for rendered tokens while the template is tidied.
        private const char Marker = '\uE000';

        private sealed record NamingPiece(string? Name, string Prefix, string Separator, string Format, string Suffix, string Written, string Lead = "", string Trail = "", bool IsLiteral = false, bool IsGroup = false);

        private static NamingPiece ToPiece(Match match)
        {
            if (match.Groups["escaped"].Success)
            {
                return new NamingPiece(null, "", "", "", "", match.Value[..1], IsLiteral: true);
            }

            var group = match.Groups["plead"].Success;
            var token = match.Groups[group ? "ptoken" : "token"].Value;
            var separator = match.Groups[group ? "pseparator" : "separator"].Value;
            var words = separator.Length > 0 ? token.Split(separator.ToCharArray(), StringSplitOptions.RemoveEmptyEntries) : [token];
            var name = NamingTokens.Canonical(string.Join(' ', words));
            return new NamingPiece(name, match.Groups[group ? "pprefix" : "prefix"].Value, separator, match.Groups[group ? "pformat" : "format"].Value,
                match.Groups[group ? "psuffix" : "suffix"].Value, token, match.Groups["plead"].Value, match.Groups["ptrail"].Value, false, group);
        }

        private static IReadOnlyList<NamingPiece> Parse(string pattern) =>
            [.. Piece().Matches(pattern).Select(ToPiece).Where(piece => !piece.IsLiteral)];

        // The text of a piece, or null when it is empty and drops out.
        private static string? Render(NamingPiece piece, NamingValues values)
        {
            if (piece.IsLiteral)
            {
                return piece.Written;
            }

            var value = piece.Name is null ? "" : Value(piece.Name, piece.Format, values);
            if (value.Length == 0)
            {
                return null;
            }

            if (piece.Separator.Trim().Length > 0)
            {
                value = value.Replace(" ", piece.Separator, StringComparison.Ordinal);
            }

            if (piece.Written.Any(char.IsLetter))
            {
                value = piece.Written == piece.Written.ToLowerInvariant() ? value.ToLowerInvariant()
                    : piece.Written == piece.Written.ToUpperInvariant() ? value.ToUpperInvariant() : value;
            }

            value = piece.Prefix + value + piece.Suffix;
            return piece.IsGroup ? "{" + piece.Lead + value + piece.Trail + "}" : value;
        }

        private static bool ValidFormat(string name, string format)
        {
            if (format.Length == 0)
            {
                return true;
            }

            if (NamingTokens.Numbered.Contains(name))
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

            if (name == "Custom Formats")
            {
                return format.Length <= 300 && !format.Contains('/');
            }

            if (name is "MediaInfo Full" or "MediaInfo AudioLanguages" or "MediaInfo AudioLanguagesAll" or "MediaInfo SubtitleLanguages" or "MediaInfo SubtitleLanguagesAll")
            {
                return LanguageFilter().IsMatch(format);
            }

            // A length cuts any text; a title may name the language it is written in.
            return int.TryParse(format, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var count)
                && count != 0 && Math.Abs((long)count) <= 200
                || name.Contains("Title", StringComparison.OrdinalIgnoreCase) && format.Length == 2 && format.All(char.IsLetter);
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
                "Episode CleanTitle" => values.EpisodeTitle is { } episodeTitle ? Clean(episodeTitle) : "",
                "Year" or "Release Year" or "Series Year" => year,
                "Season" => Number(values.Season, format),
                "Episode" => Number(values.Episode, format),
                "Absolute" => Number(values.Absolute, format),
                "Air Date" => values.AirDate?.ToString("yyyy MM dd", CultureInfo.InvariantCulture) ?? "",
                "Quality" or "Quality Title" => values.Quality ?? "",
                "Quality Full" => (values.Quality ?? "") + (values.Revision > 1 ? " Proper" : ""),
                "Quality Proper" or "Proper" => values.Revision > 1 ? "Proper" : "",
                "Quality Real" => values.IsReal ? "REAL" : "",
                "Release Group" => values.ReleaseGroup ?? "",
                "Release Hash" => values.ReleaseHash ?? "",
                "Edition" or "Edition Tags" => values.Edition ?? "",
                "Original Title" => values.OriginalReleaseTitle ?? "",
                "Original Filename" => values.OriginalFilename ?? "",
                "Custom Formats" => CustomFormats(values.CustomFormats, format),
                "Custom Format" => values.CustomFormats?.GetValueOrDefault(format) ?? "",
                "MediaInfo Audio" => values.MediaInfo?.GetValueOrDefault("MediaInfo AudioCodec") ?? "",
                "MediaInfo Video" => values.MediaInfo?.GetValueOrDefault("MediaInfo VideoCodec") ?? "",
                _ when name.StartsWith("MediaInfo ", StringComparison.Ordinal) => values.MediaInfo?.GetValueOrDefault(name) ?? "",
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

            if (format.Length > 0 && name is "MediaInfo Full" or "MediaInfo AudioLanguages" or "MediaInfo AudioLanguagesAll" or "MediaInfo SubtitleLanguages" or "MediaInfo SubtitleLanguagesAll")
            {
                value = FilterLanguages(value, format);
            }

            // A value never opens a folder or ends in a dot; a cut adds its dots afterwards.
            value = LibraryNaming.CleanPart(ReplaceColons(value, values.Rules), values.Rules.ReplacesIllegalCharacters).Trim('.', ' ');

            return format.Length > 0 && int.TryParse(format, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var limit) && !NamingTokens.Numbered.Contains(name)
                ? TruncateUtf8(value, limit)
                : value;
        }

        // Smart turns ": " into " - " and a colon without a space into a dash; the others replace every colon alike.
        private static string ReplaceColons(string value, NamingRules rules) => !rules.ReplacesIllegalCharacters || !value.Contains(':') ? value : rules.ColonReplacement switch
        {
            ColonReplacement.Delete => value.Replace(":", "", StringComparison.Ordinal),
            ColonReplacement.Dash => value.Replace(":", "-", StringComparison.Ordinal),
            ColonReplacement.SpaceDash => value.Replace(":", " -", StringComparison.Ordinal),
            ColonReplacement.SpaceDashSpace => value.Replace(":", " - ", StringComparison.Ordinal),
            ColonReplacement.Custom => value.Replace(":", rules.CustomColonReplacement ?? "", StringComparison.Ordinal),
            _ => value.Replace(": ", " - ", StringComparison.Ordinal).Replace(":", "-", StringComparison.Ordinal)
        };

        // {Custom Formats:A,B} names only these, {Custom Formats:-A,B} all but these.
        private static string CustomFormats(IReadOnlyDictionary<string, string>? formats, string format)
        {
            if (formats is null)
            {
                return "";
            }

            var exclude = format.StartsWith('-');
            var named = format.TrimStart('-').Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
            return string.Join(' ', formats.Where(item => named.Count == 0 || named.Contains(item.Key) != exclude).Select(item => item.Value));
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

        // A token, an escaped brace, or a literal group around a token ({imdb-{ImdbId}}).
        [GeneratedRegex(@"(?<escaped>\{\{|\}\})|\{(?<prefix>[- ._\[(]*)(?<token>[a-z0-9]+(?:(?<separator>[- ._]+)[a-z0-9]+)?)(?::(?<format>[ ,a-z0-9_+-]+(?<!-)))?(?<suffix>[- ._)\]]*)\}"
            + @"|\{(?<plead>[^{}]+?)\{(?<pprefix>[- ._\[(]*)(?<ptoken>[a-z0-9]+(?:(?<pseparator>[- ._]+)[a-z0-9]+)?)(?::(?<pformat>[ ,a-z0-9_+-]+(?<!-)))?(?<psuffix>[- ._)\]]*)\}(?<ptrail>[^{}]*)\}", RegexOptions.IgnoreCase)]
        private static partial Regex Piece();

        [GeneratedRegex("\uE000([0-9]+)\uE000")]
        private static partial Regex Placed();

        [GeneratedRegex(@"^[a-z_]{2,16}$", RegexOptions.IgnoreCase)]
        private static partial Regex Namespace();

        [GeneratedRegex(@"^(?:[A-Za-z]{2}(?:[+-][A-Za-z]{2})*[+]?|-[A-Za-z]{2}(?:-[A-Za-z]{2})*)$")]
        private static partial Regex LanguageFilter();

        [GeneratedRegex(@"\[(?<codes>[A-Z]{2}(?:\+[A-Z]{2})*)\]")]
        private static partial Regex LanguageGroup();

        [GeneratedRegex(@"\(\s*\)|\[\s*\]")]
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

    // How values become file names; the defaults are the *arrs'.
    internal sealed record NamingRules(bool ReplacesIllegalCharacters, ColonReplacement ColonReplacement, string? CustomColonReplacement, string? SpecialsFolder)
    {
        public static NamingRules Default { get; } = new(true, ColonReplacement.Smart, null, "Specials");
    }

    internal sealed record NamingValues(string Title, int? Year, decimal? Season, decimal? Episode, string? EpisodeTitle, string? Quality, string? ReleaseGroup, string? Edition, int Revision)
    {
        public MediaType MediaTypeId { get; init; } = MediaType.Movie;
        public IReadOnlyDictionary<string, string>? ProviderIds { get; init; }
        public NamingRules Rules { get; init; } = NamingRules.Default;
        public decimal? Absolute { get; init; }
        public DateOnly? AirDate { get; init; }
        public bool IsReal { get; init; }
        public string? ReleaseHash { get; init; }
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
