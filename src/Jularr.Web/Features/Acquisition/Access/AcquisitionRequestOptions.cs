using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Jularr.Web.Features.Playback;

namespace Jularr.Web.Features.Acquisition.Access;

/// <summary>How much of a title a request asks for.</summary>
public enum RequestScope
{
    /// <summary>Everything the title has, now and as new episodes appear.</summary>
    WholeSeries,

    /// <summary>Only content released after the request; current known units stay unmonitored.</summary>
    FutureOnly,

    /// <summary>Explicit season/episode selection; future monitoring is controlled by <see cref="AcquisitionRequestOptions.MonitorFuture"/>.</summary>
    Custom,

    /// <summary>Legacy custom scope: only the listed seasons, with no future monitoring.</summary>
    Seasons,

    /// <summary>Legacy custom scope: only the listed episodes, with no future monitoring.</summary>
    Episodes
}

public sealed record RequestEpisode(int Season, int Number);

/// <summary>
/// The richer choices a requester makes for structured video (Anime/TV), kept in the request's payload:
/// which part of the title (whole series, seasons or single episodes), the audio and subtitle
/// language they want, and the quality profile the owner allows requesters to pick. Audio and
/// subtitle language are preferences the approver sees; the scope and the quality profile are
/// applied when the acquisition starts (<see cref="AnimeAcquisitionRequestExecutor"/>).
/// </summary>
public sealed record AcquisitionRequestOptions
{
    public const int MaxSeasonNumber = 99;
    public const int MaxEpisodeNumber = 9999;
    public const int MaxSelectedEpisodes = 1000;

    private static readonly JsonSerializerOptions PayloadJsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static AcquisitionRequestOptions Default { get; } = new();

    public RequestScope Scope { get; init; } = RequestScope.WholeSeries;

    public IReadOnlyList<int> Seasons { get; init; } = [];

    public IReadOnlyList<RequestEpisode> Episodes { get; init; } = [];

    /// <summary>Whether newly discovered future units stay monitored in Custom scope.</summary>
    public bool MonitorFuture { get; init; }

    /// <summary>Preferred audio language tag (for example <c>ja</c>), or null for the release default.</summary>
    public string? AudioLanguage { get; init; }

    /// <summary>Preferred subtitle language tag, <see cref="PlaybackLanguages.SubtitlesOff"/> for none, or null for the release default.</summary>
    public string? SubtitleLanguage { get; init; }

    /// <summary>The quality profile the requester picked, or null for the title's own profile.</summary>
    public string? QualityProfileId { get; init; }

    public bool IsDefault =>
        Scope == RequestScope.WholeSeries
        && AudioLanguage is null
        && SubtitleLanguage is null
        && QualityProfileId is null;

    /// <summary>The options as they are kept in <see cref="AcquisitionRequest.PayloadJson"/>; null for the default options.</summary>
    public string? ToPayloadJson() =>
        IsDefault ? null : JsonSerializer.Serialize(this, PayloadJsonOptions);

    /// <summary>Reads options back from a request payload. A payload that is not options yields the default options.</summary>
    public static AcquisitionRequestOptions FromPayload(string? payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return Default;
        }

