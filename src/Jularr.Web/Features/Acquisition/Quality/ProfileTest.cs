using Jularr.Web.Features.Acquisition.Release;
using Jularr.Web.Features.Acquisition.Selection;

namespace Jularr.Web.Features.Acquisition.Quality;

/// <summary>What the selection engine says about one release name under a profile, in the words the Admin shows.</summary>
public sealed record ProfileTestResult(
    SelectionDecision Decision,
    string QualityKey,
    int Score,
    string? SourceProblem,
    IReadOnlyList<string> Reasons)
{
    /// <summary>Whether automatic acquisition would take the release now: it passes the engine and its source may be searched.</summary>
    public bool WouldGrab => SourceProblem is null && Decision == SelectionDecision.Eligible;
}

/// <summary>
/// Profile test: one typed release name run through the one <see cref="ReleaseSelectionEngine"/> with the profile as it is being edited and, optionally,
/// which indexer found it. It is not a scorer of its own; identity is assumed because there is no title to compare with, and every other gate
/// (quality, rules, size, source) is the real one.
/// </summary>
public static class ProfileTest
{
    public static ProfileTestResult Run(QualityProfile profile, IReleaseParser parser, string releaseName, long? sizeBytes, Guid? sourceEntryId)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(parser);

        var parsed = parser.TryParse(releaseName, out var release) ? release : null;
        var candidate = new SelectionCandidate("test", parsed, sizeBytes, null, 0, null, ReleaseIdentityEvidence.Exact("Assumed", "The test assumes the release is for the right title."), SelectionCoverage.Single);
        var evaluation = ReleaseSelectionEngine.Select(profile, [candidate]).Ranked[0];
        var sourceProblem = sourceEntryId is { } entry && profile.SourcePolicy.IsRestricted && !profile.SourcePolicy.AllowedEntryIds.Contains(entry)
            ? "The profile does not search this indexer, so the release would never be found."
            : null;
        return new ProfileTestResult(
            evaluation.Decision,
            evaluation.Score?.QualityKey ?? "",
            evaluation.PreferenceScore,
            sourceProblem,
            [.. evaluation.Reasons.Select(reason => reason.Detail), .. evaluation.Score?.RejectionReasons.Where(rejection => evaluation.Reasons.All(reason => reason.Detail != rejection)) ?? []]);
    }
}
