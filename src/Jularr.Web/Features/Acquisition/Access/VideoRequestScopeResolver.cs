using Jularr.Web.Data;
using Jularr.Web.Features.MediaCore;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Web.Features.Acquisition.Access;

public sealed record VideoRequestEpisode(Guid Id, int Number, string? Title, DateTime? AiredAt);

/// <summary>
/// One season of a TV Work as the Request dialog offers it. <see cref="Id"/> is null for episodes that only
/// carry a flat season number; such a season can still be requested through its episode ids.
/// </summary>
public sealed record VideoRequestSeason(Guid? Id, int Number, bool IsSpecial, IReadOnlyList<VideoRequestEpisode> Episodes);

/// <summary>What a requester chose for a TV title. The ids come from the browser and are only trusted after <see cref="VideoRequestScopeResolver.BuildTvPayloadAsync"/>.</summary>
public sealed record VideoRequestScopeChoice(
    VideoRequestScope Scope,
    IReadOnlyCollection<Guid> SeasonIds,
    IReadOnlyCollection<Guid> EpisodeIds,
    bool MonitorFuture);

/// <summary>
/// The canonical owner of the TV scope a requester may choose: it reads the structure of the selected Work
/// for the Request dialog and turns the browser's choice into the <see cref="VideoRequestPayload"/> the
/// executor consumes. Season and episode ids must belong to that Work, so a forged id can never widen a
/// request to another title.
/// </summary>
public sealed class VideoRequestScopeResolver(AppDbContext db)
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

    /// <summary>The same for a request that only knows the id of its Work; null when that Work is gone.</summary>
    public async Task<VideoRequestPayload?> BuildTvPayloadAsync(Guid workId, VideoRequestScopeChoice choice, CancellationToken cancellationToken) =>
        await db.Works.AsNoTracking().FirstOrDefaultAsync(x => x.Id == workId, cancellationToken) is { } work ? await BuildTvPayloadAsync(work, choice, cancellationToken) : null;

    /// <summary>
    /// Validates the choice against the Work's own structure and returns the request payload. Throws
    /// <see cref="ArgumentException"/> for an unknown id, an empty custom selection or ids sent with a scope that has none.
    /// </summary>
    public async Task<VideoRequestPayload> BuildTvPayloadAsync(Work work, VideoRequestScopeChoice choice, CancellationToken cancellationToken)
    {
        var seasonIds = choice.SeasonIds.Distinct().ToArray();
        var episodeIds = choice.EpisodeIds.Distinct().ToArray();
        if (choice.Scope == VideoRequestScope.WholeWork)
        {
            throw new ArgumentException("A series request needs a scope.", nameof(choice));
        }

        if (choice.Scope != VideoRequestScope.Custom)
        {
            if (seasonIds.Length > 0 || episodeIds.Length > 0)
            {
                throw new ArgumentException("Only a custom scope selects seasons or episodes.", nameof(choice));
            }

            return Payload(work, choice.Scope, [], [], monitorFuture: true);
        }

        if (seasonIds.Length == 0 && episodeIds.Length == 0 && !choice.MonitorFuture)
        {
            throw new ArgumentException("Choose at least one season, episode or future releases.", nameof(choice));
        }

        if (seasonIds.Length > MaxSelectedSeasons || episodeIds.Length > MaxSelectedEpisodes)
        {
            throw new ArgumentException("The selection is too large.", nameof(choice));
        }

        var knownSeasons = await db.WorkSeasons.AsNoTracking().CountAsync(x => x.WorkId == work.Id && seasonIds.Contains(x.Id), cancellationToken);
        var knownEpisodes = await db.WorkEpisodes.AsNoTracking().CountAsync(x => x.WorkId == work.Id && episodeIds.Contains(x.Id), cancellationToken);
        if (knownSeasons != seasonIds.Length || knownEpisodes != episodeIds.Length)
        {
            throw new ArgumentException("The selection contains a season or episode that does not belong to this title.", nameof(choice));
        }

        return Payload(work, VideoRequestScope.Custom, seasonIds, episodeIds, choice.MonitorFuture);
    }

    private static VideoRequestPayload Payload(Work work, VideoRequestScope scope, Guid[] seasonIds, Guid[] episodeIds, bool monitorFuture) =>
        new(work.Id, work.CanonicalTitle, work.Year, scope, episodeIds, monitorFuture, SelectedSeasonIds: seasonIds);
}
