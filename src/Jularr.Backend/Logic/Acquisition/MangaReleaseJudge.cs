using System.Globalization;
using System.Text.RegularExpressions;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Logic.Acquisition;

namespace Jularr.Backend.Logic.Acquisition
{
    // The Manga side of release identity: the series title, then the volumes a release holds.
    internal static partial class MangaReleaseJudge
    {
        // Format of a release whose name does not say; the import checks the content.
        public const string UnknownFormat = "COMIC";

        private static readonly Dictionary<string, string> Formats = new(StringComparer.OrdinalIgnoreCase)
        {
            ["cbz"] = "CBZ", ["cbr"] = "CBR", ["epub"] = "EPUB", ["pdf"] = "PDF"
        };

        private static readonly HashSet<string> Noise = new(StringComparer.OrdinalIgnoreCase)
        {
            "the", "a", "an", "of", "and", "digital", "complete", "completed", "manga", "comic", "comics", "english", "eng", "scan", "scans", "cbz", "cbr", "epub", "pdf", "proper", "repack"
        };

        // A range end is never a year.
        [GeneratedRegex(@"(?<![a-z])v(?:ol(?:ume)?s?)?\.?\s?(\d{1,4})(?:\s?-\s?(?:v(?:ol(?:ume)?)?\.?\s?)?(?!(?:19|20)\d{2}(?!\d))(\d{1,4}))?(?!\d)", RegexOptions.IgnoreCase)]
        private static partial Regex Volumes();

        [GeneratedRegex(@"(?<![a-z])(?:c|ch|chapter)\.?\s?\d{1,4}(?![a-z\d])", RegexOptions.IgnoreCase)]
        private static partial Regex Chapter();

        // Group and source tags in front of the series name.
        [GeneratedRegex(@"^\s*(?:[\[(][^\])]*[\])]\s*)+")]
        private static partial Regex LeadingTags();

        public static ReleaseJudgement<MangaIdentityMatch> Judge(IReadOnlyList<string> titles, UnitScope? scope, ReleaseOffer offer)
        {
            if (offer.DownloadUri is null)
            {
                return new ReleaseJudgement<MangaIdentityMatch>(MangaIdentityMatch.NoDownload, null, ReleaseIdentityEvidence.Strong("NoDownload"), SelectionCoverage.Single, "NoDownload");
            }

            // The series name before the volume marker must be one of the Work's titles; more words may be another series.
            var parsed = Parse(offer.Title);
            var name = ReleaseWords.Words(parsed.SeriesTitle, Noise);
            var languages = ReleaseWords.ReadLanguages(offer.Title);
            var viaId = offer.Stages.Contains("id");
            var wanted = titles.Select(title => ReleaseWords.Words(title, Noise)).Where(words => words.Count > 0).ToArray();
            if (!wanted.Any(words => words.All(name.Contains)))
            {
                return Result(MangaIdentityMatch.WrongTitle, ReleaseIdentityEvidence.Conflict("WrongTitle", parsed.SeriesTitle), SelectionCoverage.Single);
            }

            var exact = wanted.Any(words => words.SetEquals(name));
            var title = exact ? viaId ? ReleaseIdentityEvidence.Exact("MatchesTitle") : ReleaseIdentityEvidence.Strong("MatchesTitle") : ReleaseIdentityEvidence.Ambiguous("ExtraTitleWords", parsed.SeriesTitle);
            var match = exact ? MangaIdentityMatch.Matches : MangaIdentityMatch.AmbiguousTitle;

            // Chapter releases never fill volumes.
            if (parsed.VolumeStart is not { } start)
            {
                if (Chapter().IsMatch(offer.Title))
                {
                    return Result(MangaIdentityMatch.WrongVolume, ReleaseIdentityEvidence.Conflict("ChaptersOnly"), SelectionCoverage.Single);
                }

                // A release without volume numbers holds the whole series.
                var all = scope?.Units.Wanted.Count ?? 1;
                return Result(exact && scope is not null ? MangaIdentityMatch.ContainsTarget : match, title, new SelectionCoverage(all, all, Math.Max(0, (scope?.Units.All.Count ?? 1) - all)));
            }

            // Without known volumes a numbered release cannot complete the series; a person decides.
            if (scope is null)
            {
                return Result(MangaIdentityMatch.AmbiguousTitle, ReleaseIdentityEvidence.Ambiguous("VolumesUnknown", start.ToString(CultureInfo.InvariantCulture)), SelectionCoverage.Single);
            }

            var end = Math.Max(start, parsed.VolumeEnd ?? start);
            if (scope.Unit.Number < start || scope.Unit.Number > end)
            {
                return Result(MangaIdentityMatch.WrongVolume, ReleaseIdentityEvidence.Conflict("WrongVolume", start.ToString(CultureInfo.InvariantCulture)), SelectionCoverage.Single);
            }

            var covered = scope.Units.Wanted.Count(unit => unit.Number >= start && unit.Number <= end);
            return Result(match, title, new SelectionCoverage(Math.Max(1, covered), scope.Units.Wanted.Count, Math.Max(0, end - start + 1 - covered)));

            ReleaseJudgement<MangaIdentityMatch> Result(MangaIdentityMatch result, ReleaseIdentityEvidence evidence, SelectionCoverage coverage) =>
                new(result, parsed, evidence, coverage, null) { Languages = languages };
        }

        // Manga names carry the series, a volume or range and the format; video fields stay empty.
        public static ReleaseInfo Parse(string title)
        {
            var body = LeadingTags().Replace(title, "");
            var volumes = Volumes().Match(body);
            var cut = new[] { volumes.Success ? volumes.Index : -1, Chapter().Match(body) is { Success: true } chapter ? chapter.Index : -1, body.IndexOfAny(['(', '[']) }
                .Where(index => index > 0).DefaultIfEmpty(body.Length).Min();
            var tokens = ReleaseWords.Tokens(title);
            var format = tokens.Select(token => Formats.GetValueOrDefault(token)).OfType<string>().FirstOrDefault() ?? UnknownFormat;
            var name = string.Join(' ', ReleaseWords.Tokens(body[..cut]).Where(token => !Formats.ContainsKey(token) && !ReleaseWords.YearToken().IsMatch(token)));
            return new ReleaseInfo(title, name, null, null, null, null, null, null, false, false, null, ReleaseSource.Unknown, ReleaseVideoCodec.Unknown, null, ReleaseHdrFormat.None,
                ReleaseAudioCodec.Unknown, null, [], [], false, false, BookReleaseJudge.ReadName(title).Group, 1, tokens.Contains("proper", StringComparer.OrdinalIgnoreCase),
                tokens.Contains("repack", StringComparer.OrdinalIgnoreCase), null, title.ToLowerInvariant(), 1, [])
            {
                DocumentFormat = format,
                VolumeStart = volumes.Success ? int.Parse(volumes.Groups[1].Value, CultureInfo.InvariantCulture) : null,
                VolumeEnd = volumes.Success && volumes.Groups[2].Success ? int.Parse(volumes.Groups[2].Value, CultureInfo.InvariantCulture) : null
            };
        }
    }
}
