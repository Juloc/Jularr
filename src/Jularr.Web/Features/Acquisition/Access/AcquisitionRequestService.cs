using Jularr.Web.Features.Auth;
using Jularr.Web.Features.Events;
using Jularr.Web.Features.Instance;

namespace Jularr.Web.Features.Acquisition.Access;

/// <summary>
/// The one path for adding a title from search, for every media type. What the profile may do comes
/// from the capability matrix (#436): a profile with <see cref="MediaCapability.Instant"/> adds right
/// away, one with <see cref="MediaCapability.Request"/> creates a request, and anything below cannot
/// add. A request waits for the owner unless an auto-approval rule approves it; an approved request
/// goes to the media type's executor.
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

    /// <summary>Adds (or requests) a title. Returns the open request for it, new or existing.</summary>
    public async Task<AcquisitionRequest> SubmitAsync(
        AcquisitionRequestDraft draft,
        CancellationToken cancellationToken)
    {
        var capabilities = await GetCapabilitiesAsync(draft.Kind, cancellationToken);
        if (!capabilities.CanAdd)
        {
            throw new AcquisitionAccessDeniedException("You may not request or add this kind of media.");
        }

        draft = await PrepareDraftAsync(draft, cancellationToken);
        if (await store.FindOpenAsync(draft.Kind, draft.Provider, draft.ExternalId, cancellationToken) is { } open)
        {
            return open;
        }

        if (capabilities.AddCreatesRequest)
        {
            var decision = await EvaluateAutoApprovalAsync(draft.Kind, cancellationToken);
            if (decision.Rule is not { } rule)
            {
                return await store.CreateAsync(draft, account.ProfileId, AcquisitionRequestStatus.Pending, null, cancellationToken);
            }

            var autoApproved = await store.CreateAsync(
                draft,
                account.ProfileId,
                AcquisitionRequestStatus.Approved,
                AcquisitionAutoApproval.DecidedBy(rule.Id),
                cancellationToken);
            return await ExecuteAsync(autoApproved, cancellationToken);
        }

        var approved = await store.CreateAsync(
            draft,
            account.ProfileId,
            AcquisitionRequestStatus.Approved,
            account.ProfileId,
            cancellationToken);
        return await ExecuteAsync(approved, cancellationToken);
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
    /// Validates the richer structured-video options of a draft and moves them into the payload. Anime and
    /// TV share this contract; other media types use the payload for their own state. A requester without the
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

        if (draft.Kind is not (MediaAcquisitionKind.Anime or MediaAcquisitionKind.Tv) || draft.PayloadJson is not null)
        {
            throw new ArgumentException("Request options are only available for Anime and TV titles.", nameof(draft));
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

        await store.UpdateStatusAsync(request.Id, AcquisitionRequestStatus.Searching, null, null, null, null, cancellationToken);
        AcquisitionExecution result;
        try
        {
            result = await executor.ExecuteAsync(request, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(exception, "Acquisition request {RequestId} ({Kind}) failed.", request.Id, request.Kind);
            result = new AcquisitionExecution(AcquisitionRequestStatus.Failed, exception.Message);
        }

        await store.UpdateStatusAsync(
            request.Id,
            result.Status,
            result.Message,
            result.OperationId,
            result.ResultUrl,
            null,
            cancellationToken);

        // Every media kind's executor reports Downloading the moment it finds and grabs an
        // accepted release, so this one spot covers #579's "ReleaseAvailable when a wanted
        // release is found" for Anime, Manga, Light Novels and Books alike.
        if (result.Status == AcquisitionRequestStatus.Downloading)
        {
            await PublishReleaseAvailableAsync(request, result, cancellationToken);
        }

        return await RequireAsync(request.Id, cancellationToken);
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
