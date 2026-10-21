using System.Text.RegularExpressions;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Logic.Acquisition;

namespace Jularr.Backend.Logic.Acquisition
{
    // The Movie/TV side of release identity.
    internal static partial class VideoReleaseJudge
    {
        // Words of an edition or release tag that are not part of a movie's title; they never make a title ambiguous.
        private static readonly HashSet<string> TitleNoise = new(StringComparer.OrdinalIgnoreCase)
        {
            "the", "a", "an", "of", "and", "der", "die", "das", "und", "extended", "unrated", "remastered", "directors", "cut", "imax", "proper", "repack", "internal",
            "multi", "german", "dl", "dubbed", "uhd", "limited", "theatrical", "final", "special", "edition", "version", "complete", "hdr", "4k", "remux", "dc", "ws"
        };

        [GeneratedRegex(@"^(?:19|20)\d{2}$")]
        private static partial Regex YearToken();

        public static ReleaseJudgement<VideoIdentityMatch> Judge(AcquisitionKind kind, string title, int? year, TargetUnit? unit, UnitSet scope, ReleaseOffer offer)
        {
            if (offer.DownloadUri is null)
            {
                return Unusable(VideoIdentityMatch.NoDownload, "NoDownload");
            }

            if (!ReleaseParser.TryParse(offer.Title, out var parsed))
            {
                return Unusable(VideoIdentityMatch.Unparseable, "Unparseable");
            }

            var viaId = offer.Stages.Contains("id");
            var (titleMatch, titleEvidence) = kind == AcquisitionKind.Movie ? JudgeMovieTitle(title, year, parsed, viaId) : JudgeSeriesTitle(title, parsed, viaId);
            var languages = Languages(parsed);
            if (titleEvidence.Confidence == IdentityConfidence.Conflict || unit is null)
            {
                return new ReleaseJudgement<VideoIdentityMatch>(titleMatch, parsed, titleEvidence, SelectionCoverage.Single, null) { Languages = languages };
            }

            var judged = JudgeUnit(titleMatch, titleEvidence, parsed, unit, scope, viaId);
            return new ReleaseJudgement<VideoIdentityMatch>(judged.Match, judged.Parsed, judged.Evidence, judged.Coverage, judged.SafetyRejection) { Languages = languages };
        }

        // Whether a name found in a download's own file is the film or series asked for.
        public static VideoIdentityMatch JudgeTitle(AcquisitionKind kind, string title, int? year, ReleaseInfo parsed) =>
            (kind == AcquisitionKind.Movie ? JudgeMovieTitle(title, year, parsed, false) : JudgeSeriesTitle(title, parsed, false)).Match;

        // Scene names state non-English audio as words; untagged releases are English.
        internal static IReadOnlyList<string> Languages(ReleaseInfo parsed)
        {
            if (parsed.AudioLanguages.Count > 0)
            {
                return parsed.AudioLanguages;
            }

            var words = parsed.RawTitle.Split(['.', ' ', '_', '-', '[', ']', '(', ')'], StringSplitOptions.RemoveEmptyEntries);
            var languages = words.Select(word => word.ToUpperInvariant() switch
            {
                "GERMAN" => "DE",
                "FRENCH" or "TRUEFRENCH" or "VFF" => "FR",
                "ITALIAN" => "IT",
                "SPANISH" => "ES",
                "JAPANESE" => "JA",
                _ => null
            }).OfType<string>().Distinct().ToList();
            if (languages.Count == 0 || words.Any(word => word.Equals("DL", StringComparison.OrdinalIgnoreCase) || word.Equals("MULTI", StringComparison.OrdinalIgnoreCase)))
            {
                languages.Add("EN");
            }

            return languages;
        }

        private static ReleaseJudgement<VideoIdentityMatch> Unusable(VideoIdentityMatch match, string code) =>
            new(match, null, ReleaseIdentityEvidence.Strong(code), SelectionCoverage.Single, code);

        private static (VideoIdentityMatch Match, ReleaseIdentityEvidence Evidence) JudgeSeriesTitle(string requested, ReleaseInfo parsed, bool viaId)
        {
            if (TitleMatcher.Matches(requested, parsed.SeriesTitle))
            {
                return (VideoIdentityMatch.Matches, viaId ? ReleaseIdentityEvidence.Exact("Matches") : ReleaseIdentityEvidence.Strong("Matches"));
            }

            return viaId
                ? (VideoIdentityMatch.AmbiguousTitle, ReleaseIdentityEvidence.Ambiguous("AmbiguousTitle", parsed.SeriesTitle))
                : (VideoIdentityMatch.WrongTitle, ReleaseIdentityEvidence.Conflict("WrongTitle", parsed.SeriesTitle));
        }

