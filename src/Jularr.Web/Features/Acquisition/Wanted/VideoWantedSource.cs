using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.Monitoring;

namespace Jularr.Web.Features.Acquisition.Wanted;

// Gives every Movie or Series that has wanted targets the open request that carries its search, including a title that was requested and completed before.
public sealed class VideoWantedSource(MediaAcquisitionKind kind, WantedReconciler wanted, VideoMonitoringService monitoring) : IWantedSource
{
    private const int PageSize = 100;

    public MediaAcquisitionKind Kind => kind;

    public async Task<int> PrepareAsync(DateTime nowUtc, CancellationToken cancellationToken)
    {
        await wanted.ReconcileAsync(null, cancellationToken);
        var opened = 0;
        var after = Guid.Empty;
        while (opened < WantedAcquisitionService.MaxRequestsPerKindPerPass)
        {
            var works = await wanted.WorksWithoutOpenRequestAsync(kind, after, PageSize, cancellationToken);
            if (works.Count == 0)
            {
                break;
            }

            after = works[^1];
            foreach (var workId in works)
            {
                if (await monitoring.ReconcileAsync(workId, kind, wake: false, cancellationToken) == VideoMonitoringOutcome.Saved && ++opened >= WantedAcquisitionService.MaxRequestsPerKindPerPass)
                {
                    break;
                }
            }
        }

        return opened;
    }
}
