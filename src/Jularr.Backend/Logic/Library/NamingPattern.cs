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
        public static void Validate(MediaType mediaType, string pattern, string field, bool daily = false)
        {
            var pieces = ValidateTokens(pattern, field);

            var names = pieces.Select(piece => piece.Name!).ToArray();
            var hasTitle = names.Any(name => name.Equals("Title", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith(mediaType == MediaType.Series ? "Series " : "Movie ", StringComparison.OrdinalIgnoreCase)
                && name.Contains("Title", StringComparison.OrdinalIgnoreCase));
            hasTitle |= names.Any(name => name is "Book Title" or "Book CleanTitle" or "Album" or "Album Title" or "Album CleanTitle" or "Game Title");
            var hasEpisode = names.Contains("Episode", StringComparer.OrdinalIgnoreCase) && names.Contains("Season", StringComparer.OrdinalIgnoreCase)
                || names.Contains("Absolute", StringComparer.OrdinalIgnoreCase);
            if (!hasTitle || mediaType == MediaType.Series && !(daily ? names.Contains("Air Date", StringComparer.OrdinalIgnoreCase) : hasEpisode))
            {
                throw new DataValidationException(field, "naming_incomplete");
            }

            var parts = pattern.Split('/');
            var file = parts[^1];
            var fileNames = Parse(file).Select(piece => piece.Name).ToArray();
            if (parts.Any(part => part.Trim() is "" or "." or "..")
                || mediaType == MediaType.Series && !fileNames.Contains(daily ? "Air Date" : "Episode", StringComparer.OrdinalIgnoreCase)
                    && !fileNames.Contains("Absolute", StringComparer.OrdinalIgnoreCase)
                || mediaType == MediaType.Movie && !fileNames.Any(name => name?.Contains("Title", StringComparison.OrdinalIgnoreCase) == true))
            {
                throw new DataValidationException(field, "naming_invalid_path");
            }
        }

        public static void ValidateRules(NamingRules rules)
        {
            if (rules.ColonReplacement == ColonReplacement.Custom && rules.CustomColonReplacement is null)
                throw new DataValidationException(nameof(rules.CustomColonReplacement), "required");
            if (rules.CustomColonReplacement?.IndexOfAny(['/', '\\', ':']) >= 0)
                throw new DataValidationException(nameof(rules.CustomColonReplacement), "invalid_value");
            if (rules.SpecialsFolder is { } specials) ValidateFolder(specials, nameof(rules.SpecialsFolder));
        }

        public static void ValidateDaily(MediaType type, MediaSubtype? subtype, string? folder, string? file, string field)
        {
            if (file is null) return;
            if (type != MediaType.Series || subtype is not null) throw new DataValidationException(field, "invalid_value");
            var pattern = Join(folder, file);
            if (pattern.Length > 500) throw new DataValidationException(field, "too_long");
            Validate(MediaType.Series, pattern, field, daily: true);
        }

        public static void ValidateFolder(string pattern, string field)
        {
            ValidateTokens(pattern, field);
            if (pattern.Trim() is "" or "." or ".." || pattern.IndexOfAny(['/', '\\']) >= 0)
            {
                throw new DataValidationException(field, "naming_invalid_folder");
            }
        }

        private static IReadOnlyList<NamingPiece> ValidateTokens(string pattern, string field)
        {
            var pieces = Parse(pattern);
            if (pieces.Any(piece => piece.Name is null || !ValidFormat(piece.Name, piece.Format))
                || Piece().Replace(pattern, "").IndexOfAny(['{', '}']) >= 0)
            {
                throw new DataValidationException(field, "naming_unknown_token");
            }

            return pieces;
        }

        public static string Join(string? folder, string file) => string.IsNullOrWhiteSpace(folder) ? file : folder.Trim().Trim('/') + "/" + file.Trim();

        public static (string? Folder, string File) Split(string pattern) =>
            pattern.LastIndexOf('/') is var slash and >= 0 ? (pattern[..slash], pattern[(slash + 1)..]) : (null, pattern);

        public static string Example(MediaType mediaType, string pattern, MediaSubtype? subtype = null, bool multiple = false, NamingRules? rules = null, bool specials = false)
        {
            var title = mediaType == MediaType.Series ? "The Series Title's!" : mediaType == MediaType.Movie ? "The Movie: Title" : mediaType == MediaType.Music ? "Example Album" : "Example Title";
            return Apply(pattern, new NamingValues(title, 2010, specials ? 0 : 1, 1, multiple ? "Episode Title" : "Episode Title (1)", mediaType == MediaType.Movie ? "Bluray-1080p" : "WEBDL-1080p", mediaType == MediaType.Movie ? "EVOLVE" : "RlsGrp", null, 2)
            {
                MediaTypeId = mediaType, MediaSubtypeId = subtype, Rules = rules ?? NamingRules.Default,
                EpisodeNumbers = multiple ? [1, 2, 3] : [], AbsoluteNumbers = multiple ? [12, 13, 14] : [], Absolute = 12, AirDate = new DateOnly(2013, 10, 30),
                Author = "Example Author", Artist = "Example Artist", TrackTitle = "Example Track", Track = 2, Disc = 1, Volume = 1, Platform = "Example Platform",
                Language = "EN", OriginalFilename = "Original.Release", Region = "Europe",
                MediaInfo = new Dictionary<string, string> { ["MediaInfo Full"] = "AVC DTS[DE] [EN+DE]", ["MediaInfo VideoCodec"] = "AVC", ["MediaInfo AudioCodec"] = "DTS", ["MediaInfo AudioLanguages"] = "[DE]", ["MediaInfo SubtitleLanguages"] = "[EN+DE]" },
                ProviderIds = new Dictionary<string, string> { ["imdb"] = mediaType == MediaType.Movie ? "tt0066921" : "tt12345", ["anilist"] = "12345", ["openlibrary"] = "OL12345W", ["musicbrainz"] = "01234567-89ab-cdef-0123-456789abcdef", ["igdb"] = "12345" }
            }) + "." + (mediaType == MediaType.Book ? subtype == MediaSubtype.Manga ? "cbz" : "epub" : mediaType == MediaType.Music ? "flac" : mediaType == MediaType.Game ? "rom" : "mkv");
        }

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
                // Season 0 uses the same token rendering for its own folder format.
                var part = parts[index];
                if (index == parts.Length - 1 && !values.Rules.RenamesFiles && values.OriginalFilename is { Length: > 0 } original)
                {
                    segments.Add(LibraryNaming.SafeName(original));
                    continue;
                }
                if (index == parts.Length - 1 && values.EpisodeNumbers.Count > 1) part = MultipleEpisodes(part, values);
                if (index == parts.Length - 1 && values.AbsoluteNumbers.Count > 1) part = MultipleAbsolute(part, values);
                if (values.Season == 0 && values.Rules.SpecialsFolder is { Length: > 0 } specials && index < parts.Length - 1
                    && Parse(part).Any(piece => piece.Name == "Season"))
                {
                    part = specials;
                }

                // Empty tokens drop out with the brackets and separators around them; what stays is rendered afterwards,
                // so values keep whatever characters they have.
                var rendered = new List<string>();
                var literal = Tidy(Piece().Replace(part, match =>
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

        private static string MultipleEpisodes(string pattern, NamingValues values)
        {
            var numbers = values.EpisodeNumbers;
            return EpisodeBlock().Replace(pattern, match =>
            {
                var first = match.Groups["prefix"].Value + Number(values.Season, match.Groups["seasonFormat"].Value) + match.Groups["episodePrefix"].Value;
                var initial = first + Number(numbers[0], match.Groups["episodeFormat"].Value);
                var separator = Regex.Match(pattern[..match.Index], @"[- ._]+$").Value;
                var rest = values.Rules.MultiEpisodeStyle is MultiEpisodeStyle.Range or MultiEpisodeStyle.PrefixedRange ? new[] { numbers[^1] } : numbers.Skip(1);
                return initial + string.Concat(rest.Select(number => values.Rules.MultiEpisodeStyle switch
                {
                    MultiEpisodeStyle.Duplicate => separator + first + Number(number, match.Groups["episodeFormat"].Value),
                    MultiEpisodeStyle.Repeat => match.Groups["episodePrefix"].Value + Number(number, match.Groups["episodeFormat"].Value),
                    MultiEpisodeStyle.Scene or MultiEpisodeStyle.PrefixedRange => "-" + match.Groups["episodePrefix"].Value + Number(number, match.Groups["episodeFormat"].Value),
                    _ => "-" + Number(number, match.Groups["episodeFormat"].Value)
                }));
            });
        }

        private static string MultipleAbsolute(string pattern, NamingValues values) => AbsoluteBlock().Replace(pattern, match =>
        {
            var numbers = values.AbsoluteNumbers;
            var separator = Regex.Match(pattern[..match.Index], @"[- ._]+$").Value;
            if (string.IsNullOrWhiteSpace(separator)) separator = "-";
            var join = values.Rules.MultiEpisodeStyle switch
            {
                MultiEpisodeStyle.Duplicate => separator,
                MultiEpisodeStyle.Repeat => separator.Trim().Length == 0 ? " " : separator.Trim(),
                _ => "-"
            };
            var selected = values.Rules.MultiEpisodeStyle is MultiEpisodeStyle.Range or MultiEpisodeStyle.PrefixedRange ? new[] { numbers[0], numbers[^1] } : numbers;
            return string.Join(join, selected.Select(number => Number(number, match.Groups["format"].Value)));
        });

        [GeneratedRegex(@"\{absolute(?::(?<format>0+))?\}", RegexOptions.IgnoreCase)]
        private static partial Regex AbsoluteBlock();

        [GeneratedRegex(@"(?<prefix>s?)\{season(?::(?<seasonFormat>0+))?\}(?<episodePrefix>[- ._]?[ex])\{episode(?::(?<episodeFormat>0+))?\}", RegexOptions.IgnoreCase)]
        private static partial Regex EpisodeBlock();

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
                "Author" or "Author Name" or "Author SortName" => values.Author ?? "",
                "Author CleanName" => values.Author is { } author ? Clean(author) : "",
                "Book Title" or "Album" or "Album Title" or "Game Title" => finalTitle,
                "Book CleanTitle" or "Album CleanTitle" => Clean(finalTitle),
                "Book Series" => values.Collection ?? "",
                "Book SeriesPosition" or "Volume" => Number(values.Volume, format),
                "Volume Label" => values.Volume is null ? "" : "v" + Number(values.Volume, format),
                "Book Year" or "Album ReleaseYear" or "Game Year" => year,
                "Artist" or "Artist Name" or "Album Artist" => values.Artist ?? values.Author ?? "",
                "Track" => Number(values.Track, format),
                "Track Title" => values.TrackTitle ?? "",
                "Track CleanTitle" => values.TrackTitle is { } trackTitle ? Clean(trackTitle) : "",
                "Disc" => Number(values.Disc, format),
                "Disc Prefix" => values.DiscCount > 1 && values.Disc is not null ? Number(values.Disc, format) + "-" : "",
                "Disc Count" => Number(values.DiscCount, format),
                "Language" or "Book Language" => values.Language ?? "",
                "Platform" or "Game Platform" => values.Platform ?? "",
                "Region" => values.Region ?? "",
                "Version" => values.GameVersion ?? "",
                "Format" => values.Format ?? "",
                "Primary Id" => Id(values, values.MediaTypeId switch { MediaType.Movie => "imdb", MediaType.Series => values.MediaSubtypeId == MediaSubtype.Anime ? "anilist" : "tvdb", MediaType.Book => values.MediaSubtypeId is MediaSubtype.Manga or MediaSubtype.LightNovel ? "anilist" : "openlibrary", MediaType.Music => "musicbrainz", _ => "igdb" }),
                "Season" => Number(values.Season, format),
                "Episode" => values.EpisodeNumbers.Count > 1 ? Number(values.EpisodeNumbers[0], format) + "-" + Number(values.EpisodeNumbers[^1], format) : Number(values.Episode, format),
                "Absolute" => Number(values.Absolute, format),
                "Air Date" => values.AirDate?.ToString("yyyy MM dd", CultureInfo.InvariantCulture) ?? "",
                "Quality" or "Quality Title" => values.Quality ?? "",
                "Quality Full" => (values.Quality ?? "") + (values.Revision > 1 ? values.MediaSubtypeId == MediaSubtype.Anime ? " v" + values.Revision.ToString(CultureInfo.InvariantCulture) : " Proper" : ""),
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
    internal sealed record NamingRules(bool ReplacesIllegalCharacters, ColonReplacement ColonReplacement, string? CustomColonReplacement, string? SpecialsFolder, bool RenamesFiles = true, MultiEpisodeStyle MultiEpisodeStyle = MultiEpisodeStyle.PrefixedRange)
    {
        public static NamingRules Default { get; } = new(true, ColonReplacement.Smart, null, "Specials");
    }

    internal sealed record NamingValues(string Title, int? Year, decimal? Season, decimal? Episode, string? EpisodeTitle, string? Quality, string? ReleaseGroup, string? Edition, int Revision)
    {
        public MediaType MediaTypeId { get; init; } = MediaType.Movie;
        public MediaSubtype? MediaSubtypeId { get; init; }
        public IReadOnlyList<decimal> EpisodeNumbers { get; init; } = [];
        public IReadOnlyList<decimal> AbsoluteNumbers { get; init; } = [];
        public string? Author { get; init; }
        public string? Artist { get; init; }
        public string? TrackTitle { get; init; }
        public decimal? Track { get; init; }
        public decimal? Disc { get; init; }
        public decimal? DiscCount { get; init; }
        public decimal? Volume { get; init; }
        public string? Language { get; init; }
        public string? Platform { get; init; }
        public string? Region { get; init; }
        public string? GameVersion { get; init; }
        public string? Format { get; init; }
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
