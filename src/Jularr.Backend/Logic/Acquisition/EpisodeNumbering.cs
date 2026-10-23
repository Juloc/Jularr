using Jularr.Backend.Data.Logic.Acquisition;

namespace Jularr.Backend.Logic.Acquisition
{
    // Releases of Anime count episodes across seasons, daily shows name their air date; both map onto the season and episodes of the Work.
    internal static class EpisodeNumbering
    {
        public static (int Season, int Start, int End)? Resolve(ReleaseInfo parsed, IReadOnlyList<TargetUnit> episodes)
        {
            if (parsed.SeasonNumber is { } season && parsed.EpisodeStart is { } start)
            {
                return (season, start, parsed.EpisodeEnd ?? start);
            }

            var matched = parsed.AbsoluteEpisodeStart is { } first
                ? episodes.Where(episode => episode.Absolute >= first && episode.Absolute <= (parsed.AbsoluteEpisodeEnd ?? first)).ToArray()
                : parsed.AirDate is { } date ? episodes.Where(episode => episode.ReleasedOn == date).ToArray() : [];

            // One season only; a release across seasons is no single placement.
            return matched.Length > 0 && matched.Select(episode => episode.ParentNumber).Distinct().Count() == 1 && matched[0].ParentNumber is { } parent
                ? (parent, matched.Min(episode => episode.Number), matched.Max(episode => episode.Number))
                : null;
        }

        public static IReadOnlyList<TargetUnit> Episodes(IReadOnlyList<UnitStateData> states) =>
            [.. states.Where(state => state.ParentNumber is not null).Select(state => new TargetUnit(state.WorkUnitId, state.ParentNumber, state.Number, state.HasMedia, state.AbsoluteNumber, state.ReleasedOn))];
    }
}