        try
        {
            return JsonSerializer.Deserialize<AcquisitionRequestOptions>(payloadJson, PayloadJsonOptions) ?? Default;
        }
        catch (JsonException)
        {
            return Default;
        }
    }

    /// <summary>
    /// Checks the choices and returns them in canonical form (sorted, without duplicates, languages as
    /// normalized tags, and only the selection that belongs to the scope). Throws
    /// <see cref="ArgumentException"/> for an empty or out-of-range selection or an unknown language.
    /// </summary>
    public AcquisitionRequestOptions Validate()
    {
        var audio = NormalizeLanguage(AudioLanguage, allowOff: false, nameof(AudioLanguage));
        var subtitles = NormalizeLanguage(SubtitleLanguage, allowOff: true, nameof(SubtitleLanguage));
        var profile = string.IsNullOrWhiteSpace(QualityProfileId) ? null : QualityProfileId.Trim();

        var common = this with
        {
            AudioLanguage = audio,
            SubtitleLanguage = subtitles,
            QualityProfileId = profile
        };

        var seasons = Seasons.Distinct().Order().ToArray();
        if (seasons.Any(season => season is < 1 or > MaxSeasonNumber))
        {
            throw new ArgumentException($"Seasons are numbered 1 to {MaxSeasonNumber}.", nameof(Seasons));
        }

        var episodes = Episodes
            .Distinct()
            .OrderBy(episode => episode.Season)
            .ThenBy(episode => episode.Number)
            .ToArray();
        if (episodes.Length > MaxSelectedEpisodes
            || episodes.Any(episode => episode.Season is < 1 or > MaxSeasonNumber
                || episode.Number is < 1 or > MaxEpisodeNumber))
        {
            throw new ArgumentException("An episode selection is out of range.", nameof(Episodes));
        }

        switch (Scope)
        {
            case RequestScope.WholeSeries:
                return common with { Seasons = [], Episodes = [], MonitorFuture = true };

            case RequestScope.FutureOnly:
                return common with { Seasons = [], Episodes = [], MonitorFuture = true };

            case RequestScope.Custom:
                if (seasons.Length == 0 && episodes.Length == 0 && !MonitorFuture)
                {
                    throw new ArgumentException("Choose content or include future releases for Custom scope.", nameof(Scope));
                }

                return common with { Seasons = seasons, Episodes = episodes };

            case RequestScope.Seasons:
                if (seasons.Length == 0)
                {
                    throw new ArgumentException("Choose at least one season.", nameof(Seasons));
                }

                return common with { Seasons = seasons, Episodes = [], MonitorFuture = false };

            case RequestScope.Episodes:
                if (episodes.Length == 0)
                {
                    throw new ArgumentException("Choose at least one episode.", nameof(Episodes));
                }

                return common with { Seasons = [], Episodes = episodes, MonitorFuture = false };

            default:
                throw new ArgumentOutOfRangeException(nameof(Scope));
        }
    }

    private static string? NormalizeLanguage(string? value, bool allowOff, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = PlaybackLanguages.Normalize(value);
        if (normalized is null || (!allowOff && normalized == PlaybackLanguages.SubtitlesOff))
        {
            throw new ArgumentException($"'{value}' is not a language tag.", name);
        }

        return normalized;
    }
}

