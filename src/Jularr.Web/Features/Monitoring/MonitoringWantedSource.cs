using Jularr.Web.Data;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.Wanted;
using Jularr.Web.Features.MediaCore;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Web.Features.Monitoring;

/// <summary>
/// The Wanted side of relation monitoring for Movies, Series, Books, Light Novels and Manga: a Work that is monitored (through a person, a studio, a
/// collection or its own decision) and was never requested gets one approved request on the shared lifecycle. The request is the only Wanted state, so a
/// Work reached by several sources still has one request (the store's unique open-request index). Nothing is searched here, and a Work that was requested
/// before keeps the request it has.
/// </summary>
public sealed class MonitoringWantedSource(MediaAcquisitionKind kind, AppDbContext db, AcquisitionAccessStore requests, MonitoringResolver monitoring) : IWantedSource
{
    public const int MaxRequestsPerPass = 25;

    private const int PageSize = 500;

    public MediaAcquisitionKind Kind => kind;

    public static WorkMediaType WorkTypeOf(MediaAcquisitionKind kind) => kind switch
    {
        MediaAcquisitionKind.Movie => WorkMediaType.Movie,
        MediaAcquisitionKind.Tv => WorkMediaType.Series,
        MediaAcquisitionKind.Book => WorkMediaType.Book,
        MediaAcquisitionKind.LightNovel => WorkMediaType.LightNovel,
        MediaAcquisitionKind.Manga => WorkMediaType.Manga,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "This media type is not requested through Monitoring.")
    };

    public async Task<int> PrepareAsync(DateTime nowUtc, CancellationToken cancellationToken)
    {
        var type = WorkTypeOf(kind);
        var kindName = AcquisitionAccessNames.Kind(kind);
        var created = 0;
        var after = Guid.Empty;
        while (created < MaxRequestsPerPass)
        {
            // The monitored Works are read in id order, a page at a time, so Works that already have a request never hide the ones that have none.
            var page = await monitoring.MonitoredWorkIdsAsync(type, after, PageSize, cancellationToken);
            if (page.Count == 0)
            {
                break;
            }

            after = page[^1];
            var unrequested = await db.Database
                .SqlQuery<Candidate>(
                    $"""
                    SELECT i."WorkId", i."Provider", i."ExternalId", w."CanonicalTitle" AS "Title"
                    FROM "WorkExternalIdentities" i JOIN "Works" w ON w."Id" = i."WorkId"
                    WHERE i."WorkId" = ANY({page}) AND i."MediaType" = {(int)type} AND i."IsPrimary"
                      AND NOT EXISTS (SELECT 1 FROM "AcquisitionRequests" r WHERE r."Kind" = {kindName} AND r."Provider" = i."Provider" AND r."ExternalId" = i."ExternalId")
                    ORDER BY i."WorkId"
                    """)
                .ToListAsync(cancellationToken);
            foreach (var candidate in unrequested.Where(candidate => CanBeRequested(candidate.Provider, candidate.ExternalId)))
            {
                if (created >= MaxRequestsPerPass)
                {
                    break;
                }

                try
                {
                    await requests.CreateAsync(new AcquisitionRequestDraft(kind, candidate.Provider, candidate.ExternalId, candidate.Title, null, null) { WorkId = candidate.WorkId }, "owner", AcquisitionRequestStatus.Approved, "owner", cancellationToken);
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

    /// <summary>A Movie or Series is requested by its TMDB, TVDB or IMDb id, the others by the id their request flow uses.</summary>
    private bool CanBeRequested(string provider, string externalId) => kind is MediaAcquisitionKind.Movie or MediaAcquisitionKind.Tv
        ? provider is "tmdb" or "tvdb" or "imdb"
        : RequestWorkBinder.IsTrustworthy(kind, provider, externalId);

    private sealed record Candidate(Guid WorkId, string Provider, string ExternalId, string Title);
}
