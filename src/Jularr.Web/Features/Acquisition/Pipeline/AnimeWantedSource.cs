using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.Wanted;

namespace Jularr.Web.Features.Acquisition.Pipeline;

/// <summary>
/// The Anime part of the shared Wanted pass: what the monitoring pipeline needs done before the requests are followed (recovery after
/// startup, owner-requested searches, the periodic search for monitored anime). Anime facts stay in the pipeline; when and in which pass
/// it runs is the Wanted pass's decision, so Anime has no loop of its own.
/// </summary>
public sealed class AnimeWantedSource(AnimeAcquisitionScheduler scheduler) : IWantedSource
{
    public MediaAcquisitionKind Kind => MediaAcquisitionKind.Anime;

    public Task<int> PrepareAsync(DateTime nowUtc, CancellationToken cancellationToken) =>
        scheduler.AdvanceAsync(new DateTimeOffset(nowUtc, TimeSpan.Zero), cancellationToken);
}
