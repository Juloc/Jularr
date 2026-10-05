using Jularr.Web.Features.Acquisition.Access;

namespace Jularr.Web.Features.Acquisition.Monitoring;

/// <summary>One episode of a Series as the monitoring policy sees it.</summary>
public sealed record VideoEpisodeRef(Guid Id, Guid? SeasonId, int SeasonNumber, DateTime? AiredAt);

/// <summary>
/// The policy of switching one season or episode of a Series on or off. It is a pure function of the stored payload, so it runs inside
/// the compare-and-set write of <see cref="VideoMonitoringService"/> and always applies to the latest stored state: two switches made at the
/// same moment both survive. The scope the Series already has is kept; a switch only adds to the Admin-owned selections and exclusions of
/// <see cref="VideoRequestPayload"/>, so an episode added later is treated exactly as the scope says and no list grows with the episode count.
/// </summary>
public static class VideoUnitMonitoring
{
    /// <summary>
    /// <paramref name="targets"/> are the episodes of the switched unit; <paramref name="wholeSeasonId"/> is set when the unit is a season
    /// that has its own id (the season is then switched as a whole and so are episodes that appear later). A Series that monitored nothing
    /// starts from an empty custom selection, never from what an earlier, stopped selection left behind. A custom selection that ends up
    /// selecting nothing is unmonitored.
    /// </summary>
    public static VideoRequestPayload Switch(
        VideoRequestPayload payload,
        DateTime requestCreatedAt,
        DateTime nowUtc,
        IReadOnlyList<VideoEpisodeRef> targets,
        Guid? wholeSeasonId,
        bool monitored)
    {
        if (!payload.Monitored && !monitored)
        {
            return payload;
        }

        var current = payload.Monitored
            ? payload
            : payload with
            {
                Monitored = true,
                Scope = VideoRequestScope.Custom,
                SelectedEpisodeIds = [],
                SelectedSeasonIds = [],
                ExcludedEpisodeIds = [],
                ExcludedSeasonIds = [],
                MonitorFuture = false,
                MonitorFutureFromUtc = nowUtc
            };
        var selectedEpisodes = current.SelectedEpisodeIds.ToHashSet();
        var selectedSeasons = (current.SelectedSeasonIds ?? []).ToHashSet();
        var excludedEpisodes = (current.ExcludedEpisodeIds ?? []).ToHashSet();
        var excludedSeasons = (current.ExcludedSeasonIds ?? []).ToHashSet();
        var targetIds = targets.Select(target => target.Id).ToHashSet();

        if (wholeSeasonId is { } season)
        {
            excludedEpisodes.ExceptWith(targetIds);
            selectedEpisodes.ExceptWith(targetIds);
            if (monitored)
            {
                excludedSeasons.Remove(season);
                if (current.Scope != VideoRequestScope.AllCurrentAndFuture)
                {
                    selectedSeasons.Add(season);
                }
            }
            else
            {
                selectedSeasons.Remove(season);
                excludedSeasons.Add(season);
            }

            return Finished(current, selectedEpisodes, selectedSeasons, excludedEpisodes, excludedSeasons);
        }

        foreach (var target in targets)
        {
            if (monitored)
            {
                excludedEpisodes.Remove(target.Id);
                if (!IncludedBy(current, requestCreatedAt, target, selectedEpisodes, selectedSeasons, excludedEpisodes, excludedSeasons))
                {
                    selectedEpisodes.Add(target.Id);
                }
            }
            else
            {
                selectedEpisodes.Remove(target.Id);
                if (IncludedBy(current, requestCreatedAt, target, selectedEpisodes, selectedSeasons, excludedEpisodes, excludedSeasons))
                {
                    excludedEpisodes.Add(target.Id);
                }
            }
        }

        return Finished(current, selectedEpisodes, selectedSeasons, excludedEpisodes, excludedSeasons);
    }

    private static bool IncludedBy(
        VideoRequestPayload payload,
        DateTime requestCreatedAt,
        VideoEpisodeRef episode,
        HashSet<Guid> selectedEpisodes,
        HashSet<Guid> selectedSeasons,
        HashSet<Guid> excludedEpisodes,
        HashSet<Guid> excludedSeasons) =>
        new VideoRequestSelection(WithSets(payload, selectedEpisodes, selectedSeasons, excludedEpisodes, excludedSeasons), requestCreatedAt)
            .Includes(episode.Id, episode.SeasonId, episode.AiredAt);

    private static VideoRequestPayload Finished(
        VideoRequestPayload payload,
        HashSet<Guid> selectedEpisodes,
        HashSet<Guid> selectedSeasons,
        HashSet<Guid> excludedEpisodes,
        HashSet<Guid> excludedSeasons)
    {
        var next = WithSets(payload, selectedEpisodes, selectedSeasons, excludedEpisodes, excludedSeasons);
        var selectsNothing = next.Scope == VideoRequestScope.Custom && !next.MonitorFuture && selectedEpisodes.Count == 0 && selectedSeasons.Count == 0;
        return selectsNothing ? next with { Monitored = false } : next;
    }

    private static VideoRequestPayload WithSets(
        VideoRequestPayload payload,
        HashSet<Guid> selectedEpisodes,
        HashSet<Guid> selectedSeasons,
        HashSet<Guid> excludedEpisodes,
        HashSet<Guid> excludedSeasons) =>
        payload with
        {
            SelectedEpisodeIds = [.. selectedEpisodes],
            SelectedSeasonIds = [.. selectedSeasons],
            ExcludedEpisodeIds = [.. excludedEpisodes],
            ExcludedSeasonIds = [.. excludedSeasons]
        };
}
