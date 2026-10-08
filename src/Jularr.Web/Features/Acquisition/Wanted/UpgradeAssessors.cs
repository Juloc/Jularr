using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.Quality;
using Jularr.Web.Features.Acquisition.Selection;
using Jularr.Web.Features.MediaCore;

namespace Jularr.Web.Features.Acquisition.Wanted;

// A target that Monitoring or a request wants and the library already holds, as a WantedItems row names it.
public sealed class HeldTarget
{
    public short TargetKind { get; init; }

    public Guid TargetId { get; init; }
}

// How one acquisition kind tells which of the targets it holds its profile still wants better versions of; it reads the installed quality and the profile, stores nothing.
// Kinds can share a Work type (Book and Audiobook), so an assessor also names the kind of target it judges and only ever sees those.
public interface IUpgradeAssessor
{
    MediaAcquisitionKind Kind { get; }

    WantedTargetKind TargetKind { get; }

    Task<IReadOnlyList<HeldTarget>> UpgradableAsync(Guid workId, IReadOnlyList<HeldTarget> held, CancellationToken cancellationToken);
}

// The kinds whose installed targets can be upgraded; an installed target nobody assesses simply leaves the Wanted queue.
public sealed class UpgradeAssessors(IEnumerable<IUpgradeAssessor> assessors)
{
    private readonly IUpgradeAssessor[] all = [.. assessors];

    public IReadOnlyCollection<WorkMediaType> Types => [.. all.Select(assessor => WantedReconciler.WorkTypeOf(assessor.Kind)).Distinct()];

    // The held targets of a Work that its profiles still want better versions of: every assessor of the Work's type judges its own kind of target.
    public async Task<IReadOnlyList<HeldTarget>> UpgradableAsync(WorkMediaType type, Guid workId, IReadOnlyList<HeldTarget> held, CancellationToken cancellationToken)
    {
        var upgradable = new List<HeldTarget>();
        foreach (var assessor in all.Where(assessor => WantedReconciler.WorkTypeOf(assessor.Kind) == type))
        {
            var own = held.Where(target => target.TargetKind == (short)assessor.TargetKind).ToArray();
            if (own.Length > 0)
            {
                upgradable.AddRange(await assessor.UpgradableAsync(workId, own, cancellationToken));
            }
        }

        return upgradable;
    }
}

// Movie and TV: the installed quality of a Movie or of each episode against the profile's cutoff.
public sealed class VideoUpgradeAssessor(MediaAcquisitionKind kind, InstalledVideoVersions installed, QualityProfileStore profiles) : IUpgradeAssessor
{
    public MediaAcquisitionKind Kind => kind;

    public WantedTargetKind TargetKind => kind == MediaAcquisitionKind.Movie ? WantedTargetKind.Work : WantedTargetKind.Episode;

    public async Task<IReadOnlyList<HeldTarget>> UpgradableAsync(Guid workId, IReadOnlyList<HeldTarget> held, CancellationToken cancellationToken)
    {
        var profile = await profiles.ResolveAsync(kind, workId, cancellationToken);
        if (kind == MediaAcquisitionKind.Movie)
        {
            return UpgradePolicy.Assess(profile, await installed.BestMovieQualityAsync(workId, profile, cancellationToken)).IsUpgradable ? held : [];
        }

        var qualities = await installed.BestQualityByEpisodeAsync(kind, workId, profile, cancellationToken);
        return [.. held.Where(target => qualities.TryGetValue(target.TargetId, out var quality) && UpgradePolicy.Assess(profile, quality).IsUpgradable)];
    }
}
