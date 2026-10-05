using Jularr.Web.Features.Localization;
using Jularr.Web.Features.Playback.Decision;

namespace Jularr.Web.Features.Playback;

/// <summary>
/// Everything the web video player stage renders: the routes it talks to, the resume and storage state and the
/// heading. An Anime episode is addressed by its legacy episode (<see cref="LegacyEpisodeId"/>); a Movie or Series
/// episode by its canonical <see cref="Target"/> (Work, or WorkEpisode within a Work). The player scripts follow the
/// routes given here and send the canonical target with every plan and progress request when there is one.
/// <see cref="OffersAutoplay"/> is false where there is no next item to continue with, such as a Movie.
/// </summary>
public sealed record VideoPlayerStageView(
    UiTextBundle Ui,
    Guid? LegacyEpisodeId,
    PlaybackVideoTarget? Target,
    string PlanUrl,
    string PlayerBootstrapUrl,
    string ProgressUrl,
    string StorageAvailabilityUrl,
    string StorageState,
    string StorageHealth,
    double? DurationSeconds,
    double ResumeSeconds,
    string? NextUrl,
    bool AutoplayNext,
    bool OffersAutoplay,
    string FinishedKey,
    string Title,
    string? EpisodeLine,
    string? NativeTitle,
    string BackUrl,
    string BackLabelKey,
    string SubtitleCuesUrlTemplate,
    PlayerControls? Controls,
    IReadOnlyList<PlaybackCue> Cues,
    bool ShowPlayerTools,
    string? PreviousUrl = null,
    string? PreviousLabel = null);
