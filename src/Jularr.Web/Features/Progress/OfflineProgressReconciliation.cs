namespace Jularr.Web.Features.Progress;

/// <summary>One playback checkpoint a native client recorded while it could not reach the server.</summary>
public sealed record OfflineProgressCheckpoint(
    Guid EpisodeId,
    long PositionMs,
    long? DurationMs,
    bool Completed);

public enum OfflineProgressOutcome
{
    /// <summary>The checkpoint moved the resume position forward.</summary>
    Applied,

    /// <summary>The checkpoint reached the end and marked the episode watched.</summary>
    Completed,

    /// <summary>The server already holds exactly this state (for example a replayed request).</summary>
    Unchanged,

    /// <summary>The server already holds a later resume position; progress never moves backwards.</summary>
    IgnoredBehind,

    /// <summary>The episode is watched; a partial offline checkpoint neither un-watches it nor replaces a newer resume position.</summary>
    IgnoredWatched,

    /// <summary>A first checkpoint below <see cref="EpisodeProgressService.MinimumResumeMs"/> is an accidental start.</summary>
    IgnoredAccidentalStart,

    EpisodeNotFound
}

public sealed record OfflineProgressReconciliationResult(
    Guid EpisodeId,
    OfflineProgressOutcome Outcome,
    EpisodeProgressSnapshot? Progress);

/// <summary>
/// Replays offline checkpoints into the canonical <see cref="EpisodeProgressService"/>.
/// Unlike a live checkpoint (which may legitimately rewind during a rewatch),
/// an offline checkpoint can arrive long after newer playback on another
/// device, so reconciliation is monotonic: it only ever moves the resume
/// position forward or marks an unwatched episode watched. Watched state stays
/// sticky and the 30 s accidental-start rule is applied unchanged. Because a
/// replay of an already reconciled checkpoint is always a no-op, the endpoint is
/// idempotent without storing any per-client delivery state on the server.
/// </summary>
public sealed class OfflineProgressReconciler(EpisodeProgressService progress)
{
    public async Task<IReadOnlyList<OfflineProgressReconciliationResult>> ReconcileAsync(
        IReadOnlyList<OfflineProgressCheckpoint> checkpoints,
        CancellationToken cancellationToken = default)
    {
        var results = new List<OfflineProgressReconciliationResult>(checkpoints.Count);

        foreach (var checkpoint in checkpoints)
        {
            var current = await progress.GetAsync(checkpoint.EpisodeId, cancellationToken);
            if (current is null)
            {
                results.Add(new(checkpoint.EpisodeId, OfflineProgressOutcome.EpisodeNotFound, null));
                continue;
            }

            var outcome = Decide(current, checkpoint);
            if (outcome is not (OfflineProgressOutcome.Applied or OfflineProgressOutcome.Completed))
            {
                results.Add(new(checkpoint.EpisodeId, outcome, current));
                continue;
            }

            var updated = await progress.UpdateAsync(
                checkpoint.EpisodeId,
                new EpisodeProgressUpdate(
                    checkpoint.PositionMs,
                    checkpoint.DurationMs,
                    outcome == OfflineProgressOutcome.Completed),
                cancellationToken);

            results.Add(new(checkpoint.EpisodeId, outcome, updated ?? current));
        }

        return results;
    }

    /// <summary>Pure reconciliation rule; see the class summary.</summary>
    public static OfflineProgressOutcome Decide(
        EpisodeProgressSnapshot current,
        OfflineProgressCheckpoint checkpoint)
    {
        var positionMs = Math.Max(0, checkpoint.PositionMs);
        var durationMs = checkpoint.DurationMs is > 0
            ? checkpoint.DurationMs
            : current.DurationMs;

        if (durationMs is { } duration)
        {
            positionMs = Math.Min(positionMs, duration);
        }

        if (checkpoint.Completed)
        {
            return current.IsCompleted
                ? OfflineProgressOutcome.Unchanged
                : OfflineProgressOutcome.Completed;
        }

        if (current.IsCompleted)
        {
            return OfflineProgressOutcome.IgnoredWatched;
        }

        var hasStoredState = current.UpdatedAt is not null;
        if (!hasStoredState && positionMs < EpisodeProgressService.MinimumResumeMs)
        {
            return OfflineProgressOutcome.IgnoredAccidentalStart;
        }

        if (positionMs == current.PositionMs)
        {
            return OfflineProgressOutcome.Unchanged;
        }

        return positionMs > current.PositionMs
            ? OfflineProgressOutcome.Applied
            : OfflineProgressOutcome.IgnoredBehind;
    }
}
