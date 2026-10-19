using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Fields.Media;

namespace Jularr.Backend.Data.Admin.Media.V1
{
    // Explicit operator approval of a remote episode to an existing local WorkUnit.
    public sealed class LinkTvdbEpisodeIdentityDataV1 : BaseData
    {
        public LinkTvdbEpisodeIdentityDataV1(long workUnitId, int tvdbEpisodeId, long version)
        {
            WorkUnitId = workUnitId;
            TvdbEpisodeId = tvdbEpisodeId;
            Version = version;
        }

        [DataField(typeof(WorkUnitFields), nameof(WorkUnitFields.Id))]
        public long WorkUnitId { get; }

        [DataField(typeof(WorkMetadataFields), nameof(WorkMetadataFields.TvdbId))]
        public int TvdbEpisodeId { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.Version))]
        public long Version { get; }
    }
}
