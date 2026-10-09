using Jularr.Web.Data;
using Jularr.Web.Features.MediaCore;
using Jularr.Web.Features.Monitoring;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Web.Features.Acquisition.Access;

/// <summary>The vocabulary of the Request dialog. It is a command, applied as ordinary monitoring decisions, and is never stored as a state.</summary>
public enum VideoRequestScope
{
    WholeWork,
    AllCurrentAndFuture,
    FutureOnly,
    Custom
}

public sealed record VideoRequestEpisode(Guid Id, int Number, string? Title, DateTime? AiredAt);

/// <summary>
/// One season of a TV Work as the Request dialog offers it. <see cref="Id"/> is null for episodes that only
/// carry a flat season number; such a season can still be requested through its episode ids.
/// </summary>
public sealed record VideoRequestSeason(Guid? Id, int Number, bool IsSpecial, IReadOnlyList<VideoRequestEpisode> Episodes);

/// <summary>What a requester chose for a title. The ids come from the browser and are only trusted after <see cref="VideoRequestScopeResolver.ValidateTvAsync"/>.</summary>
public sealed record VideoRequestScopeChoice(
    VideoRequestScope Scope,
    IReadOnlyCollection<Guid> SeasonIds,
    IReadOnlyCollection<Guid> EpisodeIds,
    bool MonitorFuture);

/// <summary>
/// The canonical owner of what a requester may choose to monitor: it reads the structure of the selected Work for the Request dialog, checks the
/// browser's choice against that structure, applies it as monitoring decisions once the request is approved, and reads the current decisions back as
/// a choice for the dialog. Season and episode ids must belong to the Work, so a forged id can never widen a request to another title.
/// </summary>
public sealed class VideoRequestScopeResolver(AppDbContext db, MonitoringCommands commands, MonitoringResolver monitoring)
{
    public const int MaxSelectedSeasons = 200;
    public const int MaxSelectedEpisodes = 5000;

    /// <summary>Reads the scope names the Request dialog sends: <c>all</c>, <c>future</c> or <c>custom</c>.</summary>
    public static bool TryParseScope(string? value, out VideoRequestScope scope)
    {
        scope = value switch
        {
            "all" => VideoRequestScope.AllCurrentAndFuture,
            "future" => VideoRequestScope.FutureOnly,
            "custom" => VideoRequestScope.Custom,
            _ => VideoRequestScope.WholeWork
        };
        return scope != VideoRequestScope.WholeWork;
    }

    public async Task<IReadOnlyList<VideoRequestSeason>> LoadStructureAsync(Guid workId, CancellationToken cancellationToken)
    {
        var seasons = await db.WorkSeasons.AsNoTracking().Where(x => x.WorkId == workId).ToListAsync(cancellationToken);
        var episodes = await db.WorkEpisodes.AsNoTracking().Where(x => x.WorkId == workId).OrderBy(x => x.SeasonNumber).ThenBy(x => x.EpisodeNumber).ToListAsync(cancellationToken);
        var numbers = seasons.Select(x => x.SeasonNumber).Union(episodes.Select(x => x.SeasonNumber)).Order();
        return
        [
            .. numbers.Select(number => new VideoRequestSeason(
                seasons.FirstOrDefault(x => x.SeasonNumber == number)?.Id,
                number,
                number == 0,
                [.. episodes.Where(x => x.SeasonNumber == number).Select(x => new VideoRequestEpisode(x.Id, x.EpisodeNumber, x.Title, x.AiredAt))]))
        ];
    }

