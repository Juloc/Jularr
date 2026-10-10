using Jularr.Web.Features.Acquisition.Core;
using System.Text.RegularExpressions;
using Jularr.Web.Features.Acquisition.Prowlarr;
using Jularr.Web.Features.Acquisition.Release;
using Jularr.Web.Features.Acquisition.Search;
using Jularr.Web.Features.Acquisition.Selection;

namespace Jularr.Web.Features.Acquisition.Access;

/// <summary>The units of a Series a search is for: every unit of the Series and the subset that is still wanted (missing, included in the request, aired).</summary>
public sealed record VideoUnitScope(IReadOnlyList<VideoUnit> All, IReadOnlyList<VideoUnit> Wanted)
{
    public static VideoUnitScope Empty { get; } = new([], []);
}

/// <summary>
/// The Movie/TV side of release identity: is this release the requested title (and year), season and episode, and which wanted units
/// does it cover. It only produces identity evidence and coverage; the shared selection engine decides what that means for the profile.
/// A release found through a trustworthy provider id counts as exact when its numbering fits, and a title that differs from the
/// requested one then needs a person rather than being rejected, because releases carry localized or alternate titles.
/// </summary>
public static partial class VideoReleaseJudge
{
    // Words of an edition or release tag that are not part of a movie's title; they never make a title ambiguous.
    private static readonly HashSet<string> TitleNoise = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "a", "an", "of", "and", "der", "die", "das", "und", "extended", "unrated", "remastered", "directors", "cut", "imax", "proper", "repack", "internal",
        "multi", "german", "dl", "dubbed", "uhd", "limited", "theatrical", "final", "special", "edition", "version", "complete", "hdr", "4k", "remux", "dc", "ws"
    };

    [GeneratedRegex(@"^(?:19|20)\d{2}$")]
    private static partial Regex YearToken();

    public static ReleaseJudgement<VideoIdentityMatch> Judge(IReleaseParser parser, MediaAcquisitionKind kind, string title, int? year, VideoUnit? unit, VideoUnitScope scope, AcquisitionCandidate candidate)
    {
        if (candidate.InternalDownloadUri is null)
        {
            return Unusable(VideoIdentityMatch.NoDownload, "The indexer returned no download link.");
        }

        if (candidate.Protocol is not null && !candidate.Protocol.Equals("usenet", StringComparison.OrdinalIgnoreCase))
        {
            return Unusable(VideoIdentityMatch.NotUsenet, "Jularr only downloads Usenet releases.");
        }

        if (!parser.TryParse(candidate.Title, out var parsed))
        {
            return Unusable(VideoIdentityMatch.Unparseable, "The release name could not be parsed.");
        }

        var viaId = candidate.Provenance.Any(origin => origin.Stage == "id");
        var (titleMatch, titleEvidence) = kind == MediaAcquisitionKind.Movie ? JudgeMovieTitle(title, year, parsed, viaId) : JudgeSeriesTitle(title, parsed, viaId);
        if (titleEvidence.Confidence == IdentityConfidence.Conflict || unit is null)
        {
            return new ReleaseJudgement<VideoIdentityMatch>(titleMatch, parsed, titleEvidence, SelectionCoverage.Single, null);
        }

        return JudgeUnit(titleMatch, titleEvidence, parsed, unit, scope, viaId);
    }

    private static ReleaseJudgement<VideoIdentityMatch> Unusable(VideoIdentityMatch match, string reason) =>
        new(match, null, ReleaseIdentityEvidence.Strong(match.ToString(), reason), SelectionCoverage.Single, reason);

    private static (VideoIdentityMatch Match, ReleaseIdentityEvidence Evidence) JudgeSeriesTitle(string requested, ReleaseInfo parsed, bool viaId)
    {
        if (TitleMatcher.Matches(requested, parsed.SeriesTitle))
        {
            return (VideoIdentityMatch.Matches, viaId ? ReleaseIdentityEvidence.Exact("Matches", "Found by the provider id; the title agrees.") : ReleaseIdentityEvidence.Strong("Matches", "The title matches."));
        }

        return viaId
            ? (VideoIdentityMatch.AmbiguousTitle, ReleaseIdentityEvidence.Ambiguous("AmbiguousTitle", $"Found by the provider id, but the release is named '{parsed.SeriesTitle}'."))
            : (VideoIdentityMatch.WrongTitle, ReleaseIdentityEvidence.Conflict("WrongTitle", $"'{parsed.SeriesTitle}' is another title."));
    }

    private static (VideoIdentityMatch Match, ReleaseIdentityEvidence Evidence) JudgeMovieTitle(string requested, int? year, ReleaseInfo parsed, bool viaId)
    {
        var wanted = Words(requested);
        var actual = Words(parsed.SeriesTitle);
        if (wanted.Count == 0 || !wanted.All(actual.Contains))
        {
            return viaId
                ? (VideoIdentityMatch.AmbiguousTitle, ReleaseIdentityEvidence.Ambiguous("AmbiguousTitle", $"Found by the provider id, but the release is named '{parsed.SeriesTitle}'."))
                : (VideoIdentityMatch.WrongTitle, ReleaseIdentityEvidence.Conflict("WrongTitle", $"'{parsed.SeriesTitle}' is another title."));
        }

        var years = actual.Where(word => YearToken().IsMatch(word) && !wanted.Contains(word)).Select(int.Parse).ToArray();
        var hasYear = year is not null && years.Length > 0 && years.Any(candidate => Math.Abs(candidate - year.Value) <= 1);
        if (year is not null && years.Length > 0 && !hasYear)
        {
            return (VideoIdentityMatch.WrongYear, ReleaseIdentityEvidence.Conflict("WrongYear", $"The release is from {string.Join('/', years)}, not {year}."));
        }

        // A sequel or spin-off shares the words of the requested title: anything beyond the title, the year and release tags is ambiguous.
        var extra = actual.Where(word => !wanted.Contains(word) && !YearToken().IsMatch(word)).ToArray();
        if (extra.Length > 0 && !viaId)
        {
            return (VideoIdentityMatch.AmbiguousTitle, ReleaseIdentityEvidence.Ambiguous("AmbiguousTitle", $"The title has extra words ({string.Join(' ', extra)}); it may be another film."));
        }

        return (VideoIdentityMatch.Matches, viaId || hasYear ? ReleaseIdentityEvidence.Exact("Matches", viaId ? "Found by the provider id; the title agrees." : "Title and year match.") : ReleaseIdentityEvidence.Strong("Matches", "The title matches."));
    }

    private static ReleaseJudgement<VideoIdentityMatch> JudgeUnit(VideoIdentityMatch titleMatch, ReleaseIdentityEvidence title, ReleaseInfo parsed, VideoUnit unit, VideoUnitScope scope, bool viaId)
    {
        if (parsed.SeasonNumber != unit.SeasonNumber)
        {
            return new ReleaseJudgement<VideoIdentityMatch>(VideoIdentityMatch.WrongSeason, parsed, ReleaseIdentityEvidence.Conflict("WrongSeason", $"The release is for season {parsed.SeasonNumber?.ToString() ?? "?"}, not {unit.SeasonNumber}."), SelectionCoverage.Single, null);
        }

        // An ambiguous title (found by id, named differently) stays ambiguous whatever the numbering says; otherwise exact numbering after an exact title is exact.
        ReleaseIdentityEvidence Fit(string code, string detail) =>
            title.Confidence == IdentityConfidence.Ambiguous ? title : viaId ? ReleaseIdentityEvidence.Exact(code, detail) : ReleaseIdentityEvidence.Strong(code, detail);

        var seasonUnits = scope.All.Where(candidate => candidate.SeasonNumber == unit.SeasonNumber).ToArray();
        if (parsed.IsSeasonPack)
        {
            var covered = scope.Wanted.Count(candidate => candidate.SeasonNumber == unit.SeasonNumber);
            return new ReleaseJudgement<VideoIdentityMatch>(VideoIdentityMatch.ContainsTarget, parsed, Fit("ContainsTarget", "A season pack that contains the requested episode."), new SelectionCoverage(covered, scope.Wanted.Count, Math.Max(0, seasonUnits.Length - covered)), null);
        }

        if (parsed.EpisodeStart is int start && parsed.EpisodeEnd is int end && unit.EpisodeNumber >= start && unit.EpisodeNumber <= end)
        {
            var covered = scope.Wanted.Count(candidate => candidate.SeasonNumber == unit.SeasonNumber && candidate.EpisodeNumber >= start && candidate.EpisodeNumber <= end);
            return new ReleaseJudgement<VideoIdentityMatch>(titleMatch == VideoIdentityMatch.AmbiguousTitle ? VideoIdentityMatch.AmbiguousTitle : VideoIdentityMatch.Matches, parsed, Fit("Matches", "The episode numbering matches."), new SelectionCoverage(Math.Max(1, covered), scope.Wanted.Count, Math.Max(0, end - start + 1 - covered)), null);
        }

        return new ReleaseJudgement<VideoIdentityMatch>(VideoIdentityMatch.WrongEpisode, parsed, ReleaseIdentityEvidence.Conflict("WrongEpisode", "The release is for another episode."), SelectionCoverage.Single, null);
    }

    private static HashSet<string> Words(string value) =>
        value.Split([' ', '.', '_', '-', ':', ',', '(', ')', '[', ']', '\'', '"', '!', '?', '&', '/'], StringSplitOptions.RemoveEmptyEntries)
            .Select(word => word.ToLowerInvariant())
            .Where(word => !TitleNoise.Contains(word))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
}
