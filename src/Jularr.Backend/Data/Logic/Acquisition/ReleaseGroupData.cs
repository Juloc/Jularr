using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Fields.Acquisition;
using Jularr.Backend.Data.Fields.Media;

namespace Jularr.Backend.Data.Logic.Acquisition
{
    // The groups one search saw for a media type; each counts once per search.
    internal sealed class ReleaseGroupSightingsData : BaseData
    {
        public ReleaseGroupSightingsData(MediaType mediaTypeId, string[] names, DateTime seenAt)
        {
            MediaTypeId = mediaTypeId;
            Names = names;
            SeenAt = seenAt;
        }

        [DataField(typeof(WorkFields), nameof(WorkFields.MediaTypeId))]
        public MediaType MediaTypeId { get; }

        [DataField(typeof(ReleaseGroupFields), nameof(ReleaseGroupFields.Names))]
        public string[] Names { get; }

        [DataField(typeof(ReleaseGroupFields), nameof(ReleaseGroupFields.SeenAt))]
        public DateTime SeenAt { get; }
    }

    // A group typed by hand for a media type.
    internal sealed class ReleaseGroupAddData : BaseData
    {
        public ReleaseGroupAddData(MediaType mediaTypeId, string name)
        {
            MediaTypeId = mediaTypeId;
            Name = name;
        }

        [DataField(typeof(WorkFields), nameof(WorkFields.MediaTypeId))]
        public MediaType MediaTypeId { get; }

        [DataField(typeof(ReleaseGroupFields), nameof(ReleaseGroupFields.Name))]
        public string Name { get; }
    }

    // A group's rating; none means not rated.
    internal sealed class ReleaseGroupRatingData : BaseData
    {
        public ReleaseGroupRatingData(long id, ReleaseGroupRating? releaseGroupRatingId)
        {
            Id = id;
            ReleaseGroupRatingId = releaseGroupRatingId;
        }

        [DataField(typeof(ReleaseGroupFields), nameof(ReleaseGroupFields.Id))]
        public long Id { get; }

        [DataField(typeof(ReleaseGroupFields), nameof(ReleaseGroupFields.Rating), false)]
        public ReleaseGroupRating? ReleaseGroupRatingId { get; }
    }

    // A media type's groups whose name holds the search text in any case; no text lists them all.
    internal sealed class ReleaseGroupFilterData : BaseData
    {
        public ReleaseGroupFilterData(MediaType mediaTypeId, string? search)
        {
            MediaTypeId = mediaTypeId;
            Search = search;
        }

        [DataField(typeof(WorkFields), nameof(WorkFields.MediaTypeId))]
        public MediaType MediaTypeId { get; }

        [DataField(typeof(ReleaseGroupFields), nameof(ReleaseGroupFields.Search))]
        public string? Search { get; }
    }

    internal sealed class ReleaseGroupKeyData : BaseData
    {
        public ReleaseGroupKeyData(long id)
        {
            Id = id;
        }

        [DataField(typeof(ReleaseGroupFields), nameof(ReleaseGroupFields.Id))]
        public long Id { get; }
    }
}
