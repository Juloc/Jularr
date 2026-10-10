using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Jularr.Web.Features.Naming;

namespace Jularr.Web.Features.Acquisition.Naming;

public enum AnimeNamingScope
{
    SeriesFolder,
    SeasonFolder,
    EpisodeFile
}

// Renders Sonarr-compatible naming templates. The token grammar, casing rules, prefix/suffix
// handling, multi-episode styles, illegal-character and colon replacement follow Sonarr's
// FileNameBuilder; docs/ANIME_NAMING.md documents every rule this class implements.
public static partial class AnimeNamingFormatter
{
    public const string MultiEpisodeTitleSeparator = " + ";

    // Sonarr's illegal-character table: \ / < > ? * | " become + + (removed) (removed) ! - (removed) (removed).
    private static readonly (string Bad, string Good)[] IllegalCharacterReplacements =
    [
        ("\\", "+"),
        ("/", "+"),
        ("<", ""),
        (">", ""),
        ("?", "!"),
        ("*", "-"),
        ("|", ""),
        ("\"", "")
    ];

    [GeneratedRegex(
        @"(?<prefix>s?)\{season(?::(?<seasonPad>0+))?\}(?<episodeSeparator>[- ._]?[ex])\{episode(?::(?<episodePad>0+))?\}",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SeasonEpisodeRegex();

    [GeneratedRegex(@"\{season(?::0+)?\}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SeasonTokenRegex();

    [GeneratedRegex(@"\{episode(?::0+)?\}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EpisodeTokenRegex();

    [GeneratedRegex(@"\{absolute(?::0+)?\}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AbsoluteTokenRegex();

    [GeneratedRegex(@"\{air[- ._]date\}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AirDateTokenRegex();

    [GeneratedRegex(@"[\x00-\x1F]", RegexOptions.CultureInvariant)]
    private static partial Regex ControlCharacterRegex();

    [GeneratedRegex(@"[\\/]", RegexOptions.CultureInvariant)]
    private static partial Regex CleanTitleSlashRegex();

    [GeneratedRegex(
        @"(?<=\s)(,|<|>|/|\\|;|:|'|""|\||`|~|!|\?|@|\$|%|\^|\*|-|_|=)(?=\s)|('|""|:|\?|,)(?=(?:(?:s|m|t|ll|ve|d|re)\b)|\s|$)|(\(|\)|\[|\]|\{|\})",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CleanTitleRemoveRegex();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"^(?<article>The|An|A) (?<title>.*?)(?<suffix>(?: *\([^)]+\))*)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TitleArticleRegex();

    [GeneratedRegex(@"\s*\((?:19|20)\d{2}\)$", RegexOptions.CultureInvariant)]
    private static partial Regex TrailingYearRegex();

    [GeneratedRegex(@"(?::?\s?(?:\(\d+\)|(?:Part|Pt\.?)\s?\d+))$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MultiPartTitleRegex();

    public static string BuildSeriesFolderName(
        AnimeNamingProfile profile,
        AnimeNamingSeries series)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(series);

        return Render(
            profile.SeriesFolderFormat,
            AnimeNamingScope.SeriesFolder,
            profile,
            new AnimeNamingRequest(series, []),
            errors: null);
    }

    // Returns null when the profile stores episodes directly in the series folder.
    public static string? BuildSeasonFolderName(
        AnimeNamingProfile profile,
        AnimeNamingSeries series,
        int seasonNumber)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(series);

        if (!profile.UseSeasonFolders)
        {
            return null;
        }

        var template = seasonNumber == 0
            ? profile.SpecialsFolderFormat
            : profile.SeasonFolderFormat;

        return Render(
            template,
            AnimeNamingScope.SeasonFolder,
            profile,
            new AnimeNamingRequest(series, [new AnimeNamingEpisode(seasonNumber, 0, null, "")]),
            errors: null);
    }

    public static string BuildEpisodeFileName(
        AnimeNamingProfile profile,
        AnimeNamingRequest request)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(request);

        if (request.Episodes.Count == 0)
        {
            throw new ArgumentException("At least one episode is required.", nameof(request));
        }

        return Render(
            SelectEpisodeFormat(profile, request),
            AnimeNamingScope.EpisodeFile,
            profile,
            request,
            errors: null);
    }

    // Sonarr semantics: the anime template needs an absolute number for every episode and the
    // daily template needs an air date; otherwise the standard template applies.
    public static string SelectEpisodeFormat(
        AnimeNamingProfile profile,
        AnimeNamingRequest request)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(request);

        return request.Series.SeriesType switch
        {
            AnimeSeriesType.Anime when request.Episodes.Count > 0 &&
                                       request.Episodes.All(episode => episode.AbsoluteEpisodeNumber is > 0) =>
                profile.AnimeEpisodeFormat,
            AnimeSeriesType.Daily when request.Episodes.Count > 0 &&
                                       request.Episodes[0].AirDate is not null =>
                profile.DailyEpisodeFormat,
            _ => profile.StandardEpisodeFormat
        };
    }

    public static IReadOnlyList<string> Validate(AnimeNamingProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(profile.Id) ||
            profile.Id.Length > 64 ||
            !profile.Id.All(character => char.IsAsciiLetterLower(character) || char.IsAsciiDigit(character) || character == '-'))
        {
            errors.Add("Profile ID must be 1-64 lowercase letters, digits or dashes.");
        }

        if (string.IsNullOrWhiteSpace(profile.Name) || profile.Name.Length > 80)
        {
            errors.Add("Profile name must be 1-80 characters.");
        }

        var sample = AnimeNamingSamples.Single(AnimeSeriesType.Standard);
        ValidateTemplate("Series folder format", profile.SeriesFolderFormat, AnimeNamingScope.SeriesFolder, profile, sample, errors);
        ValidateTemplate("Season folder format", profile.SeasonFolderFormat, AnimeNamingScope.SeasonFolder, profile, sample, errors);
        ValidateTemplate("Specials folder format", profile.SpecialsFolderFormat, AnimeNamingScope.SeasonFolder, profile, sample, errors);
        ValidateTemplate("Standard episode format", profile.StandardEpisodeFormat, AnimeNamingScope.EpisodeFile, profile, sample, errors);
        ValidateTemplate("Daily episode format", profile.DailyEpisodeFormat, AnimeNamingScope.EpisodeFile, profile, sample, errors);
        ValidateTemplate("Anime episode format", profile.AnimeEpisodeFormat, AnimeNamingScope.EpisodeFile, profile, sample, errors);

        if (profile.UseSeasonFolders &&
            !string.IsNullOrWhiteSpace(profile.SeasonFolderFormat) &&
            !SeasonTokenRegex().IsMatch(profile.SeasonFolderFormat))
        {
            errors.Add("Season folder format must contain {season}.");
        }

        if (!HasSeasonAndEpisode(profile.StandardEpisodeFormat))
        {
            errors.Add("Standard episode format must contain {season} and {episode}.");
        }

        if (!HasSeasonAndEpisode(profile.AnimeEpisodeFormat) &&
            !AbsoluteTokenRegex().IsMatch(profile.AnimeEpisodeFormat ?? ""))
        {
            errors.Add("Anime episode format must contain {season} and {episode}, or {absolute}.");
        }

        if (!HasSeasonAndEpisode(profile.DailyEpisodeFormat) &&
            !AirDateTokenRegex().IsMatch(profile.DailyEpisodeFormat ?? ""))
        {
            errors.Add("Daily episode format must contain {Air-Date}, or {season} and {episode}.");
        }

        if (!Enum.IsDefined(profile.MultiEpisodeStyle))
        {
            errors.Add("Multi-episode style is not supported.");
        }

        if (!Enum.IsDefined(profile.ColonReplacement))
        {
            errors.Add("Colon replacement is not supported.");
        }

        if (profile.ColonReplacement == AnimeColonReplacement.Custom &&
            (profile.CustomColonReplacement ?? "").Any(character =>
                NamingTemplateEngine.IllegalLiteralCharacters.Contains(character) || char.IsControl(character)))
        {
            errors.Add("Custom colon replacement must not contain illegal file name characters.");
        }

        return errors;
    }

    // Replaces or removes characters that are illegal in file names (per-token, Sonarr semantics).
    // Smart colon replacement turns ": " into " - " and any remaining ":" into "-".
    public static string CleanFileName(string value, AnimeNamingProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var result = ControlCharacterRegex().Replace(value ?? "", "");
        if (profile.ReplaceIllegalCharacters)
        {
            result = profile.ColonReplacement switch
            {
                AnimeColonReplacement.Smart => result.Replace(": ", " - ", StringComparison.Ordinal).Replace(":", "-", StringComparison.Ordinal),
                AnimeColonReplacement.Dash => result.Replace(":", "-", StringComparison.Ordinal),
                AnimeColonReplacement.SpaceDash => result.Replace(":", " -", StringComparison.Ordinal),
                AnimeColonReplacement.SpaceDashSpace => result.Replace(":", " - ", StringComparison.Ordinal),
                AnimeColonReplacement.Custom => result.Replace(":", profile.CustomColonReplacement ?? "", StringComparison.Ordinal),
                _ => result.Replace(":", "", StringComparison.Ordinal)
            };
        }
        else
        {
            result = result.Replace(":", "", StringComparison.Ordinal);
        }

        foreach (var (bad, good) in IllegalCharacterReplacements)
        {
            result = result.Replace(bad, profile.ReplaceIllegalCharacters ? good : "", StringComparison.Ordinal);
        }

        return result.TrimStart(' ', '.').TrimEnd(' ');
    }

    // Sonarr's CleanTitle: "&" becomes "and", slashes become spaces, and punctuation that is
    // standalone, possessive/contracting or a bracket is removed.
    public static string CleanTitle(string title)
    {
        var result = (title ?? "").Replace("&", "and", StringComparison.Ordinal);
        result = CleanTitleSlashRegex().Replace(result, " ");
        result = CleanTitleRemoveRegex().Replace(result, "");
        return WhitespaceRegex().Replace(result, " ").Trim();
    }

    // "The Series Title (2010)" becomes "Series Title, The (2010)".
    public static string TitleThe(string title)
    {
        var match = TitleArticleRegex().Match(title ?? "");
        return match.Success
            ? $"{match.Groups["title"].Value}, {match.Groups["article"].Value}{match.Groups["suffix"].Value}"
            : title ?? "";
    }

    public static string TitleYear(string title, int? year)
    {
        if (year is null || TrailingYearRegex().IsMatch(title))
        {
            return title;
        }

        return $"{title} ({year.Value.ToString(CultureInfo.InvariantCulture)})";
    }

    public static string TitleWithoutYear(string title) =>
        TrailingYearRegex().Replace(title ?? "", "");

    public static string GetQualityTitle(AnimeReleaseInfo release)
    {
        ArgumentNullException.ThrowIfNull(release);

        var resolution = release.Resolution is > 0 ? $"{release.Resolution}p" : null;
        return release.Source switch
        {
            AnimeReleaseSource.WebDl => $"WEBDL-{resolution ?? "480p"}",
            AnimeReleaseSource.WebRip => $"WEBRip-{resolution ?? "480p"}",
            AnimeReleaseSource.BluRay or AnimeReleaseSource.BluRayRip => $"Bluray-{resolution ?? "480p"}",
            AnimeReleaseSource.Hdtv => release.Resolution is >= 720 ? $"HDTV-{resolution}" : "SDTV",
            _ when release.Resolution is >= 720 => $"HDTV-{resolution}",
            _ when release.Resolution is > 0 => "SDTV",
            _ => "Unknown"
        };
    }

    // Sonarr's revision marker: anime uses the release version ("v2"), other series use
    // "Repack" or "Proper".
    public static string GetQualityProper(AnimeReleaseInfo release, AnimeSeriesType seriesType)
    {
        ArgumentNullException.ThrowIfNull(release);

        if (seriesType == AnimeSeriesType.Anime && release.Version > 1)
        {
            return $"v{release.Version.ToString(CultureInfo.InvariantCulture)}";
        }

        if (release.IsRepack)
        {
            return "Repack";
        }

        return release.IsProper || release.Version > 1 ? "Proper" : "";
    }

    public static string Render(
        string template,
        AnimeNamingScope scope,
        AnimeNamingProfile profile,
        AnimeNamingRequest request,
        List<string>? errors = null)
    {
        var episodes = request.Episodes
            .OrderBy(episode => episode.SeasonNumber)
            .ThenBy(episode => episode.EpisodeNumber)
            .ToArray();

        var result = template ?? "";
        if (scope == AnimeNamingScope.EpisodeFile && episodes.Length > 0)
        {
            var source = result;
            result = SeasonEpisodeRegex().Replace(
                source,
                match => RenderSeasonEpisode(match, source, episodes, profile.MultiEpisodeStyle));
        }

        return NamingTemplateEngine.Render(result, token => RenderToken(token, scope, profile, request, episodes, errors), leadingTrim: " .");
    }

    private static void ValidateTemplate(
        string label,
        string? template,
        AnimeNamingScope scope,
        AnimeNamingProfile profile,
        AnimeNamingRequest sample,
        List<string> errors)
    {
        errors.AddRange(NamingTemplateEngine.ValidateLiteral(label, template));
        if (string.IsNullOrWhiteSpace(template) || template.Length > 500)
        {
            return;
        }

        var tokenErrors = new List<string>();
        var rendered = Render(template, scope, profile, sample, tokenErrors);
        errors.AddRange(tokenErrors.Distinct(StringComparer.Ordinal).Select(error => $"{label}: {error}"));

        if (tokenErrors.Count == 0 && string.IsNullOrWhiteSpace(rendered))
        {
            errors.Add($"{label} renders an empty name.");
        }
    }

    private static bool HasSeasonAndEpisode(string? template) =>
        !string.IsNullOrWhiteSpace(template) &&
        SeasonTokenRegex().IsMatch(template) &&
        EpisodeTokenRegex().IsMatch(template);

    private static string RenderSeasonEpisode(
        Match match,
        string source,
        IReadOnlyList<AnimeNamingEpisode> episodes,
        AnimeMultiEpisodeStyle style)
    {
        var prefix = match.Groups["prefix"].Value;
        var separator = match.Groups["episodeSeparator"].Value;
        var seasonPad = match.Groups["seasonPad"].Value.Length;
        var episodePad = match.Groups["episodePad"].Value.Length;

        string Pair(AnimeNamingEpisode episode) =>
            prefix + NamingTemplateEngine.Pad(episode.SeasonNumber, seasonPad) + separator + NamingTemplateEngine.Pad(episode.EpisodeNumber, episodePad);

        string Number(AnimeNamingEpisode episode) => NamingTemplateEngine.Pad(episode.EpisodeNumber, episodePad);

        var first = episodes[0];
        if (episodes.Count == 1)
        {
            return Pair(first);
        }

        var rest = episodes.Skip(1).ToArray();
        var last = episodes[^1];
        return style switch
        {
            AnimeMultiEpisodeStyle.Duplicate => string.Join(PrecedingSeparator(source, match.Index), episodes.Select(Pair)),
            AnimeMultiEpisodeStyle.Repeat => Pair(first) + string.Concat(rest.Select(episode => separator + Number(episode))),
            AnimeMultiEpisodeStyle.Scene => Pair(first) + string.Concat(rest.Select(episode => "-" + separator + Number(episode))),
            AnimeMultiEpisodeStyle.Range => $"{Pair(first)}-{Number(last)}",
            AnimeMultiEpisodeStyle.PrefixedRange => $"{Pair(first)}-{separator}{Number(last)}",
            _ => Pair(first) + string.Concat(rest.Select(episode => "-" + Number(episode)))
        };
    }

    // Duplicate repeats the whole season/episode pair using the separator written before it.
    private static string PrecedingSeparator(string source, int index)
    {
        var start = index;
        while (start > 0 && "- ._".Contains(source[start - 1]))
        {
            start--;
        }

        return start < index ? source[start..index] : ".";
    }

    private static string RenderToken(
        NamingToken token,
        AnimeNamingScope scope,
        AnimeNamingProfile profile,
        AnimeNamingRequest request,
        IReadOnlyList<AnimeNamingEpisode> episodes,
        List<string>? errors)
    {
        var rawToken = token.RawToken;
        var key = token.Key;
        var customFormat = token.Format;

        var numeric = key is "season" or "episode" or "absolute";
        if (!numeric && customFormat.Length > 0)
        {
            errors?.Add($"{{{rawToken}}} does not accept a format.");
            return "";
        }

        if (numeric && customFormat.Length > 0 && customFormat.Any(character => character != '0'))
        {
            errors?.Add($"{{{rawToken}}} only accepts zero padding such as :00.");
            return "";
        }

        var value = ResolveToken(key, customFormat.Length, scope, profile.MultiEpisodeStyle, request, episodes);
        if (value is null)
        {
            errors?.Add(IsKnownToken(key)
                ? $"{{{rawToken}}} is not available here."
                : $"{{{rawToken}}} is not a supported token.");
            return "";
        }

        if (value.Length == 0)
        {
            return "";
        }

        if (!numeric)
        {
            value = CleanFileName(NamingTemplateEngine.ApplyCasing(token, value), profile);
            if (value.Length == 0)
            {
                return "";
            }
        }

        return token.Prefix + value + token.Suffix;
    }

    private static readonly HashSet<string> SeriesTokens = new(StringComparer.Ordinal)
    {
        "series title", "series cleantitle", "series titleyear", "series cleantitleyear",
        "series titlewithoutyear", "series cleantitlewithoutyear", "series titlethe",
        "series cleantitlethe", "series titlefirstcharacter", "series year",
        "anilistid", "malid", "tvdbid", "tmdbid", "imdbid"
    };

    private static readonly HashSet<string> EpisodeTokens = new(StringComparer.Ordinal)
    {
        "episode", "absolute", "episode title", "episode cleantitle", "air date",
        "quality full", "quality title", "quality proper", "quality real", "quality key",
        "release group", "mediainfo simple", "mediainfo full", "mediainfo videocodec",
        "mediainfo videobitdepth", "mediainfo videodynamicrangetype", "mediainfo audiocodec",
        "mediainfo audiochannels", "mediainfo audiolanguages", "mediainfo audiolanguagesall",
        "mediainfo subtitlelanguages"
    };

    private static bool IsKnownToken(string key) =>
        key == "season" || SeriesTokens.Contains(key) || EpisodeTokens.Contains(key);

    private static string? ResolveToken(
        string key,
        int pad,
        AnimeNamingScope scope,
        AnimeMultiEpisodeStyle style,
        AnimeNamingRequest request,
        IReadOnlyList<AnimeNamingEpisode> episodes)
    {
        var series = request.Series;
        if (SeriesTokens.Contains(key))
        {
            return ResolveSeriesToken(key, series);
        }

        if (key == "season")
        {
            return scope == AnimeNamingScope.SeriesFolder || episodes.Count == 0
                ? null
                : NamingTemplateEngine.Pad(episodes[0].SeasonNumber, pad);
        }

        if (scope != AnimeNamingScope.EpisodeFile || !EpisodeTokens.Contains(key) || episodes.Count == 0)
        {
            return null;
        }

        var release = request.Release;
        return key switch
        {
            "episode" => JoinNumbers(episodes.Select(episode => (int?)episode.EpisodeNumber).ToArray(), pad, style),
            "absolute" => JoinNumbers(episodes.Select(episode => episode.AbsoluteEpisodeNumber).ToArray(), pad, style),
            "episode title" => EpisodeTitle(episodes),
            "episode cleantitle" => CleanTitle(EpisodeTitle(episodes)),
            "air date" => episodes[0].AirDate is DateOnly airDate
                ? airDate.ToString("yyyy MM dd", CultureInfo.InvariantCulture)
                : "",
            "quality full" => release is null
                ? ""
                : $"{GetQualityTitle(release)} {GetQualityProper(release, series.SeriesType)}".Trim(),
            "quality title" => release is null ? "" : GetQualityTitle(release),
            "quality proper" => release is null ? "" : GetQualityProper(release, series.SeriesType),
            "quality real" => "",
            "quality key" => release is null ? "" : Quality.ReleaseQuality.GetKey(release),
            "release group" => release?.ReleaseGroup ?? "",
            "mediainfo videocodec" => VideoCodec(release),
            "mediainfo videobitdepth" => release?.BitDepth?.ToString(CultureInfo.InvariantCulture) ?? "",
            "mediainfo videodynamicrangetype" => DynamicRange(release),
            "mediainfo audiocodec" => AudioCodec(release),
            "mediainfo audiochannels" => release?.AudioChannels ?? "",
            "mediainfo audiolanguages" => Languages(release?.AudioLanguages, omitEnglishOnly: true),
            "mediainfo audiolanguagesall" => Languages(release?.AudioLanguages, omitEnglishOnly: false),
            "mediainfo subtitlelanguages" => Languages(release?.SubtitleLanguages, omitEnglishOnly: false),
            "mediainfo simple" => $"{VideoCodec(release)} {AudioCodec(release)}".Trim(),
            "mediainfo full" => WhitespaceRegex().Replace(
                $"{VideoCodec(release)} {AudioCodec(release)}{Languages(release?.AudioLanguages, omitEnglishOnly: true)} {Languages(release?.SubtitleLanguages, omitEnglishOnly: false)}",
                " ").Trim(),
            _ => null
        };
    }

    private static string ResolveSeriesToken(string key, AnimeNamingSeries series)
    {
        var title = series.Title ?? "";
        return key switch
        {
            "series title" => title,
            "series cleantitle" => CleanTitle(title),
            "series titleyear" => TitleYear(title, series.Year),
            "series cleantitleyear" => series.Year is int year && !TrailingYearRegex().IsMatch(title)
                ? $"{CleanTitle(title)} {year.ToString(CultureInfo.InvariantCulture)}"
                : CleanTitle(title),
            "series titlewithoutyear" => TitleWithoutYear(title),
            "series cleantitlewithoutyear" => CleanTitle(TitleWithoutYear(title)),
            "series titlethe" => TitleThe(title),
            "series cleantitlethe" => CleanTitle(TitleThe(title)),
            "series titlefirstcharacter" => FirstCharacter(TitleThe(title)),
            "series year" => series.Year?.ToString(CultureInfo.InvariantCulture) ?? "",
            "anilistid" => series.AniListId ?? "",
            "malid" => series.MyAnimeListId ?? "",
            "tvdbid" => series.TvdbId ?? "",
            "tmdbid" => series.TmdbId ?? "",
            "imdbid" => series.ImdbId ?? "",
            _ => ""
        };
    }

    // Range styles render first-last; every other style lists each number. A single unknown
    // number (e.g. a missing absolute number) leaves the whole token empty.
    private static string JoinNumbers(
        IReadOnlyList<int?> numbers,
        int pad,
        AnimeMultiEpisodeStyle style)
    {
        if (numbers.Count == 0 || numbers.Any(number => number is null or < 0))
        {
            return "";
        }

        var values = numbers.Select(number => number!.Value).ToArray();
        if (values.Length == 1)
        {
            return NamingTemplateEngine.Pad(values[0], pad);
        }

        return style is AnimeMultiEpisodeStyle.Range or AnimeMultiEpisodeStyle.PrefixedRange
            ? $"{NamingTemplateEngine.Pad(values[0], pad)}-{NamingTemplateEngine.Pad(values[^1], pad)}"
            : string.Join("-", values.Select(value => NamingTemplateEngine.Pad(value, pad)));
    }

    private static string EpisodeTitle(IReadOnlyList<AnimeNamingEpisode> episodes)
    {
        var titles = episodes
            .Select(episode => string.IsNullOrWhiteSpace(episode.Title) ? "TBA" : episode.Title.Trim())
            .ToArray();

        if (titles.Length == 1)
        {
            return titles[0];
        }

        var withoutParts = titles
            .Select(title => MultiPartTitleRegex().Replace(title, "").Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        return withoutParts.Length == 1 && withoutParts[0].Length > 0
            ? withoutParts[0]
            : string.Join(MultiEpisodeTitleSeparator, titles.Distinct(StringComparer.Ordinal));
    }

    private static string FirstCharacter(string title)
    {
        if (string.IsNullOrEmpty(title) || !char.IsLetterOrDigit(title[0]))
        {
            return "_";
        }

        return title[..1].ToUpperInvariant();
    }

    // Sonarr names AVC/HEVC after the spelling found in the release name (x264/h264, x265/h265)
    // and falls back to "AVC"/"HEVC"; the parser's evidence carries that matched text.
    private static string VideoCodec(AnimeReleaseInfo? release)
    {
        if (release is null)
        {
            return "";
        }

        var matched = release.Evidence
            .FirstOrDefault(evidence => evidence.Field == "videoCodec")?.MatchedText
            .Replace(" ", "", StringComparison.Ordinal)
            .Replace(".", "", StringComparison.Ordinal)
            .Replace("_", "", StringComparison.Ordinal)
            .Replace("-", "", StringComparison.Ordinal)
            .ToLowerInvariant();

        return release.VideoCodec switch
        {
            AnimeVideoCodec.Avc => matched is "x264" or "h264" ? matched : "AVC",
            AnimeVideoCodec.Hevc => matched is "x265" or "h265" ? matched : "HEVC",
            AnimeVideoCodec.Av1 => "AV1",
            _ => ""
        };
    }

    private static string AudioCodec(AnimeReleaseInfo? release) => release?.AudioCodec switch
    {
        AnimeAudioCodec.Aac => "AAC",
        AnimeAudioCodec.Flac => "FLAC",
        AnimeAudioCodec.Opus => "Opus",
        AnimeAudioCodec.Ac3 => "AC3",
        AnimeAudioCodec.Eac3 => "EAC3",
        AnimeAudioCodec.Dts => "DTS",
        AnimeAudioCodec.DtsHd => "DTS-HD MA",
        AnimeAudioCodec.TrueHd => "TrueHD",
        _ => ""
    };

    private static string DynamicRange(AnimeReleaseInfo? release) => release?.HdrFormat switch
    {
        AnimeHdrFormat.Hdr => "HDR",
        AnimeHdrFormat.Hdr10 => "HDR10",
        AnimeHdrFormat.Hdr10Plus => "HDR10Plus",
        AnimeHdrFormat.DolbyVision => "DV",
        _ => ""
    };

    private static string Languages(IReadOnlyList<string>? languages, bool omitEnglishOnly)
    {
        if (languages is null || languages.Count == 0)
        {
            return "";
        }

        var codes = languages
            .Where(language => !string.IsNullOrWhiteSpace(language))
            .Select(language => language.Trim().ToUpperInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (codes.Length == 0 || (omitEnglishOnly && codes is ["EN"]))
        {
            return "";
        }

        return $"[{string.Join("+", codes)}]";
    }

}
