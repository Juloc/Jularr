using Jularr.Web.Data;
using Jularr.Web.Features.Acquisition.Monitoring;
using Jularr.Web.Features.Acquisition.Prowlarr;
using Jularr.Web.Features.Acquisition.Quality;
using Jularr.Web.Features.Operations;

namespace Jularr.Web.Features.Acquisition.Sabnzbd;

public sealed record SabnzbdAnimeAcquisitionRequest(
    string AnimeKey,
    string AnimeTitle,
    IReadOnlyList<AnimeEpisodeKey> Episodes,
    string? ProfileId,
    IReadOnlyList<SabnzbdAnimeReleaseCandidate> AcceptedCandidates,
    int MaxAttempts = SabnzbdAcquisitionService.DefaultMaxAttempts);

public sealed record SabnzbdAcquisitionResult(
    Guid AcquisitionId,
    Guid? OperationId,
    bool Submitted,
    string Message);

/// <summary>
/// Sends accepted anime releases to SABnzbd and, when a download fails,
/// blocklists that release and grabs the next accepted candidate within a
/// bounded number of attempts. Job state lives on the canonical Operation
/// of each attempt; this service only keeps the acquisition relation.
/// </summary>
public sealed class SabnzbdAcquisitionService(
    SabnzbdDownloadService downloads,
    SabnzbdAcquisitionStore store,
    AppDbContext db)
{
    public const string OperationKind = "anime-sabnzbd-download";
    public const int DefaultMaxAttempts = 3;
    public const int MaxAllowedAttempts = 10;

    /// <summary>
    /// Selects accepted usenet releases from a Prowlarr search, best first,
    /// using the canonical quality scorer.
    /// </summary>
    public static IReadOnlyList<SabnzbdAnimeReleaseCandidate> SelectAcceptedCandidates(
        IReadOnlyList<ProwlarrReleaseCandidate> releases,
        AnimeQualityProfile profile)
    {
        ArgumentNullException.ThrowIfNull(releases);
        ArgumentNullException.ThrowIfNull(profile);

        var byIdentity = releases
            .Where(release =>
                release.InternalDownloadUri is not null
                && string.Equals(release.Protocol, "usenet", StringComparison.OrdinalIgnoreCase))
            .GroupBy(release => release.Identity, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        return AnimeReleaseScorer
            .Rank(
                profile,
                byIdentity.Values.Select(release =>
                    new AnimeReleaseCandidate(
                        release.ParsedRelease,
                        release.SizeBytes,
                        release.Indexer,
                        release.Identity)))
            .Where(result => result.Accepted && result.Candidate.SourceId is not null)
            .Select(result => byIdentity[result.Candidate.SourceId!])
            .Select(release => new SabnzbdAnimeReleaseCandidate(
                release.Identity,
                release.Title,
                release.InternalDownloadUri!,
                release.Indexer,
                release.ParsedRelease.ReleaseGroup))
            .ToArray();
    }

    public async Task<SabnzbdAcquisitionResult> StartAsync(
        SabnzbdAnimeAcquisitionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.AnimeKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.AnimeTitle);
        ArgumentNullException.ThrowIfNull(request.Episodes);
        ArgumentNullException.ThrowIfNull(request.AcceptedCandidates);

        if (request.MaxAttempts is < 1 or > MaxAllowedAttempts)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                $"Max attempts must be between 1 and {MaxAllowedAttempts}.");
        }

        var candidates = request.AcceptedCandidates
            .Where(candidate =>
                !string.IsNullOrWhiteSpace(candidate.ReleaseIdentity)
                && !string.IsNullOrWhiteSpace(candidate.ReleaseTitle))
            .DistinctBy(candidate => candidate.ReleaseIdentity, StringComparer.OrdinalIgnoreCase)
            .Select(candidate => new SabnzbdPendingCandidate(
                candidate.ReleaseIdentity.Trim(),
                candidate.ReleaseTitle.Trim(),
                store.ProtectUrl(candidate.NzbUrl),
                candidate.ReleaseSource,
                candidate.ReleaseGroup))
            .ToArray();

        var now = DateTimeOffset.UtcNow;
        var acquisition = new SabnzbdAcquisition(
            Guid.NewGuid(),
            request.AnimeKey.Trim(),
            request.AnimeTitle.Trim(),
            request.Episodes.Distinct().ToArray(),
            request.ProfileId,
            request.MaxAttempts,
            [],
            candidates,
            now,
            now);

        await store.UpdateAsync(state => state.Acquisitions.Add(acquisition), cancellationToken);
        return await AdvanceAsync(acquisition.Id, previousOperationId: null, cancellationToken);
    }

    /// <summary>
    /// Called when the Operation of an attempt failed: blocklists that
    /// release and tries the next accepted candidate.
    /// </summary>
    public async Task<SabnzbdAcquisitionResult?> HandleFailedAsync(
        Guid operationId,
        SabnzbdFailureKind failureKind,
        string reason,
        CancellationToken cancellationToken)
    {
        var relation = await store.FindByOperationAsync(operationId, cancellationToken);
        if (relation is not { } found
            || found.Acquisition.LatestAttempt?.OperationId != operationId)
        {
            return null;
        }

        await store.BlockAsync(
            new SabnzbdBlockedRelease(
                found.Attempt.ReleaseIdentity,
                found.Attempt.ReleaseTitle,
                found.Acquisition.AnimeKey,
                failureKind,
                reason,
                operationId,
                DateTimeOffset.UtcNow),
            cancellationToken);

        return await AdvanceAsync(found.Acquisition.Id, operationId, cancellationToken);
    }

    /// <summary>
    /// Restart recovery: any acquisition whose latest attempt failed but was
    /// not advanced (for example because the process stopped in between)
    /// is blocklisted and advanced now. Idempotent.
    /// </summary>
    public async Task<int> RecoverAsync(CancellationToken cancellationToken)
    {
        var operations = new OperationStore(db);
        var state = await store.LoadAsync(cancellationToken);
        var advanced = 0;

        foreach (var acquisition in state.Acquisitions)
        {
            var latest = acquisition.LatestAttempt;
            if (latest is null || !CanAdvance(acquisition, state))
            {
                continue;
            }

            var operation = await operations.GetAsync(latest.OperationId, cancellationToken);
            if (operation?.Status != OperationStatus.Failed)
            {
                continue;
            }

            var result = await HandleFailedAsync(
                operation.Id,
                SabnzbdFailureKind.Unknown,
                operation.Error ?? "SABnzbd download failed.",
                cancellationToken);
            if (result?.Submitted == true)
            {
                advanced++;
            }
        }

        return advanced;
    }

    private async Task<SabnzbdAcquisitionResult> AdvanceAsync(
        Guid acquisitionId,
        Guid? previousOperationId,
        CancellationToken cancellationToken)
    {
        var operations = new OperationStore(db);
        var lastOperationId = previousOperationId;
        string? lastError = null;

        while (true)
        {
            var state = await store.LoadAsync(cancellationToken);
            var acquisition = state.Acquisitions.Single(item => item.Id == acquisitionId);

            if (!CanAdvance(acquisition, state))
            {
                var message = acquisition.Attempts.Length >= acquisition.MaxAttempts
                    ? $"Stopped after {acquisition.Attempts.Length} of {acquisition.MaxAttempts} attempts; no release was downloaded."
                    : "No further accepted release is available that is not blocklisted.";
                if (lastOperationId is { } exhaustedOperation)
                {
                    await operations.AppendLogAsync(
                        exhaustedOperation,
                        OperationLogLevel.Warning,
                        "Acquisition",
                        message,
                        CancellationToken.None);
                }

                return new SabnzbdAcquisitionResult(
                    acquisition.Id,
                    lastOperationId,
                    false,
                    lastError is null ? message : $"{lastError} {message}");
            }

            // Take the next candidate that is not blocklisted and persist
            // that it was consumed before submitting it.
            SabnzbdPendingCandidate? next = null;
            await store.UpdateAsync(
                current =>
                {
                    var index = current.Acquisitions.FindIndex(item => item.Id == acquisitionId);
                    var item = current.Acquisitions[index];
                    var remaining = item.PendingCandidates
                        .SkipWhile(candidate => current.IsBlocked(candidate.ReleaseIdentity))
                        .ToArray();
                    next = remaining.FirstOrDefault();
                    current.Acquisitions[index] = item with
                    {
                        PendingCandidates = remaining.Skip(1).ToArray(),
                        UpdatedAtUtc = DateTimeOffset.UtcNow
                    };
                },
                cancellationToken);

            if (next is null)
            {
                continue;
            }

            var attemptNumber = acquisition.Attempts.Length + 1;
            var outcome = await downloads.SubmitUrlAsync(
                new SabnzbdSubmission(
                    OperationKind,
                    $"Anime download · attempt {attemptNumber} of {acquisition.MaxAttempts}",
                    $"{acquisition.AnimeTitle} · {FormatEpisodes(acquisition.Episodes)} · {next.ReleaseTitle}",
                    acquisition.ProfileId,
                    SabnzbdPurpose.Anime,
                    JobName: next.ReleaseTitle,
                    ReleaseSource: next.ReleaseSource,
                    ReleaseGroup: next.ReleaseGroup),
                store.UnprotectUrl(next.ProtectedNzbUrl),
                cancellationToken);

            await store.UpdateAsync(
                current =>
                {
                    var index = current.Acquisitions.FindIndex(item => item.Id == acquisitionId);
                    var item = current.Acquisitions[index];
                    current.Acquisitions[index] = item with
                    {
                        Attempts =
                        [
                            .. item.Attempts,
                            new SabnzbdAcquisitionAttempt(
                                attemptNumber,
                                outcome.OperationId,
                                next.ReleaseIdentity,
                                next.ReleaseTitle,
                                DateTimeOffset.UtcNow)
                        ],
                        UpdatedAtUtc = DateTimeOffset.UtcNow
                    };
                },
                cancellationToken);

            if (lastOperationId is { } replaced)
            {
                await operations.AppendLogAsync(
                    replaced,
                    OperationLogLevel.Information,
                    "Acquisition",
                    $"Blocklisted this release and sent the next accepted release (attempt {attemptNumber} of {acquisition.MaxAttempts}): {next.ReleaseTitle}.",
                    CancellationToken.None);
            }

            if (outcome.Accepted)
            {
                return new SabnzbdAcquisitionResult(
                    acquisition.Id,
                    outcome.OperationId,
                    true,
                    outcome.Message);
            }

            // SABnzbd rejected the submission itself; treat it like a failed
            // download of that release and continue with the next one.
            await store.BlockAsync(
                new SabnzbdBlockedRelease(
                    next.ReleaseIdentity,
                    next.ReleaseTitle,
                    acquisition.AnimeKey,
                    SabnzbdFailureKind.Unknown,
                    outcome.Message,
                    outcome.OperationId,
                    DateTimeOffset.UtcNow),
                cancellationToken);
            lastOperationId = outcome.OperationId;
            lastError = outcome.Message;
        }
    }

    private static bool CanAdvance(
        SabnzbdAcquisition acquisition,
        SabnzbdAcquisitionStoreState state) =>
        acquisition.Attempts.Length < acquisition.MaxAttempts
        && acquisition.PendingCandidates.Any(candidate => !state.IsBlocked(candidate.ReleaseIdentity));

    public static string FormatEpisodes(IReadOnlyList<AnimeEpisodeKey> episodes)
    {
        if (episodes.Count == 0)
        {
            return "whole series";
        }

        var labels = episodes
            .OrderBy(episode => episode.SeasonNumber)
            .ThenBy(episode => episode.EpisodeNumber)
            .Select(episode => $"S{episode.SeasonNumber:00}E{episode.EpisodeNumber:00}")
            .ToArray();

        return labels.Length <= 3
            ? string.Join(", ", labels)
            : $"{string.Join(", ", labels.Take(3))} +{labels.Length - 3}";
    }
}
