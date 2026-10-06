using System.Globalization;
using Jularr.Web.Features.Acquisition.Wanted;
using Jularr.Web.Features.Discovery;
using Jularr.Web.Features.Library;
using Jularr.Web.Features.Localization;
using Jularr.Web.Features.Playback;

namespace Jularr.Web.Features.Acquisition.Access;

/// <summary>How a state is drawn: the one tone every request chip, state icon and progress bar of the consumer surfaces shares.</summary>
public enum RequestStateTone
{
    Waiting,
    Working,
    Done,
    Monitoring,
    Problem,
    Neutral
}

/// <summary>The consumer words of a request (docs/mockups/request-status-details): one owner of the label, headline, hint, milestone and time texts of the request surfaces.</summary>
public static class RequestStatusText
{
    /// <summary>A request older than this shows its date instead of "5 d ago".</summary>
    private static readonly TimeSpan s_maxRelativeAge = TimeSpan.FromDays(7);

    /// <summary>The media type of a request as one word ("Movie", "Series", "Anime"), the same words the detail pages and Request dialog use.</summary>
    public static string KindLabel(UiTextBundle ui, MediaAcquisitionKind kind) => kind == MediaAcquisitionKind.Audiobook
        ? ui["admin.requests.kind.audiobook"]
        : ui[VideoDetailView.KindKey(AcquisitionAccessNames.WorkType(kind))];

    /// <summary>What identifies a request under its title: the media type, what the card named (a native title, an author) and the year of a Movie or Series.</summary>
    public static string Identity(UiTextBundle ui, AcquisitionRequest request)
    {
        List<string> parts = [KindLabel(ui, request.Kind)];
        if (!string.IsNullOrWhiteSpace(request.Subtitle))
        {
            parts.Add(request.Subtitle);
        }

        if (request.Kind is MediaAcquisitionKind.Movie or MediaAcquisitionKind.Tv && VideoRequestPayload.Parse(request.PayloadJson)?.Year is { } year)
        {
            parts.Add(year.ToString(CultureInfo.InvariantCulture));
        }

        return string.Join(" · ", parts);
    }

    public static RequestStateTone Tone(AcquisitionRequest request, ConsumerAcquisitionView view) => request.IsCancelled
        ? RequestStateTone.Neutral
        : view.State switch
        {
            ConsumerAcquisitionState.WaitingForApproval => RequestStateTone.Waiting,
            ConsumerAcquisitionState.LookingForMedia or ConsumerAcquisitionState.GettingMedia or ConsumerAcquisitionState.Preparing => RequestStateTone.Working,
            ConsumerAcquisitionState.ReadyToWatch or ConsumerAcquisitionState.Available => RequestStateTone.Done,
            ConsumerAcquisitionState.MonitoringFutureReleases or ConsumerAcquisitionState.NotAvailableYet => RequestStateTone.Monitoring,
            _ => RequestStateTone.Problem
        };

    /// <summary>The short state of a chip: the state, with the transfer percentage only when it is reliable.</summary>
    public static string StateLabel(UiTextBundle ui, AcquisitionRequest request, ConsumerAcquisitionView view)
    {
        var label = request.IsCancelled ? ui["acquisition.state.cancelled"] : ui[ConsumerAcquisitionLabels.StateKey(view)];
        return view.ProgressPercent is { } percent ? ui.Format("requests.detail.progress", ("state", label), ("percent", percent)) : label;
    }

    /// <summary>The headline of the status surface; a request that could not be completed says so instead of the chip's "Needs attention".</summary>
    public static string Headline(UiTextBundle ui, AcquisitionRequest request, ConsumerAcquisitionView view) =>
        view.State == ConsumerAcquisitionState.NeedsAttention && !request.IsCancelled ? ui["requests.detail.state.couldNotComplete"] : StateLabel(ui, request, view);

    /// <summary>One sentence for the current state; none for a state whose card says everything (monitoring shows the next release).</summary>
    public static string? Hint(UiTextBundle ui, AcquisitionRequest request, ConsumerAcquisitionView view)
    {
        if (request.IsCancelled)
        {
            return ui["requests.detail.hint.cancelled"];
        }

        return view.State switch
        {
            ConsumerAcquisitionState.WaitingForApproval => ui["discover.request.hint.pending"],
            ConsumerAcquisitionState.LookingForMedia => ui["discover.request.hint.searching"],
            ConsumerAcquisitionState.GettingMedia => ui["discover.request.hint.downloading"],
            ConsumerAcquisitionState.Preparing => ui["discover.request.hint.importing"],
            ConsumerAcquisitionState.ReadyToWatch => ui["requests.detail.hint.readyToWatch"],
            ConsumerAcquisitionState.Available => ui["requests.detail.hint.available"],
            ConsumerAcquisitionState.NotAvailableYet => ui["acquisition.state.notAvailableYetHint"],
            ConsumerAcquisitionState.NotAvailable => ui["requests.detail.hint.notAvailable"],
            ConsumerAcquisitionState.NeedsAttention => ui["requests.detail.hint.needsAttention"],
            ConsumerAcquisitionState.Rejected => ui["requests.detail.hint.rejected"],
            _ => null
        };
    }

