using Jularr.Web.Features.Localization;
using Jularr.Web.Features.Playback;

namespace Jularr.Web.Features.Acquisition.Access;

/// <summary>
/// The choices of a request as short lines for the request lists (history and the owner's queue). Only
/// what deviates from the defaults is listed: a request for the whole series in the release's own
/// languages and the title's own profile has nothing to say.
/// </summary>
public static class RequestOptionsSummary
{
    /// <param name="profileNames">Quality profile names by id; an id that is not listed is shown as it is.</param>
    public static IReadOnlyList<string> Describe(
        AcquisitionRequestOptions options,
        UiTextBundle ui,
        IReadOnlyDictionary<string, string>? profileNames = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(ui);

        var lines = new List<string>();
        switch (options.Scope)
        {
            case RequestScope.FutureOnly:
                lines.Add(ui["requests.options.futureOnly"]);
                break;
            case RequestScope.Custom:
                if (options.Seasons.Count > 0)
                {
                    lines.Add(ui.Format("requests.options.seasons", ("list", RequestSelectionText.FormatSeasons(options.Seasons))));
                }
                if (options.Episodes.Count > 0)
                {
                    lines.Add(ui.Format("requests.options.episodes", ("list", RequestSelectionText.FormatEpisodes(options.Episodes))));
                }
                if (options.MonitorFuture)
                {
                    lines.Add(ui["requests.options.futureIncluded"]);
                }
                break;
            case RequestScope.Seasons:
                lines.Add(ui.Format("requests.options.seasons", ("list", RequestSelectionText.FormatSeasons(options.Seasons))));
                break;
            case RequestScope.Episodes:
                lines.Add(ui.Format("requests.options.episodes", ("list", RequestSelectionText.FormatEpisodes(options.Episodes))));
                break;
        }

        if (options.AudioLanguage is { } audio)
        {
            lines.Add(ui.Format("requests.options.audio", ("language", RequestLanguages.Name(audio))));
        }

        if (options.SubtitleLanguage is { } subtitles)
        {
            lines.Add(subtitles == PlaybackLanguages.SubtitlesOff
                ? ui["requests.options.noSubtitles"]
                : ui.Format("requests.options.subtitles", ("language", RequestLanguages.Name(subtitles))));
        }

        if (options.QualityProfileId is { } profile)
        {
            lines.Add(ui.Format("requests.options.profile", ("profile", profileNames?.GetValueOrDefault(profile) ?? profile)));
        }

        return lines;
    }
}
