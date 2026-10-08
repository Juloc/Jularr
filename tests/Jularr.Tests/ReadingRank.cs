using Jularr.Web.Features.Acquisition.Core;
using Jularr.Web.Features.Acquisition.Prowlarr;
using Jularr.Web.Features.Acquisition.Quality;
using Jularr.Web.Features.ReadingAcquisition;

namespace Jularr.Tests;

internal sealed record RankedReading(ProwlarrReleaseCandidate Release, ReadingReleaseInfo Parsed, int Score, string? RejectedBecause);

// Ranks Manga and Light Novel releases the way the acquisition core does, without an indexer search.
internal static class ReadingRank
{
    public static IReadOnlyList<RankedReading> Rank(IReadOnlyList<ProwlarrReleaseCandidate> releases, ReadingAcquisitionTarget target, QualityProfile? profile = null, DateTimeOffset? wantedSince = null)
    {
        var now = DateTimeOffset.UtcNow;
        var (_, evaluations) = new ReleaseRanker(TimeProvider.System)
            .RankAsync(releases, release => ReadingReleaseJudge.Judge(release, target), profile ?? ReadingQualityProfiles.For(target.Kind), (wantedSince ?? now).UtcDateTime, CancellationToken.None)
            .GetAwaiter()
            .GetResult();
        return [.. evaluations.Select(evaluation => new RankedReading(evaluation.Candidate, evaluation.Match, ReadingReleaseJudge.DisplayScore(evaluation), ReadingReleaseJudge.RejectedBecause(evaluation)))];
    }

    public static RankedReading Judge(ProwlarrReleaseCandidate release, ReadingAcquisitionTarget target, QualityProfile? profile = null) => Rank([release], target, profile)[0];
}