    public static string Milestone(UiTextBundle ui, RequestTimelineMilestone milestone) => milestone switch
    {
        RequestTimelineMilestone.Requested => ui["requests.detail.milestone.requested"],
        RequestTimelineMilestone.Approved => ui["requests.detail.milestone.approved"],
        RequestTimelineMilestone.LookingForMedia => ui["acquisition.state.lookingForMedia"],
        RequestTimelineMilestone.GettingMedia => ui["acquisition.state.gettingMedia"],
        RequestTimelineMilestone.Preparing => ui["acquisition.state.preparing"],
        RequestTimelineMilestone.Available => ui["acquisition.state.available"],
        RequestTimelineMilestone.MonitoringFutureReleases => ui["acquisition.state.monitoringFutureReleases"],
        RequestTimelineMilestone.Rejected => ui["acquisition.state.rejected"],
        RequestTimelineMilestone.Cancelled => ui["acquisition.state.cancelled"],
        _ => ui["requests.detail.state.couldNotComplete"]
    };

    /// <summary>How long ago a moment was ("Just now", "5 min ago", "3 h ago", "2 d ago"), or the date once it is a week old.</summary>
    public static string Age(UiTextBundle ui, DateTime thenUtc, DateTime nowUtc)
    {
        if (nowUtc - thenUtc >= s_maxRelativeAge)
        {
            return ShortDate(ui, thenUtc);
        }

        var (unit, count) = AdminWantedQuery.AgeOf(thenUtc, nowUtc);
        return unit switch
        {
            WantedAgeUnit.Minutes => ui.Format("admin.wanted.age.minutes", ("count", count)),
            WantedAgeUnit.Hours => ui.Format("admin.wanted.age.hours", ("count", count)),
            WantedAgeUnit.Days => ui.Format("admin.wanted.age.days", ("count", count)),
            _ => ui["admin.wanted.age.now"]
        };
    }

    /// <summary>A date in the profile's language ("8 Oct 2026"); the server never knows the viewer's time zone, so a date carries no time.</summary>
    public static string ShortDate(UiTextBundle ui, DateTime utc) => utc.ToString("d MMM yyyy", Culture(ui));

    /// <summary>The date and time of a timeline milestone as the server can render it, in UTC; the browser shows it in the viewer's own time zone.</summary>
    public static string Stamp(UiTextBundle ui, DateTime utc) => utc.ToString("d MMM HH:mm", Culture(ui)) + " UTC";

    /// <summary>The saved intent of a request in the words of the Request dialog, as label and value rows. Quality profiles stay out: they are not a consumer choice.</summary>
    public static IReadOnlyList<(string Label, string Value)> Intent(UiTextBundle ui, AcquisitionRequest request)
    {
        List<(string, string)> rows = [];
        if (request.Kind == MediaAcquisitionKind.Tv && VideoRequestPayload.Parse(request.PayloadJson) is { } payload)
        {
            rows.Add((ui["requests.detail.scope"], DiscoverRequestSummary.Scope(payload, ui)));
        }
        else if (request.Kind == MediaAcquisitionKind.Anime)
        {
            var options = request.Options;
            rows.Add((ui["requests.detail.scope"], ui[options.Scope switch
            {
                RequestScope.Seasons => "requests.new.scope.seasons",
                RequestScope.Episodes => "requests.new.scope.episodes",
                _ => "requests.new.scope.series"
            }]));
            if (options.Scope == RequestScope.Seasons)
            {
                rows.Add((ui["requests.detail.seasons"], RequestSelectionText.FormatSeasons(options.Seasons)));
            }
            else if (options.Scope == RequestScope.Episodes)
            {
                rows.Add((ui["requests.detail.episodes"], RequestSelectionText.FormatEpisodes(options.Episodes)));
            }

            if (options.AudioLanguage is { } audio)
            {
                rows.Add((ui["requests.detail.audio"], RequestLanguages.Name(audio)));
            }

            if (options.SubtitleLanguage is { } subtitles)
            {
                rows.Add((ui["requests.detail.subtitles"], subtitles == PlaybackLanguages.SubtitlesOff ? ui["requests.options.noSubtitles"] : RequestLanguages.Name(subtitles)));
            }
        }

        return rows;
    }

    private static CultureInfo Culture(UiTextBundle ui)
    {
        try
        {
            return CultureInfo.GetCultureInfo(ui.Locale.Replace('_', '-'));
        }
        catch (CultureNotFoundException)
        {
            return CultureInfo.InvariantCulture;
        }
    }
}
