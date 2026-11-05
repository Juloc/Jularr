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
            "multi", "german", "dl", "dubbed", "uhd", "limited", "theatrical", "final", "special", "edition", "version", "complete", "hdr", "4k", "remux", "dc", "ws",
            "english", "french", "truefrench", "vff", "italian", "spanish", "japanese", "korean", "russian", "dutch", "polish", "portuguese", "chinese"
        };

        [GeneratedRegex(@"^(?:19|20)\d{2}$")]
        private static partial Regex YearToken();

        public static ReleaseJudgement<VideoIdentityMatch> Judge(AcquisitionKind kind, IReadOnlyList<string> titles, int? year, UnitScope? scope, ReleaseOffer offer, bool automatic = true, string? originalLanguage = null)
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
            var (titleMatch, titleEvidence) = kind == AcquisitionKind.Movie ? JudgeMovieTitle(titles[0], year, parsed, viaId) : JudgeSeriesTitle(titles, year, parsed, viaId);
            var (languages, assumed) = Languages(parsed, originalLanguage);
            var subtitles = SubtitleLanguages(parsed);
            if (titleEvidence.Confidence == IdentityConfidence.Conflict || scope is null)
            {
                return new ReleaseJudgement<VideoIdentityMatch>(titleMatch, parsed, titleEvidence, SelectionCoverage.Single, null) { Languages = languages, LanguagesAssumedOriginal = assumed, SubtitleLanguages = subtitles };
            }

            var judged = JudgeUnit(titleMatch, titleEvidence, parsed, scope, viaId, automatic);
            return new ReleaseJudgement<VideoIdentityMatch>(judged.Match, judged.Parsed, judged.Evidence, judged.Coverage, judged.SafetyRejection) { Languages = languages, LanguagesAssumedOriginal = assumed, SubtitleLanguages = subtitles };
        }

        // Whether a name found in a download's own file is the film or series asked for; a film is asked for by its first title.
        public static VideoIdentityMatch JudgeTitle(AcquisitionKind kind, IReadOnlyList<string> titles, int? year, ReleaseInfo parsed) =>
            (kind == AcquisitionKind.Movie ? JudgeMovieTitle(titles[0], year, parsed, false) : JudgeSeriesTitle(titles, year, parsed, false)).Match;

        // Scene names state their audio as words; a release without any is in the original language of the work (unknown when the work has none recorded).
        // "DL" and "MULTi" add the original language to those named.
        internal static (IReadOnlyList<string> Languages, bool AssumedOriginal) Languages(ReleaseInfo parsed, string? originalLanguage)
        {
            var original = originalLanguage is { Length: > 0 } code ? code.ToUpperInvariant() : null;
            var words = parsed.RawTitle.Split(['.', ' ', '_', '-', '[', ']', '(', ')'], StringSplitOptions.RemoveEmptyEntries);
            var named = parsed.AudioLanguages.Count > 0 ? parsed.AudioLanguages.Select(language => language.ToUpperInvariant()).ToList() : words.Select(word => word.ToUpperInvariant() switch
            {
                "GERMAN" => "DE",
                "FRENCH" or "TRUEFRENCH" or "VFF" => "FR",
                "ITALIAN" => "IT",
                "SPANISH" => "ES",
                "JAPANESE" => "JA",
                "ENGLISH" => "EN",
                "KOREAN" => "KO",
                "RUSSIAN" => "RU",
                "DUTCH" => "NL",
                "POLISH" => "PL",
                "PORTUGUESE" => "PT",
                "CHINESE" => "ZH",
                _ => null
            }).OfType<string>().Distinct().ToList();
            if (named.Count == 0)
            {
                return (original is null ? [] : [original], true);
            }

            if (original is not null && !named.Contains(original) && DualAudio().IsMatch(parsed.RawTitle))
            {
                named.Add(original);
            }

            return (named, false);
        }

        // "DL" (dual language) and "MULTi", but not the "DL" of "WEB-DL".
        [GeneratedRegex(@"(?<![A-Za-z])(?<!WEB[ ._-])(?:DL|MULTI(?![ ._-]?SUBS?))(?![A-Za-z])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
        private static partial Regex DualAudio();

        [GeneratedRegex(@"(?<![A-Za-z])(?:(?<language>GER(?:MAN)?|DEU|ENG(?:LISH)?|FRE(?:NCH)?|SPA(?:NISH)?|ITA(?:LIAN)?|JAP(?:ANESE)?|JPN|KOR(?:EAN)?|RUS(?:SIAN)?|POR(?:TUGUESE)?|DUT(?:CH)?|POL(?:ISH)?|CHI(?:NESE)?)[ ._-]?(?:SUBS?|SUBBED|SUBTITLES?)|(?:SUBS?|SUBBED|SUBTITLES?)[ ._-]?(?<language2>GER(?:MAN)?|DEU|ENG(?:LISH)?|FRE(?:NCH)?|SPA(?:NISH)?|ITA(?:LIAN)?|JAP(?:ANESE)?|JPN|KOR(?:EAN)?|RUS(?:SIAN)?|POR(?:TUGUESE)?|DUT(?:CH)?|POL(?:ISH)?|CHI(?:NESE)?)|(?<multi>MULTI[ ._-]?SUBS?)|(?<vostfr>VOSTFR))(?![A-Za-z])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
        private static partial Regex SubtitleTag();

        // The subtitles a name states: the bracketed language groups of a media-info style name and the scene tags (GerSub, German.Subbed, ENGSUB, VOSTFR; "*" for MULTi.SUBS).
        internal static IReadOnlyList<string> SubtitleLanguages(ReleaseInfo parsed)
        {
            var languages = parsed.SubtitleLanguages.Select(language => language.ToUpperInvariant()).ToList();
            foreach (Match match in SubtitleTag().Matches(parsed.RawTitle))
            {
                var code = match.Groups["vostfr"].Success ? "FR" : match.Groups["multi"].Success ? "*" : (match.Groups["language"].Success ? match.Groups["language"].Value : match.Groups["language2"].Value).ToUpperInvariant() switch
                {
                    "GER" or "GERMAN" or "DEU" => "DE",
                    "ENG" or "ENGLISH" => "EN",
                    "FRE" or "FRENCH" => "FR",
                    "SPA" or "SPANISH" => "ES",
                    "ITA" or "ITALIAN" => "IT",
                    "JAP" or "JAPANESE" or "JPN" => "JA",
                    "KOR" or "KOREAN" => "KO",
                    "RUS" or "RUSSIAN" => "RU",
                    "POR" or "PORTUGUESE" => "PT",
                    "DUT" or "DUTCH" => "NL",
                    "POL" or "POLISH" => "PL",
                    "CHI" or "CHINESE" => "ZH",
                    _ => null
                };
                if (code is not null && !languages.Contains(code)) languages.Add(code);
            }

            return languages;
        }

        private static ReleaseJudgement<VideoIdentityMatch> Unusable(VideoIdentityMatch match, string code) =>
            new(match, null, ReleaseIdentityEvidence.Strong(code), SelectionCoverage.Single, code);

        // The series' parsed title must be one of the titles it is known by, whole: another series that merely contains the words ("Peaky Blinders - Gangs of Birmingham",
        // "My Hero Academia Memories") is not this one. A year the name adds must be the series' own. Found by id, a different name stays ambiguous for a person to decide.
        private static (VideoIdentityMatch Match, ReleaseIdentityEvidence Evidence) JudgeSeriesTitle(IReadOnlyList<string> titles, int? year, ReleaseInfo parsed, bool viaId)
        {
            switch (TitleMatcher.Compare(titles, parsed.SeriesTitle, year))
            {
                case TitleComparison.Same:
                    return (VideoIdentityMatch.Matches, viaId ? ReleaseIdentityEvidence.Exact("Matches") : ReleaseIdentityEvidence.Strong("Matches"));
                case TitleComparison.OtherYear:
                    return (VideoIdentityMatch.WrongYear, ReleaseIdentityEvidence.Conflict("WrongYear", parsed.SeriesTitle));
                default:
                    return viaId
                        ? (VideoIdentityMatch.AmbiguousTitle, ReleaseIdentityEvidence.Ambiguous("AmbiguousTitle", parsed.SeriesTitle))
                        : (VideoIdentityMatch.WrongTitle, ReleaseIdentityEvidence.Conflict("WrongTitle", parsed.SeriesTitle));
            }
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

        private static ReleaseJudgement<VideoIdentityMatch> JudgeUnit(VideoIdentityMatch titleMatch, ReleaseIdentityEvidence title, ReleaseInfo parsed, UnitScope target, bool viaId, bool automatic)
        {
            // Absolute and dated releases count by the episodes of the season they map to.
            var (unit, scope) = (target.Unit, target.Units);
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

            // An episode search takes the releases holding its episode, the automatic season pass (a pack, else its first missing episode) too, before it goes episode by episode;
            // a person's season search (no episode asked) takes those holding any wanted episode of the season.
            if (mapped is var (_, start, end) && (target.UnitNumber is null && !automatic
                ? scope.Wanted.Any(candidate => candidate.ParentNumber == unit.ParentNumber && candidate.Number >= start && candidate.Number <= end)
                : unit.Number >= start && unit.Number <= end))
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
