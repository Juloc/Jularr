using Jularr.Web.Features.Auth;
using Jularr.Web.Features.Events;
using Jularr.Web.Features.Acquisition.Wanted;
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
    IInstanceModuleService? instanceModules = null,
    RequestWorkBinder? workBinder = null,
    RequestIntent? intent = null)
{
    /// <summary>Where a profile finds the state of its requests; decision notifications open it.</summary>
    public const string HistoryPath = "/Requests";

    /// <summary>The status surface of one request: its current state, saved settings and the actions that are allowed.</summary>
    public static string StatusPath(Guid requestId) => $"{HistoryPath}/{requestId:D}";

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

        // A new request is the durable decision at which a Book, Light Novel or Manga gets its canonical Work; an unresolvable identity stays unbound.
        if (workBinder is not null && draft.WorkId is null && RequestWorkBinder.Applies(draft.Kind))
        {
            draft = draft with { WorkId = await workBinder.ResolveAsync(draft.Kind, draft.Provider, draft.ExternalId, draft.Title, cancellationToken) };
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

        // A failed request that is approved again starts over: nobody is still waiting on the profile's playback intent from before it failed.
        if (request.Status == AcquisitionRequestStatus.Failed && request.Kind is MediaAcquisitionKind.Movie or MediaAcquisitionKind.Tv)
        {
            await store.PatchPayloadAsync(id, stored => VideoRequestPayload.Parse(stored) is { PlaybackMarkers: not null } current ? current.WithoutPlaybackIntent().Serialize() : stored, cancellationToken);
        }

        // Conditional on the status read above: a requester's cancel that landed meanwhile keeps the request cancelled.
        var decided = await store.TryTransitionStatusAsync(id, [request.Status], AcquisitionRequestStatus.Approved, null, null, null, false, account.ProfileId, cancellationToken);
        if (decided is null)
        {
            return await RequireAsync(id, cancellationToken);
        }

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

        // A note that equals the cancellation marker would read as a requester's cancel, so it is not kept.
        var reason = string.IsNullOrWhiteSpace(note) || note.Trim() == AcquisitionRequest.CancelledMessage ? null : note.Trim();
        var rejected = await store.TryTransitionStatusAsync(id, [AcquisitionRequestStatus.Pending], AcquisitionRequestStatus.Rejected, reason, null, null, false, account.ProfileId, cancellationToken);
        if (rejected is not null)
        {
            await PublishDecisionAsync(request, JularrEventCategory.RequestDenied, cancellationToken);
        }
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

    /// <summary>
    /// Cancels a request that still waits for approval; the requester, the owner and media managers may. The request is left as it is when
    /// somebody decided it first (an approver, or the requester in another tab), so cancelling twice is not an error and never undoes a
    /// decision. A cancelled request never touches the acquisition of its title: nothing was approved, so nothing runs.
    /// </summary>
    public async Task<RequestCancelOutcome> CancelAsync(Guid id, CancellationToken cancellationToken)
    {
        var request = await RequireAsync(id, cancellationToken);
        RequireRequesterOrManager(request);
        if (request.IsCancelled)
        {
            return RequestCancelOutcome.AlreadyCancelled;
        }

        if (!request.CanBeCancelled)
        {
            return RequestCancelOutcome.NoLongerPending;
        }

        // A manager cancelling for somebody else is recorded; a requester cancelling their own request decided nothing.
        var actor = request.RequestedByProfileId == account.ProfileId ? null : account.ProfileId;
        var moved = await store.TryTransitionStatusAsync(id, [AcquisitionRequestStatus.Pending], AcquisitionRequestStatus.Rejected, AcquisitionRequest.CancelledMessage, null, null, false, actor, cancellationToken);
        return moved is null ? RequestCancelOutcome.NoLongerPending : RequestCancelOutcome.Cancelled;
    }

    /// <summary>
    /// Runs a failed request again with the intent it was saved with, through the same execution path as an approval: the requester (or a
    /// manager) retries, nobody has to decide again. Compare-and-set on the failed status, so two retries start one run, and a title that
    /// meanwhile has another open request is not opened a second time.
    /// </summary>
    public async Task<RequestRetryOutcome> RetryAsync(Guid id, CancellationToken cancellationToken)
    {
        var request = await RequireAsync(id, cancellationToken);
        RequireRequesterOrManager(request);
        if (request.IsOpen && request.Status != AcquisitionRequestStatus.Pending)
        {
            return RequestRetryOutcome.AlreadyRetried;
        }

        if (!request.CanBeRetried || !(await GetCapabilitiesAsync(request.Kind, cancellationToken)).CanRequest)
        {
            return RequestRetryOutcome.NotRetryable;
        }

        // Nobody is still waiting on the profile's playback intent from before it failed, exactly as when a failed request is approved again.
        Func<string?, string?> dropPlaybackIntent = stored => VideoRequestPayload.Parse(stored) is { PlaybackMarkers: not null } current ? current.WithoutPlaybackIntent().Serialize() : stored;
        if (!await store.PatchPayloadAsync(id, dropPlaybackIntent, AcquisitionRequestStatus.Failed, AcquisitionRequestStatus.Approved, null, cancellationToken))
        {
            // Another retry (second tab, double submit) won the race: the request is running already.
            return (await RequireAsync(id, cancellationToken)).Status is AcquisitionRequestStatus.Failed or AcquisitionRequestStatus.Pending or AcquisitionRequestStatus.Rejected or AcquisitionRequestStatus.Completed
                ? RequestRetryOutcome.NotRetryable
                : RequestRetryOutcome.AlreadyRetried;
        }

        await ExecuteAsync(await RequireAsync(id, cancellationToken), cancellationToken);
        return RequestRetryOutcome.Retried;
    }

    /// <summary>
    /// Saves new settings of a request that still waits for approval: what a Tv request monitors (applied when it is approved), or the audio and subtitle language of
    /// an anime request (its scope and quality profile stay as the request was made). Both are validated by the caller against the title.
    /// The write only lands while the request is still pending, so an approval that wins the race keeps the intent it executed.
    /// </summary>
    public async Task<RequestEditOutcome> EditAsync(Guid id, VideoRequestScopeChoice? tvScope, AcquisitionRequestOptions? animeLanguages, CancellationToken cancellationToken)
    {
        var request = await RequireAsync(id, cancellationToken);
        RequireRequesterOrManager(request);
        var matchesKind = request.Kind == MediaAcquisitionKind.Tv ? tvScope is not null && animeLanguages is null : tvScope is null && animeLanguages is not null;
        if (!request.CanBeEdited || !matchesKind)
        {
            return RequestEditOutcome.NotEditable;
        }

        Func<string?, string?> patch = tvScope is { } scope
            ? stored => VideoRequestPayload.Parse(stored) is { } current ? (current with { Requested = scope, MonitoringRevision = current.MonitoringRevision + 1 }).Serialize() : stored
            : stored => (AcquisitionRequestOptions.FromPayload(stored) with { AudioLanguage = animeLanguages!.AudioLanguage, SubtitleLanguage = animeLanguages.SubtitleLanguage }).Validate().ToPayloadJson();
        return await store.PatchPayloadAsync(id, patch, AcquisitionRequestStatus.Pending, AcquisitionRequestStatus.Pending, null, cancellationToken)
            ? RequestEditOutcome.Saved
            : RequestEditOutcome.NotEditable;
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

        if (intent is not null)
        {
            // What the approved request asks for counts next to Monitoring, so switching Monitoring off never cancels it.
            if (workBinder is not null && RequestWorkBinder.Applies(request.Kind))
            {
                request = await workBinder.EnsureBoundAsync(request, cancellationToken);
            }

            await intent.RecordAsync(request, cancellationToken);
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
            result = TransientAcquisitionFailure.Describe(exception) is { } problem
                ? new AcquisitionExecution(AcquisitionRequestStatus.Approved, $"{problem} Trying again soon.")
                : new AcquisitionExecution(AcquisitionRequestStatus.Failed, exception.Message);
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
                deepLink: StatusPath(request.Id),
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

    private void RequireRequesterOrManager(AcquisitionRequest request)
    {
        if (request.RequestedByProfileId != account.ProfileId && !account.Can(JularrPolicies.AdminMedia))
        {
            throw new AcquisitionAccessDeniedException("Only the requester, the owner or a media manager can change a request.");
        }
    }
}
