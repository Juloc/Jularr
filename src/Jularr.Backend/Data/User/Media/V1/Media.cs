using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Fields.Media;

namespace Jularr.Backend.Data.User.Media.V1
{
    public sealed class ReadWatchlistParametersV1 : BaseParameters
    {
        public ReadWatchlistParametersV1(int page = 1, int pageSize = 25)
        {
            Page = page;
            PageSize = pageSize;
        }

        public int Page { get; }
        public int PageSize { get; }
    }

    public sealed class WorkParametersV1 : BaseParameters
    {
        public WorkParametersV1(long workId)
        {
            WorkId = workId;
        }

        [DataField(typeof(WorkFields), nameof(WorkFields.Id))]
        public long WorkId { get; }
    }

    public sealed class WorkDetailsV1 : BaseResponse
    {
        public WorkDetailsV1(long id, MediaType mediaTypeId, string canonicalTitle, bool isWatchlisted)
        {
            Id = id;
            MediaTypeId = mediaTypeId;
            CanonicalTitle = canonicalTitle;
            IsWatchlisted = isWatchlisted;
        }

        [DataField(typeof(WorkFields), nameof(WorkFields.Id))]
        public long Id { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.MediaTypeId))]
        public MediaType MediaTypeId { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.CanonicalTitle))]
        public string CanonicalTitle { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.IsWatchlisted))]
        public bool IsWatchlisted { get; }
    }

    public sealed class UpdateWatchlistDataV1 : BaseData
    {
        public UpdateWatchlistDataV1(bool isWatchlisted)
        {
            IsWatchlisted = isWatchlisted;
        }

        [DataField(typeof(WorkFields), nameof(WorkFields.IsWatchlisted))]
        public bool IsWatchlisted { get; }
    }
}