        private static (VideoIdentityMatch Match, ReleaseIdentityEvidence Evidence) JudgeMovieTitle(string requested, int? year, ReleaseInfo parsed, bool viaId)
        {
            var wanted = Words(requested);
            var actual = Words(parsed.SeriesTitle);
            if (wanted.Count == 0 || !wanted.All(actual.Contains))
            {
                return viaId
                    ? (VideoIdentityMatch.AmbiguousTitle, ReleaseIdentityEvidence.Ambiguous("AmbiguousTitle", parsed.SeriesTitle))
                    : (VideoIdentityMatch.WrongTitle, ReleaseIdentityEvidence.Conflict("WrongTitle", parsed.SeriesTitle));
            }

            var years = actual.Where(word => YearToken().IsMatch(word) && !wanted.Contains(word)).Select(int.Parse).ToArray();
            var hasYear = year is not null && years.Length > 0 && years.Any(candidate => Math.Abs(candidate - year.Value) <= 1);
            if (year is not null && years.Length > 0 && !hasYear)
            {
                return (VideoIdentityMatch.WrongYear, ReleaseIdentityEvidence.Conflict("WrongYear", string.Join('/', years)));
            }

            // A sequel or spin-off shares the words of the requested title: anything beyond the title, the year and release tags is ambiguous.
            var extra = actual.Where(word => !wanted.Contains(word) && !YearToken().IsMatch(word)).ToArray();
            if (extra.Length > 0 && !viaId)
            {
                return (VideoIdentityMatch.AmbiguousTitle, ReleaseIdentityEvidence.Ambiguous("ExtraTitleWords", string.Join(' ', extra)));
            }

            return (VideoIdentityMatch.Matches, viaId || hasYear ? ReleaseIdentityEvidence.Exact(viaId ? "MatchesById" : "MatchesTitleAndYear") : ReleaseIdentityEvidence.Strong("Matches"));
        }

        private static ReleaseJudgement<VideoIdentityMatch> JudgeUnit(VideoIdentityMatch titleMatch, ReleaseIdentityEvidence title, ReleaseInfo parsed, TargetUnit unit, UnitSet scope, bool viaId)
        {
            // Absolute and dated releases count by the episodes of the season they map to.
            var mapped = parsed.IsSeasonPack ? null : EpisodeNumbering.Resolve(parsed, scope.All);
            if ((mapped?.Season ?? parsed.SeasonNumber) != unit.ParentNumber)
            {
                return new ReleaseJudgement<VideoIdentityMatch>(VideoIdentityMatch.WrongSeason, parsed, ReleaseIdentityEvidence.Conflict("WrongSeason", parsed.SeasonNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture)), SelectionCoverage.Single, null);
            }

            // An ambiguous title (found by id, named differently) stays ambiguous whatever the numbering says; otherwise exact numbering after an exact title is exact.
            ReleaseIdentityEvidence Fit(string code) =>
                title.Confidence == IdentityConfidence.Ambiguous ? title : viaId ? ReleaseIdentityEvidence.Exact(code) : ReleaseIdentityEvidence.Strong(code);

            var seasonUnits = scope.All.Where(candidate => candidate.ParentNumber == unit.ParentNumber).ToArray();
            if (parsed.IsSeasonPack)
            {
                var covered = scope.Wanted.Count(candidate => candidate.ParentNumber == unit.ParentNumber);
                return new ReleaseJudgement<VideoIdentityMatch>(VideoIdentityMatch.ContainsTarget, parsed, Fit("ContainsTarget"), new SelectionCoverage(covered, scope.Wanted.Count, Math.Max(0, seasonUnits.Length - covered)), null);
            }

            if (mapped is var (_, start, end) && unit.Number >= start && unit.Number <= end)
            {
                var covered = scope.Wanted.Count(candidate => candidate.ParentNumber == unit.ParentNumber && candidate.Number >= start && candidate.Number <= end);
                return new ReleaseJudgement<VideoIdentityMatch>(titleMatch == VideoIdentityMatch.AmbiguousTitle ? VideoIdentityMatch.AmbiguousTitle : VideoIdentityMatch.Matches, parsed, Fit("Matches"), new SelectionCoverage(Math.Max(1, covered), scope.Wanted.Count, Math.Max(0, end - start + 1 - covered)), null);
            }

            return new ReleaseJudgement<VideoIdentityMatch>(VideoIdentityMatch.WrongEpisode, parsed, ReleaseIdentityEvidence.Conflict("WrongEpisode"), SelectionCoverage.Single, null);
        }

        private static HashSet<string> Words(string value) =>
            value.Split([' ', '.', '_', '-', ':', ',', '(', ')', '[', ']', '\'', '"', '!', '?', '&', '/'], StringSplitOptions.RemoveEmptyEntries)
                .Select(word => word.ToLowerInvariant())
                .Where(word => !TitleNoise.Contains(word))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
}
