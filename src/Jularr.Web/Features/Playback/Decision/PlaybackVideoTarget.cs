using Jularr.Web.Features.Progress;

namespace Jularr.Web.Features.Playback.Decision;

/// <summary>
/// Provider-independent playable video identity used by the shared Player.
/// Movies target a Work; Anime and TV target a WorkEpisode within a Work.
/// </summary>
public sealed record PlaybackVideoTarget(long WorkId, Guid? WorkEpisodeId)
{
    public bool IsEpisode => WorkEpisodeId.HasValue;

    public static PlaybackVideoTarget Movie(long workId) => new(workId, null);

    public static PlaybackVideoTarget Episode(long workId, Guid workEpisodeId) =>
        new(workId, workEpisodeId);

    public MediaProgressTarget ToProgressTarget() =>
        new(WorkId, WorkEpisodeId);
}
