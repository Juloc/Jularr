using Jularr.Web.Features.Auth;
using Jularr.Web.Features.Instance;

namespace Jularr.Web.Features.Acquisition.Access;

public enum RequestBulkAction
{
    Approve,
    Reject,
    Retry,
    Cancel,
    Complete,
    Delete,
    Profile
}

public enum RequestActionDisposition
{
    Succeeded,
    Skipped,
    Failed
}

public sealed record RequestActionResult(Guid Id, RequestActionDisposition Disposition, string Reason);

public sealed partial class AcquisitionRequestService
{
    public const int MaxBulkRequests = 100;

    public async Task<RequestProfileResult> ChangeProfileAsync(Guid id, string profileId, CancellationToken cancellationToken)
    {
        RequireRequestManager();
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        if (profileAssignment is null)
        {
            throw new InvalidOperationException("Request profile assignment must be configured.");
        }

        return await store.ChangeProfileAsync(id, async request =>
        {
            if (instanceModules is not null && (!await instanceModules.IsEnabledAsync(InstanceModule.Acquisition, cancellationToken)
                || !await instanceModules.IsEnabledAsync(AcquisitionInstanceModules.For(request.Kind), cancellationToken)))
            {
                return RequestProfileResult.StateChanged;
            }

            return await profileAssignment.AssignAsync(request, profileId, cancellationToken);
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<RequestActionResult>> BulkAsync(IReadOnlyCollection<Guid> ids, RequestBulkAction action, string? note, string? profileId, CancellationToken cancellationToken)
    {
        RequireRequestManager();
        if (!Enum.IsDefined(action) || ids.Count is < 1 or > MaxBulkRequests)
        {
            throw new ArgumentException("Select between one and 100 requests and a valid action.");
        }

        var results = new List<RequestActionResult>();
        foreach (var id in ids.Distinct())
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var request = await store.GetAsync(id, cancellationToken);
                if (request is null || instanceModules is not null && (!await instanceModules.IsEnabledAsync(InstanceModule.Acquisition, cancellationToken)
                    || !await instanceModules.IsEnabledAsync(AcquisitionInstanceModules.For(request.Kind), cancellationToken)))
                {
                    results.Add(new(id, RequestActionDisposition.Skipped, "unavailable"));
                    continue;
                }

                var valid = action switch
                {
                    RequestBulkAction.Approve or RequestBulkAction.Reject or RequestBulkAction.Cancel => request.Status == AcquisitionRequestStatus.Pending,
                    RequestBulkAction.Retry => request.Status is AcquisitionRequestStatus.Failed or AcquisitionRequestStatus.Approved,
                    RequestBulkAction.Complete => request.Status is AcquisitionRequestStatus.Pending or AcquisitionRequestStatus.Approved or AcquisitionRequestStatus.Failed,
                    RequestBulkAction.Profile => request.OperationId is null && request.Status is AcquisitionRequestStatus.Pending or AcquisitionRequestStatus.Approved or AcquisitionRequestStatus.Failed,
                    RequestBulkAction.Delete => true,
                    _ => false
                };
                if (!valid)
                {
                    results.Add(new(id, RequestActionDisposition.Skipped, "stateChanged"));
                    continue;
                }

                var disposition = RequestActionDisposition.Succeeded;
                switch (action)
                {
                    case RequestBulkAction.Approve:
                    case RequestBulkAction.Retry:
                        if (action == RequestBulkAction.Approve && !string.IsNullOrWhiteSpace(profileId) && await ChangeProfileAsync(id, profileId, cancellationToken) != RequestProfileResult.Assigned)
                        {
                            results.Add(new(id, RequestActionDisposition.Skipped, "profileUnavailable"));
                            continue;
                        }

                        var acquired = await ApproveAsync(id, cancellationToken);
                        if (acquired.Status == AcquisitionRequestStatus.Failed)
                        {
                            disposition = RequestActionDisposition.Failed;
                        }
                        else if (acquired.Status is AcquisitionRequestStatus.Pending or AcquisitionRequestStatus.Rejected)
                        {
                            disposition = RequestActionDisposition.Skipped;
                        }
                        break;
                    case RequestBulkAction.Reject:
                        await RejectAsync(id, note, cancellationToken);
                        disposition = (await store.GetAsync(id, cancellationToken))?.Status == AcquisitionRequestStatus.Rejected ? disposition : RequestActionDisposition.Skipped;
                        break;
                    case RequestBulkAction.Cancel:
                        disposition = await CancelAsync(id, cancellationToken) == RequestCancelOutcome.Cancelled ? disposition : RequestActionDisposition.Skipped;
                        break;
                    case RequestBulkAction.Complete:
                        await MarkCompletedAsync(id, cancellationToken);
                        disposition = (await store.GetAsync(id, cancellationToken))?.Status == AcquisitionRequestStatus.Completed ? disposition : RequestActionDisposition.Skipped;
                        break;
                    case RequestBulkAction.Delete:
                        disposition = await DeleteAsync(id, cancellationToken) == RequestDeleteOutcome.StateChangedOrActiveDownload ? RequestActionDisposition.Skipped : disposition;
                        break;
                    case RequestBulkAction.Profile:
                        disposition = string.IsNullOrWhiteSpace(profileId) || await ChangeProfileAsync(id, profileId, cancellationToken) != RequestProfileResult.Assigned ? RequestActionDisposition.Skipped : disposition;
                        break;
                }

                results.Add(new(id, disposition, disposition == RequestActionDisposition.Succeeded ? "done" : disposition == RequestActionDisposition.Failed ? "acquisitionFailed" : "stateChanged"));
            }
            catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or AcquisitionAccessDeniedException or System.Data.Common.DbException)
            {
                logger.LogWarning(exception, "Bulk request action {Action} failed for {RequestId}.", action, id);
                results.Add(new(id, RequestActionDisposition.Failed, "actionFailed"));
            }
        }

        return results;
    }
}
