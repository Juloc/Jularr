using Jularr.Web.Data;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.Quality;
using Jularr.Web.Features.Acquisition.Selection;
using Jularr.Web.Features.Acquisition.Wanted;
using Jularr.Web.Features.MediaCore;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Web.Features.Audiobooks;

// The audio edition of a Book is upgradable while the best container it has is below what the audiobook profile wants (an MP3 where the profile wants an M4B); a
// container the profile does not rank is never replaced automatically.
public sealed class AudiobookUpgradeAssessor(AppDbContext db, QualityProfileStore profiles) : IUpgradeAssessor
{
    public MediaAcquisitionKind Kind => MediaAcquisitionKind.Audiobook;

    public WantedTargetKind TargetKind => WantedTargetKind.Edition;

    public async Task<IReadOnlyList<HeldTarget>> UpgradableAsync(Guid workId, IReadOnlyList<HeldTarget> held, CancellationToken cancellationToken)
    {
        var profile = await profiles.ResolveAsync(MediaAcquisitionKind.Audiobook, workId, cancellationToken);
        var formats = await (from link in db.WorkSourceLinks.AsNoTracking()
                             join file in db.AudiobookFiles.AsNoTracking() on link.SourceId equals file.AudiobookId
                             where link.WorkId == workId && link.SourceKind == WorkSourceKind.Audiobook
                             select file.Format).ToListAsync(cancellationToken);
        return UpgradePolicy.Assess(profile, UpgradePolicy.Best(profile, formats)).IsUpgradable ? held : [];
    }
}
