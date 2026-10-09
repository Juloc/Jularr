using Jularr.Web.Data;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.MediaCore;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Web.Features.Acquisition.Wanted;

// The request that would carry one wanted Work, and who it is made for.
public sealed record WantedRequestDraft(AcquisitionRequestDraft Draft, string Requester);

// What is media-specific about opening a request for a Work: the provider identity and payload its executor expects.
public interface IWantedRequestDrafter
{
    MediaAcquisitionKind Kind { get; }

    Task<WantedRequestDraft?> DraftAsync(Guid workId, CancellationToken cancellationToken);
}

// The provider identity a request is made with when its executor needs nothing more than the title.
public sealed class IdentityRequestDrafter(MediaAcquisitionKind kind, AppDbContext db) : IWantedRequestDrafter
{
    public MediaAcquisitionKind Kind => kind;

    public async Task<WantedRequestDraft?> DraftAsync(Guid workId, CancellationToken cancellationToken)
    {
        var type = WantedReconciler.WorkTypeOf(kind);
        var identity = await (
                from i in db.WorkExternalIdentities.AsNoTracking()
                join w in db.Works.AsNoTracking() on i.WorkId equals w.Id
                where i.WorkId == workId && i.MediaType == type && i.IsPrimary
                select new { i.Provider, i.ExternalId, w.CanonicalTitle })
            .FirstOrDefaultAsync(cancellationToken);
        return identity is not null && RequestWorkBinder.IsTrustworthy(kind, identity.Provider, identity.ExternalId)
            ? new WantedRequestDraft(new AcquisitionRequestDraft(kind, identity.Provider, identity.ExternalId, identity.CanonicalTitle, null, null) { WorkId = workId }, "owner")
            : null;
    }
}

// Opens the request that carries each wanted Work of one media type. A title that was requested and completed before is requested again; one whose latest
// request is open, failed or rejected stays with what the lifecycle or the owner decided.
public sealed class WantedRequestSource(MediaAcquisitionKind kind, WantedReconciler wanted, AcquisitionAccessStore requests, IWantedRequestDrafter drafter) : IWantedSource
{
    private const int PageSize = 100;

    public MediaAcquisitionKind Kind => kind;

    public async Task<int> PrepareAsync(DateTime nowUtc, CancellationToken cancellationToken)
    {
        await wanted.ReconcileAllIfDueAsync(cancellationToken);
        var created = 0;
        var after = Guid.Empty;
        while (created < WantedAcquisitionService.MaxRequestsPerKindPerPass)
        {
            var works = await wanted.WorksWithoutOpenRequestAsync(kind, after, PageSize, cancellationToken);
            if (works.Count == 0)
            {
                break;
            }

            after = works[^1];
            foreach (var workId in works)
            {
                if (created >= WantedAcquisitionService.MaxRequestsPerKindPerPass)
                {
                    break;
                }

                if (await drafter.DraftAsync(workId, cancellationToken) is not { } request
                    || await requests.FindLatestAsync(kind, request.Draft.Provider, request.Draft.ExternalId, cancellationToken) is { Status: not AcquisitionRequestStatus.Completed })
                {
                    continue;
                }

                try
                {
                    await requests.CreateAsync(request.Draft, request.Requester, AcquisitionRequestStatus.Approved, request.Requester, cancellationToken);
                    created++;
                }
                catch (OpenRequestExistsException)
                {
                    // Another pass or the owner requested the title in the same moment; the open request is the Wanted state.
                }
            }
        }

        return created;
    }
}
