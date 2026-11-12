using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Fields.Acquisition;

namespace Jularr.Backend.Data.Admin.Acquisition.V1
{
    public sealed class ReadReleaseGroupsParametersV1 : BaseParameters
    {
        public ReadReleaseGroupsParametersV1(string? search = null, int page = 1, int pageSize = 25, ReleaseGroupFilter? filter = null, AcquisitionMediaKind? mediaKind = null,
            ReleaseGroupSort? sort = null)
        {
            Search = search;
            Page = page;
            PageSize = pageSize;
            Filter = filter;
            MediaKind = mediaKind;
            Sort = sort;
        }

        // Part of a group's name, in any case.
        [DataField(typeof(ReleaseGroupFields), nameof(ReleaseGroupFields.Search))]
        public string? Search { get; }

        // Not rated groups or one rating; none is every group.
        [DataField(typeof(ReleaseGroupFields), nameof(ReleaseGroupFields.Filter), false)]
        public ReleaseGroupFilter? Filter { get; }

        // Only groups a search of this media type saw.
        [DataField(typeof(ReleaseGroupFields), nameof(ReleaseGroupFields.MediaKind), false)]
        public AcquisitionMediaKind? MediaKind { get; }

        // Rating first when none is given.
        [DataField(typeof(ReleaseGroupFields), nameof(ReleaseGroupFields.Sort), false)]
        public ReleaseGroupSort? Sort { get; }

        public int Page { get; }

        public int PageSize { get; }
    }

    public sealed class ReleaseGroupDetailsV1 : BaseResponse
    {
        public ReleaseGroupDetailsV1(long id, string name, ReleaseGroupRating? releaseGroupRatingId, int seenCount, DateTime? lastSeenAt, AcquisitionMediaKind[] mediaKinds)
        {
            Id = id;
            Name = name;
            ReleaseGroupRatingId = releaseGroupRatingId;
            SeenCount = seenCount;
            LastSeenAt = lastSeenAt;
            MediaKinds = mediaKinds;
        }

        [DataField(typeof(ReleaseGroupFields), nameof(ReleaseGroupFields.Id))]
        public long Id { get; }

        [DataField(typeof(ReleaseGroupFields), nameof(ReleaseGroupFields.Name))]
        public string Name { get; }

        // None means not rated.
        [DataField(typeof(ReleaseGroupFields), nameof(ReleaseGroupFields.Rating), false)]
        public ReleaseGroupRating? ReleaseGroupRatingId { get; }

        // How many searches found the group and when the last one did; a group typed by hand has not been seen.
        [DataField(typeof(ReleaseGroupFields), nameof(ReleaseGroupFields.Count))]
        public int SeenCount { get; }

        [DataField(typeof(ReleaseGroupFields), nameof(ReleaseGroupFields.LastSeenAt))]
        public DateTime? LastSeenAt { get; }

        // The media types whose searches saw the group, most seen first.
        [DataField(typeof(ReleaseGroupFields), nameof(ReleaseGroupFields.MediaKinds))]
        public AcquisitionMediaKind[] MediaKinds { get; }
    }

    // The figures of a media type's groups.
    public sealed class ReleaseGroupFiguresV1 : BaseResponse
    {
        public ReleaseGroupFiguresV1(int groupCount, int goodCount, int notRatedCount, int blockedCount)
        {
            GroupCount = groupCount;
            GoodCount = goodCount;
            NotRatedCount = notRatedCount;
            BlockedCount = blockedCount;
        }

        [DataField(typeof(ReleaseGroupFields), nameof(ReleaseGroupFields.Count))]
        public int GroupCount { get; }

        [DataField(typeof(ReleaseGroupFields), nameof(ReleaseGroupFields.Count))]
        public int GoodCount { get; }

        [DataField(typeof(ReleaseGroupFields), nameof(ReleaseGroupFields.Count))]
        public int NotRatedCount { get; }

        [DataField(typeof(ReleaseGroupFields), nameof(ReleaseGroupFields.Count))]
        public int BlockedCount { get; }
    }

    public sealed class ReleaseGroupScopeParametersV1 : BaseParameters
    {
    }

    public sealed class AddReleaseGroupDataV1 : BaseData
    {
        public AddReleaseGroupDataV1(string name)
        {
            Name = name;
        }

        [DataField(typeof(ReleaseGroupFields), nameof(ReleaseGroupFields.Name))]
        public string Name { get; }
    }

    public sealed class SavedReleaseGroupV1 : BaseResponse
    {
        public SavedReleaseGroupV1(long id)
        {
            Id = id;
        }

        [DataField(typeof(ReleaseGroupFields), nameof(ReleaseGroupFields.Id))]
        public long Id { get; }
    }

    public sealed class ReleaseGroupParametersV1 : BaseParameters
    {
        public ReleaseGroupParametersV1(long releaseGroupId)
        {
            ReleaseGroupId = releaseGroupId;
        }

        [DataField(typeof(ReleaseGroupFields), nameof(ReleaseGroupFields.Id))]
        public long ReleaseGroupId { get; }
    }

    public sealed class RateReleaseGroupDataV1 : BaseData
    {
        public RateReleaseGroupDataV1(ReleaseGroupRating? releaseGroupRatingId)
        {
            ReleaseGroupRatingId = releaseGroupRatingId;
        }

        // None takes the rating back.
        [DataField(typeof(ReleaseGroupFields), nameof(ReleaseGroupFields.Rating), false)]
        public ReleaseGroupRating? ReleaseGroupRatingId { get; }
    }
}
