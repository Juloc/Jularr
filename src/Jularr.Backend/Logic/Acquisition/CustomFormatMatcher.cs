using System.Globalization;
using System.Text.RegularExpressions;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Logic.Acquisition;

namespace Jularr.Backend.Logic.Acquisition
{
    // Whether a release matches a custom format, the way Sonarr and Radarr decide it: per condition kind no required condition may fail
    // and at least one condition must hold; a negated condition holds when it does not match. Sources and modifiers use the app's own names.
    internal static partial class CustomFormatMatcher
    {
        private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);

        public static bool Matches(CustomFormat format, ReleaseCandidate candidate, FormatContext context) =>
            TryMatch(format, candidate, context, out var matches) && matches;

        public static bool TryMatch(CustomFormat format, ReleaseCandidate candidate, FormatContext context, out bool matches)
        {
            try
            {
                matches = format.Conditions.Length > 0 && format.Conditions.GroupBy(condition => condition.Kind).All(group =>
                {
                    var results = group.Select(condition => (condition.Required, Holds: Raw(condition, candidate, context) != condition.Negate)).ToArray();
                    return !results.Any(result => result.Required && !result.Holds) && results.Any(result => result.Holds);
                });
                return true;
            }
            catch (Exception exception) when (exception is ArgumentException or RegexMatchTimeoutException)
            {
                matches = false;
                return false;
            }
        }

        private static bool Raw(FormatCondition condition, ReleaseCandidate candidate, FormatContext context)
        {
            var release = candidate.Release;
            return condition.Kind switch
            {
                FormatConditionKind.ReleaseTitle => Pattern(condition.Value, release.RawTitle),
                FormatConditionKind.Edition => release.Edition is { } edition && Pattern(condition.Value, edition),
                FormatConditionKind.ReleaseGroup => release.ReleaseGroup is { } group && Pattern(condition.Value, group),
                FormatConditionKind.Language => Language(condition, candidate.Languages?.Select(language => language.ToLowerInvariant()).ToArray() ?? release.Languages, context.OriginalLanguage),
                FormatConditionKind.Source => string.Equals(condition.Value, Source(release, context.Movie), StringComparison.OrdinalIgnoreCase),
                FormatConditionKind.Resolution => condition.Resolution is { } resolution && release.Resolution == resolution,
                FormatConditionKind.QualityModifier => string.Equals(condition.Value, Modifier(release.Modifier), StringComparison.OrdinalIgnoreCase),
                FormatConditionKind.IndexerFlag => condition.Value is { } flag && candidate.IndexerFlags.Contains(flag, StringComparer.OrdinalIgnoreCase),
                FormatConditionKind.Size => candidate.SizeBytes is { } bytes && Between(condition, bytes / 1_073_741_824m),
                FormatConditionKind.ReleaseType => string.Equals(condition.Value, ReleaseType(release), StringComparison.OrdinalIgnoreCase),
                FormatConditionKind.Year => (context.Year ?? Year(release.RawTitle)) is { } year && Between(condition, year),
                _ => false
            };
        }

        private static bool Pattern(string? pattern, string value)
        {
            if (string.IsNullOrEmpty(pattern))
            {
                return false;
            }

            return Regex.IsMatch(value, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);
        }

        // "original" is the language the Work was made in; an except-condition holds when the release has any other language.
        private static bool Language(FormatCondition condition, IReadOnlyList<string> languages, string? original)
        {
            var wanted = condition.Value is "original" ? original : condition.Value;
            if (wanted is null)
            {
                return false;
            }

            if (wanted == "any")
            {
                return !condition.ExceptLanguage && languages.Count > 0;
            }

            if (wanted == "unknown")
            {
                return condition.ExceptLanguage ? languages.Count > 0 : languages.Count == 0;
            }

            return condition.ExceptLanguage
                ? languages.Any(language => !string.Equals(language, wanted, StringComparison.OrdinalIgnoreCase))
                : languages.Contains(wanted, StringComparer.OrdinalIgnoreCase);
        }

        // Sonarr: television, televisionRaw, web, webRip, dvd, bluray, blurayRaw. Radarr: cam, telesync, telecine, workprint, dvd, tv, webdl, webrip, bluray.
        private static string? Source(ReleaseInfo release, bool movie) => (release.Source, movie) switch
        {
            (ReleaseSource.Cam, true) => "cam",
            (ReleaseSource.Telesync, true) => "telesync",
            (ReleaseSource.Telecine, true) => "telecine",
            (ReleaseSource.Workprint, true) => "workprint",
            (ReleaseSource.Dvd, _) => "dvd",
            (ReleaseSource.Hdtv or ReleaseSource.Sdtv, true) => "tv",
            (ReleaseSource.Hdtv or ReleaseSource.Sdtv, false) => release.Modifier == ReleaseQualityModifier.RawHd ? "televisionRaw" : "television",
            (ReleaseSource.WebDl, true) => "webdl",
            (ReleaseSource.WebDl, false) => "web",
            (ReleaseSource.WebRip, true) => "webrip",
            (ReleaseSource.WebRip, false) => "webRip",
            (ReleaseSource.BluRay or ReleaseSource.BluRayRip, true) => "bluray",
            (ReleaseSource.BluRay or ReleaseSource.BluRayRip, false) => release.Modifier == ReleaseQualityModifier.Remux ? "blurayRaw" : "bluray",
            _ => "unknown"
        };

        private static string Modifier(ReleaseQualityModifier modifier) => modifier switch
        {
            ReleaseQualityModifier.Remux => "remux",
            ReleaseQualityModifier.BrDisk => "brdisk",
            ReleaseQualityModifier.RawHd => "rawhd",
            ReleaseQualityModifier.Regional => "regional",
            ReleaseQualityModifier.Screener => "screener",
            _ => "none"
        };

        private static string ReleaseType(ReleaseInfo release) => release.IsSeasonPack ? "seasonPack" : release.IsMultiEpisode ? "multiEpisode"
            : release.EpisodeStart is not null || release.AbsoluteEpisodeStart is not null || release.AirDate is not null ? "singleEpisode" : "unknown";

        // Sonarr and Radarr include the maximum but not the minimum.
        private static bool Between(FormatCondition condition, decimal value) =>
            (condition.Minimum is not { } minimum || value > minimum) && (condition.Maximum is not { } maximum || value <= maximum);

        private static int? Year(string title) => YearRegex().Match(title) is { Success: true } match ? int.Parse(match.Value, CultureInfo.InvariantCulture) : null;

        [GeneratedRegex(@"(?<=[ ._(\[])(?:19|20)\d{2}(?=[ ._)\]]|$)", RegexOptions.CultureInvariant)]
        private static partial Regex YearRegex();
    }

    // What a match needs beyond the release: whether it is a film, the Work's original language and its year.
    internal sealed record FormatContext(bool Movie, string? OriginalLanguage, int? Year);
}
