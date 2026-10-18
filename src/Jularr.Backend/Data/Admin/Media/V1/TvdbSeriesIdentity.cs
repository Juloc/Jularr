using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Fields.Media;

namespace Jularr.Backend.Data.Admin.Media.V1
{
    // Explicitly confirm a series identity, without changing episodes, files or progress.
    public sealed class LinkTvdbSeriesIdentityDataV1 : BaseData
    {
        public LinkTvdbSeriesIdentityDataV1(int tvdbId, long version)
        {
            TvdbId = tvdbId;
            Version = version;
        }

        [DataField(typeof(WorkMetadataFields), nameof(WorkMetadataFields.TvdbId))]
        public int TvdbId { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.Version))]
        public long Version { get; }
    }
}
