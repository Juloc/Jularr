using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Fields.Media;

namespace Jularr.Backend.Data.Admin.Media.V1
{
    public sealed class TvdbSeriesPreviewV1 : BaseResponse
    {
        public TvdbSeriesPreviewV1(long workId, int tvdbId, string seriesName, string? imdbId, IReadOnlyList<TvdbEpisodeComparisonV1> episodes)
        {
            WorkId = workId;
            TvdbId = tvdbId;
            SeriesName = seriesName;
            ImdbId = imdbId;
            Episodes = episodes;
        }

        [DataField(typeof(WorkFields), nameof(WorkFields.Id))]
        public long WorkId { get; }

        [DataField(typeof(WorkMetadataFields), nameof(WorkMetadataFields.TvdbId))]
        public int TvdbId { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.CanonicalTitle))]
        public string SeriesName { get; }

        [DataField(typeof(WorkMetadataFields), nameof(WorkMetadataFields.ExternalId), false)]
        public string? ImdbId { get; }

        // Same number alone is never evidence that two provider episodes are the same.
        public IReadOnlyList<TvdbEpisodeComparisonV1> Episodes { get; }
    }

    public sealed class TvdbEpisodeComparisonV1 : BaseResponse
    {
        public TvdbEpisodeComparisonV1(int seasonNumber, int episodeNumber, int? tvdbEpisodeId, long? localWorkUnitId, string? tvdbTitle, string? localTitle, string state)
        {
            SeasonNumber = seasonNumber;
            EpisodeNumber = episodeNumber;
            TvdbEpisodeId = tvdbEpisodeId;
            LocalWorkUnitId = localWorkUnitId;
            TvdbTitle = tvdbTitle;
            LocalTitle = localTitle;
            State = state;
        }

        [DataField(typeof(WorkUnitFields), nameof(WorkUnitFields.SeasonNumber))]
        public int SeasonNumber { get; }

        [DataField(typeof(WorkUnitFields), nameof(WorkUnitFields.SeasonNumber))]
        public int EpisodeNumber { get; }

        public int? TvdbEpisodeId { get; }
        public long? LocalWorkUnitId { get; }
        public string? TvdbTitle { get; }
        public string? LocalTitle { get; }

        // new: no existing episode number; review: same number; local_only: not on TVDB.
        public string State { get; }
    }
}
