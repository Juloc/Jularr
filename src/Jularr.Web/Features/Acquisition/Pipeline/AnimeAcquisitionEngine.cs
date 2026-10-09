using Jularr.Web.Data;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.Core;
using Jularr.Web.Features.Acquisition.DownloadClients;
using Jularr.Web.Features.Acquisition.History;
using Jularr.Web.Features.Acquisition.Import;
using Jularr.Web.Features.Acquisition.Indexers;
using Jularr.Web.Features.Acquisition.Monitoring;
using Jularr.Web.Features.Acquisition.Ownership;
using Jularr.Web.Features.Acquisition.Sabnzbd;
using Jularr.Web.Features.Acquisition.Search;
using Jularr.Web.Features.Acquisition.Wanted;
using Jularr.Web.Features.MediaCore;
using Jularr.Web.Features.Operations;
using Jularr.Web.Features.Sonarr;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Web.Features.Acquisition.Pipeline;

// The Anime side of the shared request lifecycle: it picks the wanted episode of a request, searches and grabs through the shared core and leaves downloading,
// importing and retrying to the Wanted pass, like the other media types. What is Anime-specific stays in AnimeReleaseJudge (matching, mapping, packs) and in the
// ownership rules of the Sonarr coexistence modes.
public sealed class AnimeAcquisitionEngine(
    AppDbContext db,
    AnimeAcquisitionInventory inventory,
    AnimeMonitoringStore monitoringStore,
    IndexerSearchCoordinator indexers,
    DownloadClientStore downloadClients,
    AcquisitionCore core,
    ReleaseRequestTracker tracker,
    SonarrObservationService observation,
    AcquisitionOwnershipStore ownershipStore,
    SabnzbdAcquisitionStore blocklist,
    AcquisitionHistoryService history,
    TimeProvider clock)
{
    public const string OperationKind = "anime-usenet-download";

    /// <summary>The kind of the downloads the old Anime pipeline started; they still exist as Operations and are imported like the ones of requests.</summary>
    public const string LegacyOperationKind = "anime-sabnzbd-download";

    /// <summary>How long a request with nothing to search for yet waits before it looks again (a releasing series gains an episode every week at most).</summary>
    public static readonly TimeSpan IdleWait = TimeSpan.FromHours(6);

    // Searches for the first missing (else upgradable) episode of the request's scope and grabs the best release for it. The caller read the scope and found the
    // request neither complete nor held back by ownership.
    public async Task<AcquisitionExecution> SearchAndGrabAsync(AcquisitionRequest request, AnimeRequestScope scope, string waitingMessage, CancellationToken cancellationToken)
    {
        if (await InFlightAsync(request, scope.ResultUrl, cancellationToken) is { } inFlight)
        {
            return inFlight;
        }

        var key = scope.Slots.Anime.Key;
        var payload = AnimeRequestPayload.Of(request) with { AnimeKey = key, Episodes = null, ReleaseIdentity = null, ReleaseTitle = null };
        var now = clock.GetUtcNow();
        if (!await indexers.HasEnabledIndexerAsync(cancellationToken))
        {
            return await WaitAsync(request, payload, now + ReleaseRequestTracker.UnavailableRetry, "Not available yet. Searching is not set up on this server.", scope.ResultUrl, cancellationToken);
        }

        if (!(await downloadClients.LoadAllAsync(cancellationToken)).Any(entry => entry.Enabled))
        {
            return new AcquisitionExecution(AcquisitionRequestStatus.Failed, "No download client is configured.", ResultUrl: scope.ResultUrl);
        }

        if (await inventory.LoadAsync(key, cancellationToken) is not { } target)
        {
            return new AcquisitionExecution(AcquisitionRequestStatus.Failed, "The series is no longer in the library.");
        }

        var wantedEpisodes = WantedEpisodes(scope, now);
        if (wantedEpisodes.Count == 0)
        {
            return await WaitAsync(request, payload, now + IdleWait, waitingMessage, scope.ResultUrl, cancellationToken);
        }

        var unit = wantedEpisodes[0];
        var slot = target.Find(unit.Key.SeasonNumber, unit.Key.EpisodeNumber)!;
        // Every decision of the search is written to its Operation, so the owner can see why a release was accepted or rejected.
        var operations = new OperationStore(db);
        var searchOperation = await operations.CreateAsync(
            new OperationDescriptor(AnimeAcquisitionPipeline.SearchOperationKind, AnimeAcquisitionPipeline.OperationCategory, "Anime search", $"{target.Anime.Title} · {AnimeAcquisitionPipeline.Label(unit.Key)} · {unit.Reason}", target.Profile.Id, OperationLane.Normal, Retryable: false),
            cancellationToken);
        await operations.MarkRunningAsync(searchOperation, cancellationToken);
        var searchTarget = AnimeReleaseJudge.PlannedTargetFor(target, slot, wantedEpisodes);
        var settings = (await monitoringStore.LoadAsync(cancellationToken)).Anime.GetValueOrDefault(key);
        var snapshot = await observation.GetSnapshotAsync(forceRefresh: true, cancellationToken);
        var blocked = (await blocklist.LoadAsync(cancellationToken)).IsBlocked;
        var plan = AnimeReleaseJudge.Plan(target, [slot], wantedEpisodes, slot.Key, searchTarget, snapshot, now, blocked);
        var search = await core.SearchAsync(plan, target.Profile, new SearchOptions { ProwlarrIndexerIds = settings?.IndexerIds }, cancellationToken);

        var candidates = AnimeReleaseJudge.ToCandidates(target, [slot], search);
        foreach (var warning in search.Search.Warnings)
        {
            await operations.AppendLogAsync(searchOperation, OperationLogLevel.Warning, AnimeAcquisitionPipeline.LogModule, $"{warning.IndexerName}: {warning.Message}{(string.IsNullOrEmpty(warning.Query) ? "" : $" ({warning.Query})")}", cancellationToken);
        }

        await AnimeAcquisitionPipeline.LogDecisionsAsync(operations, searchOperation, candidates, cancellationToken);
        var accepted = candidates.Where(candidate => candidate.Decision.Grab).Select(candidate => candidate.Release.Identity).ToHashSet(StringComparer.Ordinal);
        var grabbable = search.Releases.Where(release => release.IsGrabbable && accepted.Contains(release.Candidate.Identity)).ToArray();
        var next = grabbable.FirstOrDefault(release => !(payload.TriedReleases ?? []).Contains(release.Candidate.Identity, StringComparer.OrdinalIgnoreCase));
        if (next is not null)
        {
            payload = payload with
            {
                Episodes = next.Match.Covered.Count > 0 ? next.Match.Covered : [unit.Key],
                ReleaseIdentity = next.Candidate.Identity,
                ReleaseTitle = next.Candidate.Title
            };
        }

        var label = AnimeAcquisitionPipeline.Label(unit.Key);
        var execution = await core.GrabAsync(
            request,
            payload,
            grabbable,
            search.Releases.Count == 0 ? "No release found on the indexers." : "No accepted release matched the wanted episode.",
            new GrabTarget(OperationKind, "Anime download", $"{target.Anime.Title} · {label}", MediaAcquisitionKind.Anime, key),
            cancellationToken,
            searchUnavailable: search.Search.EveryIndexerFailed);
        if (execution.Status == AcquisitionRequestStatus.Downloading && next is not null && execution.OperationId is { } operationId)
        {
            await RecordGrabAsync(target, payload.Episodes!, next, operationId, now, cancellationToken);
            var message = $"Sent to SABnzbd: {next.Candidate.Title} for {AnimeAcquisitionPipeline.FormatEpisodes(payload.Episodes!)}.";
            await operations.AppendLogAsync(searchOperation, OperationLogLevel.Information, AnimeAcquisitionPipeline.LogModule, message, cancellationToken);
            await operations.MarkSucceededAsync(searchOperation, message, CancellationToken.None);
        }
        else
        {
            await operations.MarkSucceededAsync(searchOperation, $"No accepted release among {candidates.Count} result(s); retried after backoff.", CancellationToken.None);
        }

        return execution with { ResultUrl = scope.ResultUrl };
    }

    // A request with a grab whose import has not completed yet (its payload still names the episodes) searches nothing: running it again never grabs twice.
    private async Task<AcquisitionExecution?> InFlightAsync(AcquisitionRequest request, string resultUrl, CancellationToken cancellationToken)
    {
        if (AnimeRequestPayload.Of(request).Episodes is not { Count: > 0 } || request.OperationId is not { } operationId)
        {
            return null;
        }

        return (await new OperationStore(db).GetAsync(operationId, cancellationToken))?.Status switch
        {
            OperationStatus.Queued or OperationStatus.Running => new AcquisitionExecution(AcquisitionRequestStatus.Downloading, "Download is in progress.", operationId, resultUrl),
            OperationStatus.Succeeded => new AcquisitionExecution(AcquisitionRequestStatus.Importing, "Download complete. Importing into the library.", operationId, resultUrl),
            _ => null
        };
    }

    // The wanted episodes of the request in order: what is missing first, then what the shared upgrade policy still wants better.
    private static IReadOnlyList<AnimeWantedEpisode> WantedEpisodes(AnimeRequestScope scope, DateTimeOffset now) =>
    [
        .. scope.Missing.Select(key => new AnimeWantedEpisode(key, AnimeWantedReason.Missing, now)),
        .. scope.Upgradable.Select(key => new AnimeWantedEpisode(key, AnimeWantedReason.CutoffUnmet, now))
    ];

    // The grab is recorded like the old pipeline did: the Jularr ownership of the job (so the importer may place its files) and one history entry per episode.
    public async Task RecordGrabAsync(AnimeAcquisitionTarget target, IReadOnlyList<AnimeEpisodeKey> episodes, ReleaseEvaluation<AnimeMatch> chosen, Guid operationId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var releaseKey = chosen.Candidate.ParsedRelease.ReleaseKey;
        var download = await new OperationStore(db).GetAsync(operationId, cancellationToken);
        await ownershipStore.UpdateAsync(
            current => SonarrParallelSafety.RegisterJob(current, new AcquisitionOwnership(operationId.ToString(), target.Anime.Key, AcquisitionOwner.Jularr, releaseKey, AcquisitionOwnershipStatus.Pending, now, download?.ExternalId)),
            cancellationToken);
        foreach (var episode in episodes)
        {
            await history.RecordAsync(
                new AcquisitionHistoryEntry
                {
                    AnimeId = target.Anime.Id,
                    SeasonNumber = episode.SeasonNumber,
                    EpisodeNumber = episode.EpisodeNumber,
                    AbsoluteEpisodeNumber = episode.AbsoluteEpisodeNumber,
                    EventKind = AcquisitionHistoryEventKind.Grabbed,
                    ReleaseTitle = chosen.Candidate.Title,
                    ReleaseKey = releaseKey,
                    Score = chosen.Score?.Score ?? 0,
                    QualityKey = chosen.Score?.QualityKey,
                    Indexer = chosen.Candidate.Indexer,
                    Reason = "Accepted candidate for a wanted episode.",
                    OccurredAtUtc = now.UtcDateTime
                },
                cancellationToken);
        }
    }

    private async Task<AcquisitionExecution> WaitAsync(AcquisitionRequest request, AnimeRequestPayload payload, DateTimeOffset until, string message, string resultUrl, CancellationToken cancellationToken)
    {
        await tracker.SaveAsync(request, payload with { Searches = 0, LastProblem = null, NextSearchUtc = until.UtcDateTime }, cancellationToken);
        return new AcquisitionExecution(AcquisitionRequestStatus.Approved, message, ResultUrl: resultUrl);
    }
}

