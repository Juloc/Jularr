using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Localization;

namespace Jularr.Web.Features.Discovery;

/// <summary>
/// What the Discover Request dialog posts: the identity of the card (re-validated on the server) and the
/// settings of the one dialog. Scope, season and episode choices exist for series only, languages for anime only.
/// </summary>
public sealed class DiscoverRequestForm
{
    public string? Category { get; set; }
    public string? Provider { get; set; }
    public string? ExternalId { get; set; }
    public string? Title { get; set; }
    public string? Subtitle { get; set; }
    public string? Author { get; set; }
    public string? CoverImageUrl { get; set; }

    /// <summary><c>all</c>, <c>future</c> or <c>custom</c>; empty for kinds without a scope.</summary>
    public string? Scope { get; set; }

    public Guid[] SeasonIds { get; set; } = [];
    public Guid[] EpisodeIds { get; set; } = [];
    public bool MonitorFuture { get; set; }
    public string? Audio { get; set; }
    public string? Subtitles { get; set; }

    public bool HasScope => !string.IsNullOrEmpty(Scope) || SeasonIds.Length > 0 || EpisodeIds.Length > 0 || MonitorFuture;
    public bool HasLanguage => !string.IsNullOrWhiteSpace(Audio) || !string.IsNullOrWhiteSpace(Subtitles);
}

/// <summary>The settings the Request dialog shows for one resolved title: only the groups that apply to its media kind. <see cref="Existing"/> is the open request that makes a new one unnecessary.</summary>
public sealed record DiscoverRequestSettingsView(
    UiTextBundle Ui,
    MediaAcquisitionKind Kind,
    AcquisitionRequest? Existing,
    IReadOnlyList<VideoRequestSeason> Seasons,
    string? DefaultAudio,
    string? DefaultSubtitles)
{
    public bool OffersScope => Existing is null && Kind == MediaAcquisitionKind.Tv;

    public bool OffersLanguage => Existing is null && Kind == MediaAcquisitionKind.Anime;
}

public static class DiscoverRequestSummary
{
    /// <summary>The exact series scope that was requested, in the words of the dialog's Scope control.</summary>
    public static string Scope(VideoRequestPayload payload, UiTextBundle ui)
    {
        if (payload.Scope != VideoRequestScope.Custom)
        {
            return ui[payload.Scope == VideoRequestScope.FutureOnly ? "discover.request.scope.future" : "discover.request.scope.all"];
        }

        List<string> parts = [ui["discover.request.scope.custom"]];
        if (payload.SelectedSeasonIds is { Length: > 0 } seasons)
        {
            parts.Add(ui.Format("discover.request.summary.seasons", ("count", seasons.Length)));
        }

        if (payload.SelectedEpisodeIds.Length > 0)
        {
            parts.Add(ui.Format("discover.request.summary.episodes", ("count", payload.SelectedEpisodeIds.Length)));
        }

        if (payload.MonitorFuture)
        {
            parts.Add(ui["discover.request.summary.future"]);
        }

        return string.Join(" · ", parts);
    }
}

/// <summary>The success state of the Request dialog: what was requested and where the request stands.</summary>
public sealed record DiscoverRequestResultView(
    UiTextBundle Ui,
    AcquisitionRequest Request,
    bool AlreadyRequested,
    IReadOnlyList<string> Summary,
    int? Progress)
{
    public bool IsDone => Request.Status is AcquisitionRequestStatus.Completed or AcquisitionRequestStatus.Rejected or AcquisitionRequestStatus.Failed;

    /// <summary>The state in consumer words: an auto-approving policy only ever shows up as the result, never as another action.</summary>
    public string StateLabel => Request.Status switch
    {
        AcquisitionRequestStatus.Pending => Ui["discover.request.state.pending"],
        AcquisitionRequestStatus.Approved when Request.WasAutoApproved => Ui["requests.autoApproved"],
        AcquisitionRequestStatus.Approved => Ui[ConsumerAcquisitionLabels.StatusKey(Request.Status)],
        AcquisitionRequestStatus.Searching => Ui["discover.request.state.searching"],
        AcquisitionRequestStatus.Importing => Ui["discover.request.state.importing"],
        _ => Ui[ConsumerAcquisitionLabels.StatusKey(Request.Status)]
    };

    /// <summary>One sentence in consumer words for the current state; a rejection has none because its reason is not necessarily user-visible.</summary>
    public string? StateHint => Request.Status switch
    {
        AcquisitionRequestStatus.Pending => Ui["discover.request.hint.pending"],
        AcquisitionRequestStatus.Approved => Ui["discover.request.hint.approved"],
        AcquisitionRequestStatus.Searching => Ui["discover.request.hint.searching"],
        AcquisitionRequestStatus.Downloading => Ui["discover.request.hint.downloading"],
        AcquisitionRequestStatus.Importing => Ui["discover.request.hint.importing"],
        AcquisitionRequestStatus.Failed => Ui["discover.request.hint.failed"],
        _ => null
    };
}
