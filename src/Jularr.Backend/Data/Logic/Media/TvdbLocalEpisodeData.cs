using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Fields.Media;

namespace Jularr.Backend.Data.Logic.Media
{
    internal sealed class TvdbLocalEpisodeData : BaseData
    {
        public TvdbLocalEpisodeData(long id, int seasonNumber, int episodeNumber, string? title)
        {
            Id = id;
            SeasonNumber = seasonNumber;
            EpisodeNumber = episodeNumber;
            Title = title;
        }

        [DataField(typeof(WorkUnitFields), nameof(WorkUnitFields.Id))]
        public long Id { get; }

        [DataField(typeof(WorkUnitFields), nameof(WorkUnitFields.SeasonNumber))]
        public int SeasonNumber { get; }

        [DataField(typeof(WorkUnitFields), nameof(WorkUnitFields.SeasonNumber))]
        public int EpisodeNumber { get; }

        [DataField(typeof(WorkUnitFields), nameof(WorkUnitFields.Title), false)]
        public string? Title { get; }
    }
}
