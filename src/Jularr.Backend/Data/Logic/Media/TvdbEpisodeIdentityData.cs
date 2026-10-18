using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Fields.Media;

namespace Jularr.Backend.Data.Logic.Media
{
    internal sealed class TvdbEpisodeIdentityData : BaseData
    {
        public TvdbEpisodeIdentityData(long workId, long workUnitId, int tvdbId, int tvdbEpisodeId, long version)
        {
            WorkId = workId;
            WorkUnitId = workUnitId;
            TvdbId = tvdbId;
            TvdbEpisodeId = tvdbEpisodeId;
            Version = version;
        }

        [DataField(typeof(WorkFields), nameof(WorkFields.Id))]
        public long WorkId { get; }

        [DataField(typeof(WorkUnitFields), nameof(WorkUnitFields.Id))]
        public long WorkUnitId { get; }

        [DataField(typeof(WorkMetadataFields), nameof(WorkMetadataFields.TvdbId))]
        public int TvdbId { get; }

        [DataField(typeof(WorkMetadataFields), nameof(WorkMetadataFields.TvdbId))]
        public int TvdbEpisodeId { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.Version))]
        public long Version { get; }
    }
}
