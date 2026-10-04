using Jularr.Web.Features.Progress;

namespace Jularr.Web.Features.Playback.Decision;

/// <summary>
/// Provider-independent playable video identity used by the shared Player.
/// Movies target a Work; Anime and TV target a WorkEpisode within a Work.
/// </summary>
public sealed record PlaybackVideoTarget(Guid WorkId, Guid? WorkEpisodeId)
{
    public Guid IdentityId => WorkEpisodeId ?? WorkId;

    public bool IsEpisode => WorkEpisodeId.HasValue;

    public static PlaybackVideoTarget Movie(Guid workId) => new(workId, null);

    public static PlaybackVideoTarget Episode(Guid workId, Guid workEpisodeId) =>
        new(workId, workEpisodeId);

    public MediaProgressTarget ToProgressTarget() =>
        new(WorkId, WorkEpisodeId);
}
