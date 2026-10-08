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

// How one media type tells which of the targets it holds its profile still wants better versions of; it reads the installed quality and the profile, stores nothing.
public interface IUpgradeAssessor
{
    MediaAcquisitionKind Kind { get; }

    Task<IReadOnlyList<HeldTarget>> UpgradableAsync(Guid workId, IReadOnlyList<HeldTarget> held, CancellationToken cancellationToken);
}

// The media types whose installed targets can be upgraded; an installed target of any other type simply leaves the Wanted queue.
public sealed class UpgradeAssessors(IEnumerable<IUpgradeAssessor> assessors)
{
    private readonly Dictionary<WorkMediaType, IUpgradeAssessor> byType = assessors.ToDictionary(assessor => WantedReconciler.WorkTypeOf(assessor.Kind));

    public IReadOnlyCollection<WorkMediaType> Types => byType.Keys;

    public IUpgradeAssessor? For(WorkMediaType type) => byType.GetValueOrDefault(type);
}

// Movie and TV: the installed quality of a Movie or of each episode against the profile's cutoff.
public sealed class VideoUpgradeAssessor(MediaAcquisitionKind kind, InstalledVideoVersions installed, QualityProfileStore profiles) : IUpgradeAssessor
{
    public MediaAcquisitionKind Kind => kind;

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