    /// <summary>
    /// Checks the choice against the Work's own structure and returns it without duplicates. Throws <see cref="ArgumentException"/> for an unknown id,
    /// an empty custom selection or ids sent with a scope that has none.
    /// </summary>
    public async Task<VideoRequestScopeChoice> ValidateTvAsync(Guid workId, VideoRequestScopeChoice choice, CancellationToken cancellationToken)
    {
        var seasonIds = choice.SeasonIds.Distinct().ToArray();
        var episodeIds = choice.EpisodeIds.Distinct().ToArray();
        if (choice.Scope == VideoRequestScope.WholeWork)
        {
            throw new ArgumentException("A series request needs a scope.", nameof(choice));
        }

        if (choice.Scope != VideoRequestScope.Custom)
        {
            return seasonIds.Length > 0 || episodeIds.Length > 0
                ? throw new ArgumentException("Only a custom scope selects seasons or episodes.", nameof(choice))
                : new VideoRequestScopeChoice(choice.Scope, [], [], MonitorFuture: true);
        }

        if (seasonIds.Length == 0 && episodeIds.Length == 0 && !choice.MonitorFuture)
        {
            throw new ArgumentException("Choose at least one season, episode or future releases.", nameof(choice));
        }

        if (seasonIds.Length > MaxSelectedSeasons || episodeIds.Length > MaxSelectedEpisodes)
        {
            throw new ArgumentException("The selection is too large.", nameof(choice));
        }

        var knownSeasons = await db.WorkSeasons.AsNoTracking().CountAsync(x => x.WorkId == workId && seasonIds.Contains(x.Id), cancellationToken);
        var knownEpisodes = await db.WorkEpisodes.AsNoTracking().CountAsync(x => x.WorkId == workId && episodeIds.Contains(x.Id), cancellationToken);
        return knownSeasons != seasonIds.Length || knownEpisodes != episodeIds.Length
            ? throw new ArgumentException("The selection contains a season or episode that does not belong to this title.", nameof(choice))
            : new VideoRequestScopeChoice(VideoRequestScope.Custom, seasonIds, episodeIds, choice.MonitorFuture);
    }

    /// <summary>Applies a validated choice as monitoring decisions: all and the Movie switch the Work on, future and a custom selection write their ordinary decisions.</summary>
    public Task ApplyAsync(Guid workId, VideoRequestScopeChoice? choice, CancellationToken cancellationToken) => choice?.Scope switch
    {
        VideoRequestScope.FutureOnly => commands.FutureAsync(workId, cancellationToken),
        VideoRequestScope.Custom => commands.ApplySelectionAsync(workId, choice.SeasonIds, choice.EpisodeIds, choice.MonitorFuture, cancellationToken),
        _ => commands.SetAsync(MonitoringTargetKind.Work, workId, true, cancellationToken)
    };

    /// <summary>
    /// The current monitoring of a Series as a choice for the dialog, or null when nothing is monitored. Everything on and nothing decided is "all"; the
    /// Work on with every known episode off is "future"; anything else is a custom selection of the seasons and episodes that are on.
    /// </summary>
    public async Task<VideoRequestScopeChoice?> ChoiceOfAsync(Guid workId, CancellationToken cancellationToken)
    {
        var view = await monitoring.LoadAsync(workId, cancellationToken);
        if (!view.IsAnyMonitored)
        {
            return null;
        }

        var structure = await LoadStructureAsync(workId, cancellationToken);
        var seasonIds = view.DecidedIds(MonitoringTargetKind.Season, monitored: true).ToArray();
        var seasonOf = structure.SelectMany(season => season.Episodes.Select(episode => (Episode: episode.Id, Season: season.Id))).ToDictionary(pair => pair.Episode, pair => pair.Season);
        if (view.IsWorkMonitored && !view.HasNodeDecisions)
        {
            return new VideoRequestScopeChoice(VideoRequestScope.AllCurrentAndFuture, [], [], MonitorFuture: true);
        }

        if (view.IsWorkMonitored && seasonIds.Length == 0 && seasonOf.Keys.All(id => view.DecisionOf(id) == false))
        {
            return new VideoRequestScopeChoice(VideoRequestScope.FutureOnly, [], [], MonitorFuture: true);
        }

        var episodes = seasonOf.Keys.Where(id => view.IsMonitored(id, seasonOf[id]) && !(seasonOf[id] is { } season && seasonIds.Contains(season))).ToArray();
        return new VideoRequestScopeChoice(VideoRequestScope.Custom, seasonIds, episodes, view.IsWorkMonitored);
    }
}
