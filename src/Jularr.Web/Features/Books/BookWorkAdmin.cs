using Jularr.Web.Data;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.Quality;
using Jularr.Web.Features.Acquisition.Selection;
using Jularr.Web.Features.Acquisition.Wanted;
using Jularr.Web.Features.MediaCore;
using Jularr.Web.Features.Monitoring;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Web.Features.Books;

public sealed record BookTargetState(bool Monitored, bool Wanted, bool Installed, AcquisitionRequestStatus? RequestStatus, string? RequestMessage);

/// <summary>One file of an edition with where it is stored; <see cref="Exists"/> is false when the stored path cannot be read now (an unmounted share, a deleted file).</summary>
public sealed record BookFileAdmin(Guid Id, string Name, string Format, long SizeBytes, string? Location, bool Exists);

public sealed record BookEditionAdmin(Guid Id, string? Title, string Language, string? Isbn, string? Publisher, string? Provider, IReadOnlyList<BookFileAdmin> Files);

public sealed record BookIdentityAdmin(string Provider, string ExternalId, bool IsPrimary);

/// <summary>What the Admin Book page shows beyond the two monitoring targets: the book, its provider identities, editions and files, the profile and where the upgrade stands.</summary>
public sealed record BookWorkAdminDetail(
    string? Author,
    Guid? LibraryBookId,
    Guid? FirstChapterId,
    bool HasCover,
    IReadOnlyList<BookIdentityAdmin> Identities,
    IReadOnlyList<BookEditionAdmin> Editions,
    string ProfileId,
    string ProfileName,
    string? AssignedProfileId,
    IReadOnlyList<(string Id, string Name)> Profiles,
    string? InstalledQuality,
    bool UpgradeWanted,
    bool BelowCutoff,
    AcquisitionRequest? BookRequest)
{
    public static BookWorkAdminDetail Empty { get; } = new(null, null, null, false, [], [], "", "", null, [], null, false, false, null);

    public IReadOnlyList<string> Formats => [.. Editions.SelectMany(edition => edition.Files).Select(file => file.Format).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)];

    public IReadOnlyList<string> Languages => [.. Editions.Select(edition => edition.Language).Where(language => language is not ("und" or "")).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)];

    public long SizeBytes => Editions.SelectMany(edition => edition.Files).Sum(file => file.SizeBytes);

    public bool HasUnreadableFile => Editions.SelectMany(edition => edition.Files).Any(file => !file.Exists);
}

public sealed record BookWorkAdminView(long WorkId, string Title, int? Year, BookTargetState Book, BookTargetState Audiobook, AcquisitionRequest? LatestAudiobookRequest)
{
    public BookWorkAdminDetail Detail { get; init; } = BookWorkAdminDetail.Empty;
}

