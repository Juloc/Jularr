using Jularr.Web.Features.Acquisition.Access;

namespace Jularr.Web.Features.Acquisition.ManualSearch;

/// <summary>What a manual grab got through before it stopped, so a caller that sees an exception knows whether a download exists.</summary>
public sealed class ManualGrabProgress
{
    public bool SubmitStarted { get; set; }

    public bool Accepted { get; set; }

    public Guid? OperationId { get; set; }
}

/// <summary>
/// The request-side lifecycle of one Manual Search grab, shared by the media types that grab a release for a waiting request (Music, Manga, Light
/// Novels): the request is claimed with one conditional status write so a request the scheduler grabbed for meanwhile is never grabbed twice, the
/// media type submits through the same path as automatic acquisition, and the outcome is recorded on the request like an automatic one. A failure
/// after the release may have reached the download client never hands the request back to the scheduler.
/// </summary>
public sealed class ManualGrabCoordinator(AcquisitionAccessStore requests, AcquisitionRequestService requestService, ILogger<ManualGrabCoordinator> logger)
{
    private const string SentMessage = "The release was sent to the download client. Follow it under Operations.";
    private const string InterruptedMessage = "Submitting the release was interrupted. Check Operations before choosing another release.";

    /// <param name="claimable">The statuses a request may be claimed from: the ones in which it waits for a release.</param>
    /// <param name="grab">Submits the chosen release for the claimed request; returns null when the release was tried meanwhile, so nothing was submitted.</param>
    public async Task<ManualGrabOutcome> GrabAsync(AcquisitionRequest request, IReadOnlyCollection<AcquisitionRequestStatus> claimable, Func<AcquisitionRequest, ManualGrabProgress, Task<AcquisitionExecution?>> grab, CancellationToken cancellationToken)
    {
        if (await requests.TryTransitionStatusAsync(request.Id, claimable, AcquisitionRequestStatus.Searching, null, null, cancellationToken) is not { } claimedFrom)
        {
            return new ManualGrabOutcome(ManualGrabStatus.NotSearchable, null, null);
        }

        var progress = new ManualGrabProgress();
        AcquisitionExecution? execution;
        try
        {
            var claimed = await requests.GetAsync(request.Id, cancellationToken) ?? request;
            execution = await grab(claimed, progress);
            if (execution is null)
            {
                await ReleaseClaimAsync(request.Id, claimedFrom);
                return new ManualGrabOutcome(ManualGrabStatus.AlreadySubmitted, null, null);
            }
        }
        catch (Exception exception) when (progress.SubmitStarted)
        {
            // The release may be at the download client already: never hand the request back to the scheduler.
            logger.LogError(exception, "Manual grab for request {RequestId} stopped after the release was submitted.", request.Id);
            var message = progress.Accepted ? SentMessage : InterruptedMessage;
            var recorded = await TryFinishClaimAsync(request.Id, progress.Accepted ? AcquisitionRequestStatus.Downloading : AcquisitionRequestStatus.Failed, message, progress.OperationId);
            if (exception is OperationCanceledException && !progress.Accepted)
            {
                throw;
            }

            return new ManualGrabOutcome(recorded && progress.Accepted ? ManualGrabStatus.Submitted : ManualGrabStatus.Unrecorded, message, null);
        }
        catch
        {
            await ReleaseClaimAsync(request.Id, claimedFrom);
            throw;
        }

        try
        {
            var applied = await requestService.ApplyManualExecutionAsync(request.Id, execution, cancellationToken);
            return new ManualGrabOutcome(execution.Status == AcquisitionRequestStatus.Downloading ? ManualGrabStatus.Submitted : ManualGrabStatus.ClientRejected, execution.Message, applied);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Manual grab for request {RequestId} was submitted but the request could not be updated.", request.Id);
            var accepted = execution.Status == AcquisitionRequestStatus.Downloading;
            await TryFinishClaimAsync(request.Id, accepted ? AcquisitionRequestStatus.Downloading : AcquisitionRequestStatus.Failed, accepted ? SentMessage : execution.Message, execution.OperationId);
            return new ManualGrabOutcome(ManualGrabStatus.Unrecorded, execution.Message, null);
        }
    }

    // Best effort and never throws: it runs while another failure is being handled and must not replace it.
    private async Task ReleaseClaimAsync(Guid requestId, AcquisitionStatusTransition claimedFrom)
    {
        try
        {
            await requests.TryTransitionStatusAsync(requestId, [AcquisitionRequestStatus.Searching], claimedFrom.PreviousStatus, claimedFrom.PreviousMessage, null, CancellationToken.None);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Could not give request {RequestId} back after a manual grab stopped.", requestId);
        }
    }

    private async Task<bool> TryFinishClaimAsync(Guid requestId, AcquisitionRequestStatus status, string? message, Guid? operationId)
    {
        try
        {
            return await requests.TryTransitionStatusAsync(requestId, [AcquisitionRequestStatus.Searching], status, message, operationId, CancellationToken.None) is not null;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Could not record the manual grab of request {RequestId}.", requestId);
            return false;
        }
    }
}
