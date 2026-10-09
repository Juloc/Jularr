using Jularr.Web.Data;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.Wanted;
using Jularr.Web.Features.MediaCore;
using Jularr.Web.Features.Monitoring;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Web.Features.Books;

public sealed record BookTargetState(bool Monitored, bool Wanted, bool Installed, AcquisitionRequestStatus? RequestStatus, string? RequestMessage);

public sealed record BookWorkAdminView(Guid WorkId, string Title, int? Year, BookTargetState Book, BookTargetState Audiobook, AcquisitionRequest? LatestAudiobookRequest);

/// <summary>
/// Where the Book and the audiobook of one Book Work stand. They are two targets with their own Monitoring decision, Wanted row, installed state and request:
/// nothing about the Book says anything about its audiobook.
/// </summary>
public sealed class BookWorkAdminQuery(AppDbContext db, MonitoringResolver monitoring, AcquisitionAccessStore requests)
{
    public async Task<BookWorkAdminView?> GetAsync(Guid workId, CancellationToken cancellationToken)
    {
        var work = await db.Works.AsNoTracking().Where(item => item.Id == workId && item.MediaType == WorkMediaType.Book).Select(item => new { item.CanonicalTitle, item.Year }).SingleOrDefaultAsync(cancellationToken);
        if (work is null)
        {
            return null;
        }

        var view = await monitoring.LoadAsync(workId, cancellationToken);
        var editionId = await db.WorkEditions.AsNoTracking()
            .Where(edition => edition.WorkId == workId && edition.Format == LegacyWorkBridge.AudiobookEditionFormat)
            .Select(edition => (Guid?)edition.Id)
            .FirstOrDefaultAsync(cancellationToken);
        var wanted = await db.WantedItems.AsNoTracking().Where(item => item.WorkId == workId && (item.TargetKind == WantedTargetKind.Work || item.TargetKind == WantedTargetKind.Edition)).Select(item => item.TargetKind).ToListAsync(cancellationToken);
        var bookInstalled = await (
                from link in db.WorkSourceLinks.AsNoTracking()
                join edition in db.BookEditions.AsNoTracking() on link.SourceId equals edition.Id
                join file in db.BookFiles.AsNoTracking() on edition.Id equals file.EditionId
                where link.WorkId == workId && link.SourceKind == WorkSourceKind.BookEdition
                select 1)
            .AnyAsync(cancellationToken);
        var audiobookInstalled = await (
                from link in db.WorkSourceLinks.AsNoTracking()
                join file in db.AudiobookFiles.AsNoTracking() on link.SourceId equals file.AudiobookId
                where link.WorkId == workId && link.SourceKind == WorkSourceKind.Audiobook
                select 1)
            .AnyAsync(cancellationToken);

        var identity = await IdentityAsync(workId, cancellationToken);
        var bookRequest = identity is var (provider, externalId) ? await requests.FindLatestAsync(MediaAcquisitionKind.Book, provider, externalId, cancellationToken) : null;
        var audiobookRequest = identity is var (audioProvider, audioExternalId) ? await requests.FindLatestAsync(MediaAcquisitionKind.Audiobook, audioProvider, audioExternalId, cancellationToken) : null;
        return new BookWorkAdminView(
            workId,
            work.CanonicalTitle,
            work.Year,
            new BookTargetState(view.IsWorkMonitored, wanted.Contains(WantedTargetKind.Work), bookInstalled, bookRequest?.Status, bookRequest?.StatusMessage),
            new BookTargetState(editionId is { } id && view.IsEditionMonitored(id), wanted.Contains(WantedTargetKind.Edition), audiobookInstalled, audiobookRequest?.Status, audiobookRequest?.StatusMessage),
            audiobookRequest);
    }

    /// <summary>The provider identity an audiobook request of the Work is made with: the Work's primary Book identity, null when it has none.</summary>
    public async Task<(string Provider, string ExternalId)?> IdentityAsync(Guid workId, CancellationToken cancellationToken)
    {
        var identity = await db.WorkExternalIdentities.AsNoTracking().Where(item => item.WorkId == workId && item.MediaType == WorkMediaType.Book && item.IsPrimary).Select(item => new { item.Provider, item.ExternalId }).FirstOrDefaultAsync(cancellationToken);
        return identity is null ? null : (identity.Provider, identity.ExternalId);
    }
}
