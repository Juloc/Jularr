using Jularr.Web.Data;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.Quality;
using Jularr.Web.Features.Acquisition.Selection;
using Jularr.Web.Features.Acquisition.Wanted;
using Jularr.Web.Features.MediaCore;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Web.Features.Books;

public static class BookInstalledQuality
{
    // The best format among the files of the Work's editions, as the profile's quality order names it; null when the Work has no file.
    public static async Task<string?> BestAsync(AppDbContext db, QualityProfile profile, Guid workId, CancellationToken cancellationToken) =>
        UpgradePolicy.Best(
            profile,
            await (from link in db.WorkSourceLinks.AsNoTracking()
                   join file in db.BookFiles.AsNoTracking() on link.SourceId equals file.EditionId
                   where link.WorkId == workId && link.SourceKind == WorkSourceKind.BookEdition
                   select file.Format).ToListAsync(cancellationToken));
}

// A Book is upgradable while the best format it has is below what its profile wants (a PDF where the profile wants an EPUB).
public sealed class BookUpgradeAssessor(AppDbContext db, QualityProfileStore profiles) : IUpgradeAssessor
{
    public MediaAcquisitionKind Kind => MediaAcquisitionKind.Book;

    public WantedTargetKind TargetKind => WantedTargetKind.Work;

    public async Task<IReadOnlyList<HeldTarget>> UpgradableAsync(Guid workId, IReadOnlyList<HeldTarget> held, CancellationToken cancellationToken)
    {
        var profile = await profiles.ResolveAsync(MediaAcquisitionKind.Book, workId, cancellationToken);
        return UpgradePolicy.Assess(profile, await BookInstalledQuality.BestAsync(db, profile, workId, cancellationToken)).IsUpgradable ? held : [];
    }
}
