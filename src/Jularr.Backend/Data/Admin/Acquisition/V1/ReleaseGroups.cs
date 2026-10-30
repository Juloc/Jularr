using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Fields.Acquisition;
using Jularr.Backend.Data.Fields.Media;

namespace Jularr.Backend.Data.Admin.Acquisition.V1
{
    public sealed class ReadReleaseGroupsParametersV1 : BaseParameters
    {
        public ReadReleaseGroupsParametersV1(MediaType mediaTypeId, string? search = null, int page = 1, int pageSize = 25)
        {
            MediaTypeId = mediaTypeId;
            Search = search;
            Page = page;
            PageSize = pageSize;
        }

        [DataField(typeof(WorkFields), nameof(WorkFields.MediaTypeId))]
        public MediaType MediaTypeId { get; }

        // Part of a group's name, in any case.
        [DataField(typeof(ReleaseGroupFields), nameof(ReleaseGroupFields.Search))]
        public string? Search { get; }

        public int Page { get; }

        public int PageSize { get; }
    }

    public sealed class ReleaseGroupDetailsV1 : BaseResponse
    {
        public ReleaseGroupDetailsV1(long id, MediaType mediaTypeId, string name, ReleaseGroupRating? releaseGroupRatingId, int seenCount, DateTime? lastSeenAt)
        {
            Id = id;
            MediaTypeId = mediaTypeId;
            Name = name;
            ReleaseGroupRatingId = releaseGroupRatingId;
            SeenCount = seenCount;
            LastSeenAt = lastSeenAt;
        }

        [DataField(typeof(ReleaseGroupFields), nameof(ReleaseGroupFields.Id))]
        public long Id { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.MediaTypeId))]
        public MediaType MediaTypeId { get; }

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
        public ReleaseGroupScopeParametersV1(MediaType mediaTypeId)
        {
            MediaTypeId = mediaTypeId;
        }

        [DataField(typeof(WorkFields), nameof(WorkFields.MediaTypeId))]
        public MediaType MediaTypeId { get; }
    }

    public sealed class AddReleaseGroupDataV1 : BaseData
    {
        public AddReleaseGroupDataV1(MediaType mediaTypeId, string name)
        {
            MediaTypeId = mediaTypeId;
            Name = name;
        }

        [DataField(typeof(WorkFields), nameof(WorkFields.MediaTypeId))]
        public MediaType MediaTypeId { get; }

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