/// <summary>
/// Reads and writes the compact selection text of the request form: seasons as <c>1, 3-4</c> and
/// episodes as <c>1-6, 9</c> (of the default season) or <c>S02E03-05, S03E01</c>.
/// </summary>
public static partial class RequestSelectionText
{
    [GeneratedRegex(@"^(?:S(?<season>\d{1,3})E)?(?<start>\d{1,5})(?:-(?<end>\d{1,5}))?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EpisodeToken();

    [GeneratedRegex(@"^S?(?<start>\d{1,3})(?:-S?(?<end>\d{1,3}))?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SeasonToken();

    public static bool TryParseSeasons(string? text, out IReadOnlyList<int> seasons)
    {
        var result = new SortedSet<int>();
        seasons = [];
        foreach (var token in Tokens(text))
        {
            var match = SeasonToken().Match(token);
            if (!match.Success
                || !TryRange(match, out var start, out var end)
                || start < 1
                || end > AcquisitionRequestOptions.MaxSeasonNumber)
            {
                return false;
            }

            for (var season = start; season <= end; season++)
            {
                result.Add(season);
            }
        }

        seasons = [.. result];
        return true;
    }

    /// <param name="defaultSeason">The season that plain numbers such as <c>1-6</c> belong to.</param>
    public static bool TryParseEpisodes(string? text, int defaultSeason, out IReadOnlyList<RequestEpisode> episodes)
    {
        var result = new SortedSet<RequestEpisode>(Comparer<RequestEpisode>.Create(
            (left, right) => left.Season != right.Season
                ? left.Season.CompareTo(right.Season)
                : left.Number.CompareTo(right.Number)));
        episodes = [];
        foreach (var token in Tokens(text))
        {
            var match = EpisodeToken().Match(token);
            if (!match.Success || !TryRange(match, out var start, out var end) || start < 1)
            {
                return false;
            }

            var season = match.Groups["season"].Success
                ? int.Parse(match.Groups["season"].Value, CultureInfo.InvariantCulture)
                : defaultSeason;
            if (season is < 1 or > AcquisitionRequestOptions.MaxSeasonNumber
                || end > AcquisitionRequestOptions.MaxEpisodeNumber
                || end - start >= AcquisitionRequestOptions.MaxSelectedEpisodes)
            {
                return false;
            }

            for (var number = start; number <= end; number++)
            {
                result.Add(new RequestEpisode(season, number));
                if (result.Count > AcquisitionRequestOptions.MaxSelectedEpisodes)
                {
                    return false;
                }
            }
        }

        episodes = [.. result];
        return true;
    }

    /// <summary>Seasons as compact text: <c>1-3, 5</c>.</summary>
    public static string FormatSeasons(IEnumerable<int> seasons) =>
        string.Join(", ", Runs(seasons.Distinct().Order().ToArray()).Select(run =>
            run.Start == run.End ? Number(run.Start) : $"{Number(run.Start)}-{Number(run.End)}"));

    /// <summary>Episodes as compact text: <c>S01E01-06, S02E03</c>.</summary>
    public static string FormatEpisodes(IEnumerable<RequestEpisode> episodes)
    {
        var parts = new List<string>();
        foreach (var season in episodes.GroupBy(episode => episode.Season).OrderBy(group => group.Key))
        {
            foreach (var run in Runs(season.Select(episode => episode.Number).Distinct().Order().ToArray()))
            {
                var prefix = $"S{season.Key.ToString("00", CultureInfo.InvariantCulture)}E";
                parts.Add(run.Start == run.End
                    ? $"{prefix}{run.Start.ToString("00", CultureInfo.InvariantCulture)}"
                    : $"{prefix}{run.Start.ToString("00", CultureInfo.InvariantCulture)}-{run.End.ToString("00", CultureInfo.InvariantCulture)}");
            }
        }

        return string.Join(", ", parts);
    }

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static IEnumerable<string> Tokens(string? text) =>
        (text ?? string.Empty).Split([',', ';', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static bool TryRange(Match match, out int start, out int end)
    {
        start = int.Parse(match.Groups["start"].Value, CultureInfo.InvariantCulture);
        end = match.Groups["end"].Success
            ? int.Parse(match.Groups["end"].Value, CultureInfo.InvariantCulture)
            : start;
        return end >= start;
    }

    private static IEnumerable<(int Start, int End)> Runs(IReadOnlyList<int> sorted)
    {
        var index = 0;
        while (index < sorted.Count)
        {
            var start = sorted[index];
            var end = start;
            while (index + 1 < sorted.Count && sorted[index + 1] == end + 1)
            {
                index++;
                end = sorted[index];
            }

            yield return (start, end);
            index++;
        }
    }
}

/// <summary>The languages the request form offers, written in their own language so they need no translation.</summary>
public static class RequestLanguages
{
    public static IReadOnlyList<(string Tag, string Name)> Choices { get; } =
    [
        ("ja", "日本語"),
        ("en", "English"),
        ("de", "Deutsch"),
        ("fr", "Français"),
        ("es", "Español"),
        ("it", "Italiano"),
        ("pt", "Português"),
        ("ko", "한국어"),
        ("zh", "中文"),
        ("ru", "Русский")
    ];

    /// <summary>The display name of a tag; tags the form does not offer show as the upper-case tag.</summary>
    public static string Name(string tag) =>
        Choices.FirstOrDefault(choice => choice.Tag == tag).Name ?? tag.ToUpperInvariant();
}
