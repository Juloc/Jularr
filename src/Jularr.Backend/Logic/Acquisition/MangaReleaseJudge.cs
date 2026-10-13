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

        [GeneratedRegex(@"(?<![a-z])v(?:ol(?:ume)?s?)?\.?\s?(\d{1,4})(?:\s?-\s?(?:v(?:ol(?:ume)?)?\.?\s?)?(\d{1,4}))?(?!\d)", RegexOptions.IgnoreCase)]
        private static partial Regex Volumes();

        [GeneratedRegex(@"(?<![a-z])(?:c|ch|chapter)\.?\s?\d{1,4}(?!\d)", RegexOptions.IgnoreCase)]
        private static partial Regex Chapter();

        [GeneratedRegex(@"^(?:19|20)\d{2}$")]
        private static partial Regex YearToken();

        public static ReleaseJudgement<MangaIdentityMatch> Judge(IReadOnlyList<string> titles, UnitScope? scope, ReleaseOffer offer)
        {
            if (offer.DownloadUri is null)
            {
                return new ReleaseJudgement<MangaIdentityMatch>(MangaIdentityMatch.NoDownload, null, ReleaseIdentityEvidence.Strong("NoDownload"), SelectionCoverage.Single, "NoDownload");
            }

            // The series name before the volume marker must be one of the Work's titles; more words may be another series.
            var parsed = Parse(offer.Title);
            var name = Words(parsed.SeriesTitle);
            var languages = new[] { offer.Title.Contains("german", StringComparison.OrdinalIgnoreCase) || offer.Title.Contains("deutsch", StringComparison.OrdinalIgnoreCase) ? "DE" : "EN" };
            var viaId = offer.Stages.Contains("id");
            var wanted = titles.Select(Words).Where(words => words.Count > 0).ToArray();
            if (!wanted.Any(words => words.All(name.Contains)))
            {
                return Result(MangaIdentityMatch.WrongTitle, ReleaseIdentityEvidence.Conflict("WrongTitle", parsed.SeriesTitle), SelectionCoverage.Single);
            }

            var exact = wanted.Any(words => words.SetEquals(name));
            var title = exact ? viaId ? ReleaseIdentityEvidence.Exact("MatchesTitle") : ReleaseIdentityEvidence.Strong("MatchesTitle") : ReleaseIdentityEvidence.Ambiguous("ExtraTitleWords", parsed.SeriesTitle);
            var match = exact ? MangaIdentityMatch.Matches : MangaIdentityMatch.AmbiguousTitle;
            if (scope is null)
            {
                return Result(match, title, SelectionCoverage.Single);
            }

            // Chapter releases do not fill volumes; a release without volume numbers holds the whole series.
            if (parsed.VolumeStart is not { } start)
            {
                if (Chapter().IsMatch(offer.Title))
                {
                    return Result(MangaIdentityMatch.WrongVolume, ReleaseIdentityEvidence.Conflict("ChaptersOnly"), SelectionCoverage.Single);
                }

                var all = scope.Units.Wanted.Count;
                return Result(exact ? MangaIdentityMatch.ContainsTarget : match, title, new SelectionCoverage(all, all, Math.Max(0, scope.Units.All.Count - all)));
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
            var volumes = Volumes().Match(title);
            var cut = new[] { volumes.Success ? volumes.Index : -1, Chapter().Match(title) is { Success: true } chapter ? chapter.Index : -1, title.IndexOfAny(['(', '[']) }
                .Where(index => index > 0).DefaultIfEmpty(title.Length).Min();
            var tokens = Tokens(title);
            var format = tokens.Select(token => Formats.GetValueOrDefault(token)).OfType<string>().FirstOrDefault() ?? UnknownFormat;
            var name = string.Join(' ', Tokens(title[..cut]).Where(token => !Formats.ContainsKey(token) && !YearToken().IsMatch(token)));
            return new ReleaseInfo(title, name, null, null, null, null, null, null, false, false, null, ReleaseSource.Unknown, ReleaseVideoCodec.Unknown, null, ReleaseHdrFormat.None,
                ReleaseAudioCodec.Unknown, null, [], [], false, false, null, 1, tokens.Contains("proper", StringComparer.OrdinalIgnoreCase),
                tokens.Contains("repack", StringComparer.OrdinalIgnoreCase), null, title.ToLowerInvariant(), 1, [])
            {
                DocumentFormat = format,
                VolumeStart = volumes.Success ? int.Parse(volumes.Groups[1].Value, CultureInfo.InvariantCulture) : null,
                VolumeEnd = volumes.Success && volumes.Groups[2].Success ? int.Parse(volumes.Groups[2].Value, CultureInfo.InvariantCulture) : null
            };
        }

        private static HashSet<string> Words(string value) =>
            Tokens(value).Select(word => word.ToLowerInvariant()).Where(word => !Noise.Contains(word)).ToHashSet(StringComparer.OrdinalIgnoreCase);

        private static string[] Tokens(string value) =>
            value.Split([' ', '.', '_', '-', ':', ',', '(', ')', '[', ']', '{', '}', '\'', '"', '!', '?', '&', '/', '#', '+', '~'], StringSplitOptions.RemoveEmptyEntries);
    }
}
