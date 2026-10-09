using Jularr.Web.Features.Progress;

namespace Jularr.Web.Features.Playback.Decision;

/// <summary>
/// Inputs from canonical owners only. Non-resume signals must be supplied by their actual owner;
/// absent evidence is zero, never a fabricated popularity score.
/// </summary>
public sealed record PlaybackPreparationCandidate(
    long WorkId,
    Guid? WorkEpisodeId,
    PlaybackPreparationCost Cost,
    PlaybackPreparationCapacity Capacity,
    int UpNextProfiles = 0,
    int WatchlistProfiles = 0,
    int RecentLiveTranscodes = 0);

public sealed record PlaybackPreparationCandidateAssessment(
    long WorkId,
    Guid? WorkEpisodeId,
    PlaybackPreparationAssessment Assessment);

/// <summary>
/// Read-only bridge between canonical playback progress and the optional ROI gate.
/// This does not enqueue, probe, transcode, wake storage or create any rendition.
/// </summary>
public sealed class PlaybackPreparationDemandService(VideoProgressService progress)
{
    public async Task<IReadOnlyList<PlaybackPreparationCandidateAssessment>> AssessAsync(
        IReadOnlyCollection<PlaybackPreparationCandidate> candidates,
        DateTime sinceUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        if (sinceUtc.Kind != DateTimeKind.Utc)
        {
            throw new ArgumentException("Demand cutoff must be UTC.", nameof(sinceUtc));
        }

        if (candidates.Count > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(candidates), "A preparation assessment covers at most 100 targets.");
        }

        var batch = candidates.ToArray();
        if (batch.Any(candidate => candidate.WorkId <= 0) ||
            batch.Select(candidate => (candidate.WorkId, candidate.WorkEpisodeId)).Distinct().Count() != batch.Length)
        {
            throw new ArgumentException("Preparation targets must have valid unique canonical identities.", nameof(candidates));
        }

        // No database reads while disabled or while the server cannot safely prepare anything.
        // The cost/space decision remains with the one canonical ROI evaluator below.
        var workIds = batch
            .Where(candidate => candidate.Capacity.AdminEnabled &&
                                candidate.Capacity.InIdleWindow &&
                                !candidate.Capacity.InteractiveLoad &&
                                candidate.Capacity.SourceAvailableWithoutWake &&
                                !candidate.Capacity.AlreadyHasCompatibleRendition &&
                                candidate.Capacity.NeedsVideoConversion)
            .Select(candidate => candidate.WorkId)
            .Distinct()
            .ToArray();

        IReadOnlyList<VideoResumeDemandCount> resume = workIds.Length == 0
            ? []
            : await progress.GetRecentResumeDemandAsync(workIds, sinceUtc, cancellationToken);

        var counts = resume.ToDictionary(
            item => (item.WorkId, item.WorkEpisodeId),
            item => (int)Math.Min(int.MaxValue, Math.Max(0, item.ResumeProfiles)));

        return batch.Select(candidate =>
        {
            counts.TryGetValue((candidate.WorkId, candidate.WorkEpisodeId), out var profiles);
            var evidence = new PlaybackPreparationEvidence(
                profiles,
                candidate.UpNextProfiles,
                candidate.WatchlistProfiles,
                candidate.RecentLiveTranscodes);
            return new PlaybackPreparationCandidateAssessment(
                candidate.WorkId,
                candidate.WorkEpisodeId,
                PlaybackPreparationDemand.Evaluate(evidence, candidate.Cost, candidate.Capacity));
        }).ToArray();
    }
}
