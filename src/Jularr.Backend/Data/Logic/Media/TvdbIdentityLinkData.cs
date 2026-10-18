using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Fields.Media;

namespace Jularr.Backend.Data.Logic.Media
{
    internal sealed class TvdbIdentityLinkData : BaseData
    {
        public TvdbIdentityLinkData(long id, int tvdbId, long version, string imdbId)
        {
            Id = id;
            TvdbId = tvdbId;
            Version = version;
            ImdbId = imdbId;
        }

        [DataField(typeof(WorkFields), nameof(WorkFields.Id))]
        public long Id { get; }

        [DataField(typeof(WorkMetadataFields), nameof(WorkMetadataFields.TvdbId))]
        public int TvdbId { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.Version))]
        public long Version { get; }

        [DataField(typeof(WorkMetadataFields), nameof(WorkMetadataFields.ExternalId))]
        public string ImdbId { get; }
    }
}