/// <summary>
/// Where the Book and the audiobook of one Book Work stand. They are two targets with their own Monitoring decision, Wanted row, installed state and request:
/// nothing about the Book says anything about its audiobook.
/// </summary>
public sealed class BookWorkAdminQuery(AppDbContext db, MonitoringResolver monitoring, AcquisitionAccessStore requests, QualityProfileStore? profiles = null)
{
    public async Task<BookWorkAdminView?> GetAsync(long workId, CancellationToken cancellationToken)
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
            audiobookRequest)
        {
            Detail = await DetailAsync(workId, view.IsWorkMonitored, bookRequest, cancellationToken)
        };
    }

    /// <summary>
    /// The provider identity a request of the Work is made with: its catalog id, which is what requests, the direct sources and the request lookup use,
    /// else the primary identity; null when the Work has none. An import records its own source key as a primary identity too, which is no request identity.
    /// </summary>
    public async Task<(string Provider, string ExternalId)?> IdentityAsync(long workId, CancellationToken cancellationToken)
    {
        var identity = await db.WorkExternalIdentities.AsNoTracking()
            .Where(item => item.WorkId == workId && item.MediaType == WorkMediaType.Book)
            .OrderByDescending(item => item.Provider == BookCatalogService.CatalogRequestProvider)
            .ThenByDescending(item => item.IsPrimary)
            .Select(item => new { item.Provider, item.ExternalId })
            .FirstOrDefaultAsync(cancellationToken);
        return identity is null ? null : (identity.Provider, identity.ExternalId);
    }

    /// <summary>Whether a profile can take a book at all: it allows an EPUB or a PDF. A profile for video or audio qualities would reject every release of a book.</summary>
    public static bool ServesBooks(QualityProfile profile) =>
        profile.AllowedQualities.Any(quality => quality.Equals("EPUB", StringComparison.OrdinalIgnoreCase) || quality.Equals("PDF", StringComparison.OrdinalIgnoreCase));

    private async Task<BookWorkAdminDetail> DetailAsync(long workId, bool monitored, AcquisitionRequest? bookRequest, CancellationToken cancellationToken)
    {
        var identities = await db.WorkExternalIdentities.AsNoTracking()
            // The source keys an import records for its own bookkeeping are not identities of the book; its catalog ids and ISBNs are.
            .Where(item => item.WorkId == workId && item.MediaType == WorkMediaType.Book && (item.Provider == BookCatalogService.CatalogRequestProvider || item.Provider == "isbn"))
            .OrderByDescending(item => item.Provider == BookCatalogService.CatalogRequestProvider)
            .ThenByDescending(item => item.IsPrimary)
            .ThenBy(item => item.Provider)
            .Select(item => new BookIdentityAdmin(item.Provider, item.ExternalId, item.IsPrimary))
            .ToListAsync(cancellationToken);
        var novel = await (
                from link in db.WorkSourceLinks.AsNoTracking()
                join entry in db.NovelWorks.AsNoTracking() on link.SourceId equals entry.Id
                where link.WorkId == workId && link.SourceKind == WorkSourceKind.NovelWork
                orderby entry.ImportedAt
                select new { entry.Id, entry.Author, entry.CoverImageUrl })
            .FirstOrDefaultAsync(cancellationToken);
        var firstChapter = novel is null ? null : await db.NovelChapters.AsNoTracking().Where(chapter => chapter.WorkId == novel.Id).OrderBy(chapter => chapter.Number).Select(chapter => (Guid?)chapter.Id).FirstOrDefaultAsync(cancellationToken);
        var rows = await (
                from link in db.WorkSourceLinks.AsNoTracking()
                join edition in db.BookEditions.AsNoTracking() on link.SourceId equals edition.Id
                where link.WorkId == workId && link.SourceKind == WorkSourceKind.BookEdition
                orderby edition.IsPrimary descending, edition.CreatedAt
                select edition)
            .ToListAsync(cancellationToken);
        var files = await db.BookFiles.AsNoTracking().Where(file => rows.Select(edition => edition.Id).Contains(file.EditionId)).OrderBy(file => file.FileName).ToListAsync(cancellationToken);
        var editions = rows
            .Select(edition => new BookEditionAdmin(
                edition.Id,
                edition.Title,
                edition.Language,
                edition.Isbn13 ?? edition.Isbn10,
                edition.Publisher,
                edition.SourceProvider,
                [.. files.Where(file => file.EditionId == edition.Id).Select(file => new BookFileAdmin(file.Id, file.FileName, file.Format, file.SizeBytes, file.StoragePath, !string.IsNullOrEmpty(file.StoragePath) && File.Exists(file.StoragePath)))]))
            .ToList();

        var profile = profiles is null ? null : await profiles.ResolveAsync(MediaAcquisitionKind.Book, workId, cancellationToken);
        var state = profiles is null ? null : await profiles.LoadAsync(cancellationToken);
        var installed = profile is null ? null : await BookInstalledQuality.BestAsync(db, profile, workId, cancellationToken);
        var belowCutoff = profile is not null && installed is not null && UpgradePolicy.Assess(profile, installed).IsUpgradable;
        return new BookWorkAdminDetail(
            novel?.Author,
            novel?.Id,
            firstChapter,
            novel is { CoverImageUrl: { Length: > 0 } },
            identities,
            editions,
            profile?.Id ?? "",
            profile?.Name ?? "",
            state?.WorkAssignments.GetValueOrDefault(workId.ToString("D")),
            [.. (state?.Profiles ?? []).Where(ServesBooks).Select(item => (item.Id, item.Name))],
            installed,
            monitored && belowCutoff,
            belowCutoff,
            bookRequest);
    }
}
