using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Fields.Acquisition;

namespace Jularr.Backend.Data.Logic.Acquisition
{
    // The groups one search saw; each counts once per search.
    internal sealed class ReleaseGroupSightingsData : BaseData
    {
        public ReleaseGroupSightingsData(string[] names, DateTime seenAt)
        {
            Names = names;
            SeenAt = seenAt;
        }

        [DataField(typeof(ReleaseGroupFields), nameof(ReleaseGroupFields.Names))]
        public string[] Names { get; }

        [DataField(typeof(ReleaseGroupFields), nameof(ReleaseGroupFields.SeenAt))]
        public DateTime SeenAt { get; }
    }

    // A group typed by hand.
    internal sealed class ReleaseGroupAddData : BaseData
    {
        public ReleaseGroupAddData(string name)
        {
            Name = name;
        }

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

    // The groups whose name holds the search text in any case; no text lists them all.
    internal sealed class ReleaseGroupFilterData : BaseData
    {
        public ReleaseGroupFilterData(string? search)
        {
            Search = search;
        }

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
