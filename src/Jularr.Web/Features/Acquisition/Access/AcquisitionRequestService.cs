using Jularr.Web.Features.Auth;
using Jularr.Web.Features.Events;
using Jularr.Web.Features.Instance;

namespace Jularr.Web.Features.Acquisition.Access;

/// <summary>
/// The one path for requesting a title from search, for every media type. What the profile may do comes
/// from the capability matrix (#436): a profile with <see cref="MediaCapability.Instant"/> has its request
/// approved right away, one with <see cref="MediaCapability.Request"/> creates a request, and anything
/// below cannot request. A request waits for the owner unless an auto-approval rule approves it; an
/// approved request goes to the media type's executor.
/// </summary>
public sealed class AcquisitionRequestService(
    AcquisitionAccessStore store,
    IEnumerable<IAcquisitionRequestExecutor> executors,
    CurrentAccountContext account,
    IMediaCapabilityService mediaCapabilities,
    AcquisitionRequestSettingsStore requestSettings,
    IJularrEventPublisher events,
    ILogger<AcquisitionRequestService> logger,
    IInstanceModuleService? instanceModules = null)
{
    /// <summary>Where a profile finds the state of its requests; decision notifications open it.</summary>
    public const string HistoryPath = "/Requests";

    public async Task<AcquisitionCapabilities> GetCapabilitiesAsync(
        MediaAcquisitionKind kind,
        CancellationToken cancellationToken)
    {
        var capability = await mediaCapabilities.GetEffectiveCapabilityAsync(
            account.User,
            AcquisitionAccessNames.WorkType(kind),
            cancellationToken);

        if (instanceModules is not null)
        {
            var instance = await instanceModules.GetAsync(cancellationToken);
            if (!instance.IsEnabled(InstanceModule.Acquisition)
                || !instance.IsEnabled(AcquisitionInstanceModules.For(kind)))
            {
                capability = MediaCapability.Hidden;
            }
        }

        var policy = await store.GetPolicyAsync(kind, cancellationToken);
        return AcquisitionCapabilities.Resolve(kind, capability, policy.Manual, account.Can(JularrPolicies.AdminMedia));
    }

    /// <summary>Requests a title. Returns the open request for it, new or existing.</summary>
    public async Task<AcquisitionRequest> SubmitAsync(AcquisitionRequestDraft draft, CancellationToken cancellationToken) =>
        (await SubmitWithOutcomeAsync(draft, cancellationToken)).Request;

    /// <summary>
    /// Requests a title and says whether the title already had an open request. A title is only ever requested once
    /// at a time: when another profile wins the race for the same title, the unique index rejects this insert and the
    /// winner's open request is returned, so a concurrent duplicate never fails and never reads as a new request.
    /// </summary>
    public async Task<AcquisitionSubmission> SubmitWithOutcomeAsync(AcquisitionRequestDraft draft, CancellationToken cancellationToken)
    {
        var capabilities = await GetCapabilitiesAsync(draft.Kind, cancellationToken);
        if (!capabilities.CanRequest)
        {
            throw new AcquisitionAccessDeniedException("You may not request this kind of media.");
        }

        draft = await PrepareDraftAsync(draft, cancellationToken);
        if (await store.FindOpenAsync(draft.Kind, draft.Provider, draft.ExternalId, cancellationToken) is { } open)
        {
            return new AcquisitionSubmission(open, AlreadyRequested: true);
        }

        var status = AcquisitionRequestStatus.Approved;
        var decidedBy = account.ProfileId;
        if (!capabilities.AutoApproves)
        {
            var decision = await EvaluateAutoApprovalAsync(draft.Kind, cancellationToken);
            status = decision.Rule is null ? AcquisitionRequestStatus.Pending : AcquisitionRequestStatus.Approved;
            decidedBy = decision.Rule is { } rule ? AcquisitionAutoApproval.DecidedBy(rule.Id) : null;
        }

        AcquisitionRequest created;
        try
        {
            created = await store.CreateAsync(draft, account.ProfileId, status, decidedBy, cancellationToken);
        }
        catch (OpenRequestExistsException)
        {
            var winner = await store.FindOpenAsync(draft.Kind, draft.Provider, draft.ExternalId, cancellationToken)
                ?? throw new InvalidOperationException("The open request for this title disappeared while it was being requested.");
            return new AcquisitionSubmission(winner, AlreadyRequested: true);
        }

        return new AcquisitionSubmission(status == AcquisitionRequestStatus.Pending ? created : await ExecuteAsync(created, cancellationToken), AlreadyRequested: false);
    }

    public async Task<AcquisitionRequest> ApproveAsync(Guid id, CancellationToken cancellationToken)
    {
        RequireRequestManager();
        var request = await RequireAsync(id, cancellationToken);
        // Approved requests that wait for a release can be searched again right away.
        if (request.Status is not (AcquisitionRequestStatus.Pending or AcquisitionRequestStatus.Failed or AcquisitionRequestStatus.Approved))
        {
            return request;
        }

        await store.UpdateStatusAsync(id, AcquisitionRequestStatus.Approved, null, null, null, account.ProfileId, cancellationToken);
        await PublishDecisionAsync(request, JularrEventCategory.RequestApproved, cancellationToken);
        return await ExecuteAsync(await RequireAsync(id, cancellationToken), cancellationToken);
    }

    /// <summary>
    /// Runs an already approved request again without a signed-in owner: a background search for a
    /// title that had no release yet, or the next release after a failed download.
    /// </summary>
    public async Task<AcquisitionRequest> ContinueAsync(Guid id, CancellationToken cancellationToken)
    {
        var request = await RequireAsync(id, cancellationToken);
        if (request.Status is not (
                AcquisitionRequestStatus.Approved or
                AcquisitionRequestStatus.Downloading or
                AcquisitionRequestStatus.Importing))
        {
            return request;
        }

        return await ExecuteAsync(request, cancellationToken);
    }

    /// <summary>
    /// Brings a request of a monitored media type (<see cref="IMonitoredAcquisitionExecutor"/>) to the state its pipeline is in, as read by
    /// <paramref name="observation"/>. It only reads that state and never searches or grabs, so repeating it changes nothing. The change is
    /// conditional on the status the request was read with, so a request somebody else moved on meanwhile (an owner marking it done) keeps its
    /// new state. A request whose executor has not run yet is reported as such and left as it is.
    /// </summary>
    public async Task<MonitoredFollowOutcome> FollowMonitoredAsync(AcquisitionRequest request, IRequestObservation observation, CancellationToken cancellationToken)
    {
        if (!request.IsObservedFromMonitoring)
        {
            return MonitoredFollowOutcome.Unchanged;
        }

        if (await observation.ObserveAsync(request, cancellationToken) is not { } observed)
        {
            return MonitoredFollowOutcome.NotExecuted;
        }

        var clearOperation = observed.Status == AcquisitionRequestStatus.Approved;
        var unchanged = observed.Status == request.Status
            && observed.Message == request.StatusMessage
            && (observed.OperationId is null || observed.OperationId == request.OperationId)
            && (observed.ResultUrl is null || observed.ResultUrl == request.ResultUrl)
            && !(clearOperation && request.OperationId is not null);
        if (unchanged)
        {
            return MonitoredFollowOutcome.Unchanged;
        }

        var moved = await store.TryTransitionStatusAsync(request.Id, [request.Status], observed.Status, observed.Message, observed.OperationId, observed.ResultUrl, clearOperation, cancellationToken);
        if (moved is null)
        {
            return MonitoredFollowOutcome.Unchanged;
        }

        // The requester hears once per download: not when the same download is only seen again, and not on a flap between its stages.
        if (observed.Status == AcquisitionRequestStatus.Downloading && request.Status == AcquisitionRequestStatus.Approved && observed.OperationId != request.OperationId)
        {
            await PublishReleaseAvailableAsync(request, observed, cancellationToken);
        }

        return MonitoredFollowOutcome.Changed;
    }

    public async Task RejectAsync(Guid id, string? note, CancellationToken cancellationToken)
    {
        RequireRequestManager();
        var request = await RequireAsync(id, cancellationToken);
        if (request.Status != AcquisitionRequestStatus.Pending)
        {
            return;
        }

        await store.UpdateStatusAsync(
            id,
            AcquisitionRequestStatus.Rejected,
            string.IsNullOrWhiteSpace(note) ? null : note.Trim(),
            null,
            null,
            account.ProfileId,
            cancellationToken);
        await PublishDecisionAsync(request, JularrEventCategory.RequestDenied, cancellationToken);
    }

    /// <summary>
    /// Puts a rejected (or withdrawn) request back in front of the owner. A title is only ever requested
    /// once at a time, so nothing changes when another request for it is open already.
    /// </summary>
    public async Task<AcquisitionRequest> ReopenAsync(Guid id, CancellationToken cancellationToken)
    {
        RequireRequestManager();
        var request = await RequireAsync(id, cancellationToken);
        if (request.Status != AcquisitionRequestStatus.Rejected
            || await store.FindOpenAsync(request.Kind, request.Provider, request.ExternalId, cancellationToken) is not null)
        {
            return request;
        }

        await store.UpdateStatusAsync(id, AcquisitionRequestStatus.Pending, null, null, null, null, cancellationToken);
        return await RequireAsync(id, cancellationToken);
    }

    /// <summary>For media types without automatic acquisition: the owner added it by hand.</summary>
    public async Task MarkCompletedAsync(Guid id, CancellationToken cancellationToken)
    {
        RequireRequestManager();
        var request = await RequireAsync(id, cancellationToken);
        if (!request.IsOpen)
        {
            return;
        }

        await store.UpdateStatusAsync(id, AcquisitionRequestStatus.Completed, null, null, null, account.ProfileId, cancellationToken);
    }

    /// <summary>A requester may withdraw their own pending request; the owner may withdraw any.</summary>
    public async Task CancelAsync(Guid id, CancellationToken cancellationToken)
    {
        var request = await RequireAsync(id, cancellationToken);
        if (request.Status != AcquisitionRequestStatus.Pending)
        {
            return;
        }

        if (!account.Can(JularrPolicies.AdminMedia) && request.RequestedByProfileId != account.ProfileId)
        {
            throw new AcquisitionAccessDeniedException("Only the requester, the owner or a media manager can withdraw a request.");
        }

        await store.UpdateStatusAsync(id, AcquisitionRequestStatus.Rejected, "Withdrawn.", null, null, account.ProfileId, cancellationToken);
    }

    /// <summary>
    /// Validates the richer options of a draft and moves them into the payload. Only anime titles have
    /// options (the other media types use the payload for their own state), and a requester without the
    /// media manager role may only pick a quality profile the owner opened to requests.
    /// </summary>
    private async Task<AcquisitionRequestDraft> PrepareDraftAsync(
        AcquisitionRequestDraft draft,
        CancellationToken cancellationToken)
    {
        if (draft.Options is not { } chosen)
        {
            return draft;
        }

        if (draft.Kind != MediaAcquisitionKind.Anime || draft.PayloadJson is not null)
        {
            throw new ArgumentException("Request options are only available for anime titles.", nameof(draft));
        }

        var options = chosen.Validate();
        if (options.QualityProfileId is { } profileId && !account.Can(JularrPolicies.AdminMedia))
        {
            var allowed = (await requestSettings.LoadAsync(cancellationToken)).RequesterQualityProfileIds;
            if (!allowed.Contains(profileId, StringComparer.Ordinal))
            {
                throw new AcquisitionAccessDeniedException("That quality profile is not open to requests.");
            }
        }

        return draft with { PayloadJson = options.ToPayloadJson(), Options = null };
    }

    /// <summary>Whether an auto-approval rule approves a new request of the signed-in profile (quota counted per rule).</summary>
    private async Task<AutoApprovalDecision> EvaluateAutoApprovalAsync(
        MediaAcquisitionKind kind,
        CancellationToken cancellationToken)
    {
        var rules = (await requestSettings.LoadAsync(cancellationToken)).AutoApprovalRules;
        var profileId = account.ProfileId;
        var used = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var rule in AutoApprovalEvaluator.QuotaRulesFor(rules, kind, profileId))
        {
            used[rule.Id] = await store.CountAutoApprovedSinceAsync(
                profileId,
                rule.Id,
                DateTime.UtcNow - rule.Quota!.Period,
                cancellationToken);
        }

        return AutoApprovalEvaluator.Evaluate(rules, kind, profileId, used);
    }

    private async Task<AcquisitionRequest> ExecuteAsync(AcquisitionRequest request, CancellationToken cancellationToken)
    {
        if (instanceModules is not null)
        {
            var instance = await instanceModules.GetAsync(cancellationToken);
            if (!instance.IsEnabled(InstanceModule.Acquisition)
                || !instance.IsEnabled(AcquisitionInstanceModules.For(request.Kind)))
            {
                await store.UpdateStatusAsync(
                    request.Id,
                    AcquisitionRequestStatus.Approved,
                    "Acquisition is disabled for this media type.",
                    null,
                    null,
                    null,
                    cancellationToken);
                return await RequireAsync(request.Id, cancellationToken);
            }
        }

        var executor = executors.FirstOrDefault(candidate => candidate.Kind == request.Kind);
        if (executor is null)
        {
            // No automatic acquisition for this media type yet: the approved request is the owner's to-do.
            await store.UpdateStatusAsync(
                request.Id,
                AcquisitionRequestStatus.Approved,
                "Approved — the owner adds it to the library.",
                null,
                null,
                null,
                cancellationToken);
            return await RequireAsync(request.Id, cancellationToken);
        }

        // Claim the request with one conditional write: a Manual Search grab (or another pass) that took it meanwhile keeps it.
        if (await store.TryTransitionStatusAsync(request.Id, AcquisitionAccessNames.UnderwayStatuses, AcquisitionRequestStatus.Searching, null, null, cancellationToken) is null)
        {
            return await RequireAsync(request.Id, cancellationToken);
        }

        AcquisitionExecution result;
        var threw = false;
        try
        {
            result = await executor.ExecuteAsync(request, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(exception, "Acquisition request {RequestId} ({Kind}) failed.", request.Id, request.Kind);
            result = new AcquisitionExecution(AcquisitionRequestStatus.Failed, exception.Message);
            threw = true;
        }

        await ApplyExecutionAsync(request, result, threw, cancellationToken);
        return await RequireAsync(request.Id, cancellationToken);
    }

    /// <summary>
    /// Applies the outcome of a release the owner selected by hand (Manual Search) exactly as the outcome of an automatic
    /// execution: the same status, operation link and release-available notification.
    /// </summary>
    public async Task<AcquisitionRequest> ApplyManualExecutionAsync(Guid id, AcquisitionExecution result, CancellationToken cancellationToken)
    {
        RequireRequestManager();
        await ApplyExecutionAsync(await RequireAsync(id, cancellationToken), result, threw: false, cancellationToken);
        return await RequireAsync(id, cancellationToken);
    }

    /// <summary>The status of a result that was computed from a payload: the result itself while the payload still says what the run read, else back to Approved.</summary>
    private static AcquisitionStatusOutcome StatusOf(AcquisitionExecution result, bool stillApplies) =>
        stillApplies
            ? new AcquisitionStatusOutcome(result.Status, result.Message, result.ResultUrl)
            : new AcquisitionStatusOutcome(AcquisitionRequestStatus.Approved, "The request changed while it was being worked on; searching again.", result.ResultUrl);

    private async Task ApplyExecutionAsync(AcquisitionRequest request, AcquisitionExecution result, bool threw, CancellationToken cancellationToken)
    {
        // Searching is what the run (or the Manual Search claim) set before. If somebody moved the request on meanwhile (an Admin ending it), that
        // is not overwritten, except that a download which was already handed over is always recorded: it exists and its import must complete.
        // A request of a monitored media type that goes back to waiting, or that its executor fails for good, without a download of its own no
        // longer has the download it had. A failure the executor threw may be transient, so it keeps the link, and for the other media types the
        // last download stays linked so the owner can retry it.
        var clearOperation = result.OperationId is null
            && (result.Status == AcquisitionRequestStatus.Approved || (result.Status == AcquisitionRequestStatus.Failed && !threw))
            && executors.OfType<IMonitoredAcquisitionExecutor>().Any(candidate => candidate.Kind == request.Kind);
        var applied = result.StillApplies is { } stillApplies
            ? await store.PatchPayloadAsync(request.Id, stored => stored, AcquisitionRequestStatus.Searching, stored => StatusOf(result, stillApplies(stored)), cancellationToken)
            : await store.TryTransitionStatusAsync(request.Id, [AcquisitionRequestStatus.Searching], result.Status, result.Message, result.OperationId, result.ResultUrl, clearOperation, cancellationToken) is not null;
        if (!applied && result.Status == AcquisitionRequestStatus.Downloading)
        {
            await store.UpdateStatusAsync(request.Id, result.Status, result.Message, result.OperationId, result.ResultUrl, null, cancellationToken);
        }

        // Every media kind's executor reports Downloading the moment it finds and grabs an
        // accepted release, so this one spot covers #579's "ReleaseAvailable when a wanted
        // release is found" for Anime, Manga, Light Novels and Books alike. Seeing the download the request
        // already has again (an owner's approval or retry while it runs) is not a new release.
        if (result.Status == AcquisitionRequestStatus.Downloading && (result.OperationId is null || result.OperationId != request.OperationId))
        {
            await PublishReleaseAvailableAsync(request, result, cancellationToken);
        }
    }

    /// <summary>
    /// Notifies the requester of the owner's decision (#429). Delivery is fully decoupled here:
    /// a channel failure inside <see cref="IJularrEventPublisher"/> is already caught and logged
    /// by the publisher, so it can never turn an approval/rejection into a failed request.
    /// </summary>
    private Task PublishDecisionAsync(AcquisitionRequest request, JularrEventCategory category, CancellationToken cancellationToken) =>
        events.PublishAsync(
            JularrEvent.Create(
                category,
                profileId: request.RequestedByProfileId,
                mediaType: AcquisitionAccessNames.Kind(request.Kind),
                subjectId: request.Id.ToString(),
                messageParams: new Dictionary<string, string> { ["title"] = request.Title },
                deepLink: request.ResultUrl ?? HistoryPath,
                dedupKey: $"acquisition-request:{request.Id}:{category}"),
            cancellationToken);

    /// <summary>
    /// #579: tells the requester a release was found and grabbed for their request. Same dedup
    /// shape as <see cref="PublishDecisionAsync"/> keyed on the request, so a later release found
    /// after an earlier one failed refreshes one notification instead of piling up new rows.
    /// </summary>
    private Task PublishReleaseAvailableAsync(AcquisitionRequest request, AcquisitionExecution result, CancellationToken cancellationToken) =>
        events.PublishAsync(
            JularrEvent.Create(
                JularrEventCategory.ReleaseAvailable,
                profileId: request.RequestedByProfileId,
                mediaType: AcquisitionAccessNames.Kind(request.Kind),
                subjectId: request.Id.ToString(),
                messageParams: new Dictionary<string, string> { ["title"] = request.Title },
                deepLink: result.ResultUrl,
                dedupKey: $"acquisition-request:{request.Id}:release-available",
                relatedOperationId: result.OperationId),
            cancellationToken);

    private async Task<AcquisitionRequest> RequireAsync(Guid id, CancellationToken cancellationToken) =>
        await store.GetAsync(id, cancellationToken)
        ?? throw new InvalidOperationException("The request no longer exists.");

    private void RequireRequestManager()
    {
        if (!account.Can(JularrPolicies.AdminMedia))
        {
            throw new AcquisitionAccessDeniedException("Only the owner or a media manager can decide requests.");
        }
    }
}
