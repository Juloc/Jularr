using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Fields.Acquisition;
using Jularr.Backend.Data.Fields.Localization;
using Jularr.Backend.Data.Fields.Media;
using Jularr.Backend.Data.Fields.Profile;

namespace Jularr.Backend.Data.Admin.Request.V1
{
    public sealed class ReadRequestsParametersV1 : BaseParameters
    {
        public ReadRequestsParametersV1(int page = 1, int pageSize = 25, RequestTabType requestTabTypeId = RequestTabType.All)
        {
            Page = page;
            PageSize = pageSize;
            RequestTabTypeId = requestTabTypeId;
        }

        public int Page { get; }
        public int PageSize { get; }

        [DataField(typeof(AcquisitionRequestFields), nameof(AcquisitionRequestFields.RequestTabTypeId))]
        public RequestTabType RequestTabTypeId { get; }
    }

    public sealed class ReadRequestsSummaryParametersV1 : BaseParameters
    {
    }

    // The numbers of the request list's filter chips and figures: how many requests each tab holds.
    public sealed class RequestsSummaryV1 : BaseResponse
    {
        public RequestsSummaryV1(int totalCount, int openCount, int approvedCount, int inProgressCount, int doneCount, int rejectedCount)
        {
            TotalCount = totalCount;
            OpenCount = openCount;
            ApprovedCount = approvedCount;
            InProgressCount = inProgressCount;
            DoneCount = doneCount;
            RejectedCount = rejectedCount;
        }

        [DataField(typeof(AcquisitionRequestFields), nameof(AcquisitionRequestFields.RequestCount))]
        public int TotalCount { get; }

        [DataField(typeof(AcquisitionRequestFields), nameof(AcquisitionRequestFields.RequestCount))]
        public int OpenCount { get; }

        [DataField(typeof(AcquisitionRequestFields), nameof(AcquisitionRequestFields.RequestCount))]
        public int ApprovedCount { get; }

        [DataField(typeof(AcquisitionRequestFields), nameof(AcquisitionRequestFields.RequestCount))]
        public int InProgressCount { get; }

        [DataField(typeof(AcquisitionRequestFields), nameof(AcquisitionRequestFields.RequestCount))]
        public int DoneCount { get; }

        [DataField(typeof(AcquisitionRequestFields), nameof(AcquisitionRequestFields.RequestCount))]
        public int RejectedCount { get; }
    }

    public sealed class UpdateRequestParametersV1 : BaseParameters
    {
        public UpdateRequestParametersV1(long acquisitionRequestId)
        {
            AcquisitionRequestId = acquisitionRequestId;
        }

        [DataField(typeof(AcquisitionRequestFields), nameof(AcquisitionRequestFields.Id))]
        public long AcquisitionRequestId { get; }
    }

    public sealed class UpdateRequestDataV1 : BaseData
    {
        public UpdateRequestDataV1(AcquisitionRequestStatusType acquisitionRequestStatusTypeId, long version)
        {
            AcquisitionRequestStatusTypeId = acquisitionRequestStatusTypeId;
            Version = version;
        }

        [DataField(typeof(AcquisitionRequestFields), nameof(AcquisitionRequestFields.AcquisitionRequestStatusTypeId))]
        public AcquisitionRequestStatusType AcquisitionRequestStatusTypeId { get; }

        [DataField(typeof(AcquisitionRequestFields), nameof(AcquisitionRequestFields.Version))]
        public long Version { get; }
    }

    public sealed class RequestDetailsV1 : BaseResponse
    {
        public RequestDetailsV1(long id, long workId, string canonicalTitle, MediaType mediaTypeId, string requesterName, string requestedLanguage,
            AcquisitionRequestStatusType acquisitionRequestStatusTypeId, long version, DateTime createdAt)
        {
            Id = id;
            WorkId = workId;
            CanonicalTitle = canonicalTitle;
            MediaTypeId = mediaTypeId;
            RequesterName = requesterName;
            RequestedLanguage = requestedLanguage;
            AcquisitionRequestStatusTypeId = acquisitionRequestStatusTypeId;
            Version = version;
            CreatedAt = createdAt;
        }

        [DataField(typeof(AcquisitionRequestFields), nameof(AcquisitionRequestFields.Id))]
        public long Id { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.Id))]
        public long WorkId { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.CanonicalTitle))]
        public string CanonicalTitle { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.MediaTypeId))]
        public MediaType MediaTypeId { get; }

        [DataField(typeof(ProfileFields), nameof(ProfileFields.DisplayName))]
        public string RequesterName { get; }

        [DataField(typeof(UiLocaleFields), nameof(UiLocaleFields.Name))]
        public string RequestedLanguage { get; }

        [DataField(typeof(AcquisitionRequestFields), nameof(AcquisitionRequestFields.AcquisitionRequestStatusTypeId))]
        public AcquisitionRequestStatusType AcquisitionRequestStatusTypeId { get; }

        [DataField(typeof(AcquisitionRequestFields), nameof(AcquisitionRequestFields.Version))]
        public long Version { get; }

        [DataField(typeof(AcquisitionRequestFields), nameof(AcquisitionRequestFields.CreatedAt))]
        public DateTime CreatedAt { get; }
    }
}