/// <summary>Anime requests on the shared Wanted lifecycle: due searches follow the request's back-off, a bad release is blocklisted unless the client's own storage failed, and an import goes on with the next episode.</summary>
public sealed class AnimeWantedRequestHandler(AcquisitionAccessStore store, AcquisitionRequestService requests, SabnzbdAcquisitionStore blocklist, AppDbContext db, TimeProvider clock) : IWantedRequestHandler
{
    public MediaAcquisitionKind Kind => MediaAcquisitionKind.Anime;

    public bool IsSearchDue(AcquisitionRequest request, DateTime nowUtc) => ReleaseRequestTracker.IsSearchDue(AnimeRequestPayload.Of(request), nowUtc);

    public async Task ContinueAfterProblemAsync(AcquisitionRequest request, string problem, CancellationToken cancellationToken)
    {
        var payload = AnimeRequestPayload.Of(request);
        var operation = request.OperationId is { } operationId ? await new OperationStore(db).GetAsync(operationId, cancellationToken) : null;
        var failureKind = operation is not null && DownloadOperationDetails.TryParse(operation.Details, out var details) && Enum.TryParse<SabnzbdFailureKind>(details?.FailureKind, ignoreCase: true, out var parsed) ? parsed : SabnzbdFailureKind.Unknown;
        if (SabnzbdFailureKinds.IsInfrastructure(failureKind))
        {
            // The client's own storage or scripts failed, which says nothing about the release: it is not blocklisted or tried, and the episode is searched again later.
            var retry = payload with
            {
                Episodes = null,
                ReleaseIdentity = null,
                ReleaseTitle = null,
                TriedReleases = (payload.TriedReleases ?? []).Where(identity => !string.Equals(identity, payload.ReleaseIdentity, StringComparison.OrdinalIgnoreCase)).ToArray(),
                Searches = Math.Max(0, payload.Searches - 1),
                LastProblem = problem,
                NextSearchUtc = clock.GetUtcNow().UtcDateTime + ReleaseRequestTracker.UnavailableRetry
            };
            await store.PatchPayloadAsync(request.Id, _ => retry.Serialize(), cancellationToken);
            await store.UpdateStatusAsync(request.Id, AcquisitionRequestStatus.Approved, $"{problem} Searching again later.", null, null, null, cancellationToken);
            return;
        }

        if (payload.ReleaseIdentity is { Length: > 0 } identity)
        {
            await blocklist.BlockAsync(new SabnzbdBlockedRelease(identity, payload.ReleaseTitle ?? identity, payload.AnimeKey ?? "", failureKind, problem, request.OperationId, clock.GetUtcNow()), cancellationToken);
        }

        await store.PatchPayloadAsync(request.Id, _ => ReleaseRequestTracker.AfterProblem(payload with { Episodes = null }, problem).Serialize(), cancellationToken);
        await requests.ContinueAsync(request.Id, cancellationToken);
    }

    // The imported episodes are in the library now; the same lifecycle looks for the next wanted one, or completes the request.
    public async Task<bool> ContinueAfterCompletedImportAsync(AcquisitionRequest request, CompletedDownloadImportResult result, CancellationToken cancellationToken)
    {
        await store.PatchPayloadAsync(request.Id, _ => (AnimeRequestPayload.Of(request) with { Episodes = null, ReleaseIdentity = null, ReleaseTitle = null, TriedReleases = [], Searches = 0, LastProblem = null, NextSearchUtc = null }).Serialize(), cancellationToken);
        await requests.ContinueAsync(request.Id, cancellationToken);
        return true;
    }
}
