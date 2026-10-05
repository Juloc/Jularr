using Jularr.Web.Features.Library;
using Jularr.Web.Features.Progress;
using Jularr.Web.Ui;

namespace Jularr.Web.Features.InstantPlay;

/// <summary>One known episode of a Series or Anime Work as the next-episode rule sees it, whether or not a file exists for it.</summary>
/// <param name="IsReleased">False for an episode that has not aired yet: it can be neither watched nor acquired.</param>
public sealed record SeriesUnit(Guid Id, int SeasonNumber, int Number, bool HasMedia, bool IsReleased)
{
    public bool IsSpecial => SeasonNumber <= 0;
}

/// <summary>The episode a Series offers next and where the profile stands in it.</summary>
public sealed record NextEpisode(SeriesUnit Unit, MediaBannerProgressState State);

/// <summary>
/// The one rule that says which episode of a Series the profile needs next. The Library card, the detail hero and the playback
/// intent all use it, so they can never disagree. It reads the canonical episode structure, local or not, and the profile's
/// canonical progress:
/// <list type="bullet">
/// <item>No history: the first released episode.</item>
/// <item>The most recently touched episode (completed, or resumable past <see cref="VideoProgressService.MinimumResumeMs"/>) is the
/// anchor, the same one Continue Watching uses. An unfinished anchor is resumed.</item>
/// <item>After a completed anchor: the next released unwatched episode after it, else the first released unwatched one.</item>
/// <item>Specials are only offered by a title that has nothing else; an episode the structure does not know is never guessed.</item>
/// <item>Everything released is watched: the first local episode is offered again, or nothing when none is local.</item>
/// </list>
/// </summary>
public static class NextRequiredEpisode
{
    public static NextEpisode? Resolve(IReadOnlyCollection<SeriesUnit> units, IReadOnlyDictionary<Guid, EpisodeProgressState> progress)
    {
        var ordered = units.OrderBy(x => x.IsSpecial ? 1 : 0).ThenBy(x => x.SeasonNumber).ThenBy(x => x.Number).ThenBy(x => x.Id).ToArray();
        var regular = ordered.Where(x => !x.IsSpecial).ToArray();
        var candidates = regular.Length > 0 ? regular : ordered;

        bool IsWatched(SeriesUnit unit) => progress.TryGetValue(unit.Id, out var row) && row.IsCompleted;
        bool IsRequired(SeriesUnit unit) => unit.IsReleased && !IsWatched(unit);

        var anchor = ordered
            .Where(unit => progress.TryGetValue(unit.Id, out var row) && (row.IsCompleted || row.PositionMs >= VideoProgressService.MinimumResumeMs))
            .OrderByDescending(unit => progress[unit.Id].UpdatedAt)
            .ThenBy(unit => unit.Id)
            .FirstOrDefault();
        if (anchor is null)
        {
            return candidates.FirstOrDefault(x => x.IsReleased) is { } first ? new NextEpisode(first, MediaBannerProgressState.NotStarted) : null;
        }

        if (!IsWatched(anchor))
        {
            return new NextEpisode(anchor, MediaBannerProgressState.InProgress);
        }

        var next = candidates.SkipWhile(x => x.Id != anchor.Id).Skip(1).FirstOrDefault(IsRequired) ?? candidates.FirstOrDefault(IsRequired);
        if (next is not null)
        {
            return new NextEpisode(next, MediaBannerProgressState.InProgress);
        }

        return candidates.FirstOrDefault(x => x.HasMedia) is { } rewatch ? new NextEpisode(rewatch, MediaBannerProgressState.Completed) : null;
    }
}
