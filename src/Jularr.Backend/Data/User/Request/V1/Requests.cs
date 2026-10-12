using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Fields.Acquisition;
using Jularr.Backend.Data.Fields.Localization;
using Jularr.Backend.Data.Fields.Media;

namespace Jularr.Backend.Data.User.Request.V1
{
    public sealed class ReadRequestsParametersV1 : BaseParameters
    {
        public ReadRequestsParametersV1(int page = 1, int pageSize = 25)
        {
            Page = page;
            PageSize = pageSize;
        }

        public int Page { get; }
        public int PageSize { get; }
    }

    public sealed class CreateRequestParametersV1 : BaseParameters
    {
        public CreateRequestParametersV1(long workId)
        {
            WorkId = workId;
        }

        [DataField(typeof(WorkFields), nameof(WorkFields.Id))]
        public long WorkId { get; }
    }

    public sealed class CreateRequestDataV1 : BaseData
    {
        public CreateRequestDataV1(long uiLocaleId)
        {
            UiLocaleId = uiLocaleId;
        }

        [DataField(typeof(UiLocaleFields), nameof(UiLocaleFields.Id))]
        public long UiLocaleId { get; }
    }

    public sealed class CreateRequestResultV1 : BaseResponse
    {
        public CreateRequestResultV1(long id)
        {
            Id = id;
        }

        [DataField(typeof(AcquisitionRequestFields), nameof(AcquisitionRequestFields.Id))]
        public long Id { get; }
    }

    public sealed class RequestDetailsV1 : BaseResponse
    {
        public RequestDetailsV1(long id, long workId, string canonicalTitle, string requestedLanguage, AcquisitionRequestStatusType acquisitionRequestStatusTypeId, DateTime createdAt)
        {
            Id = id;
            WorkId = workId;
            CanonicalTitle = canonicalTitle;
            RequestedLanguage = requestedLanguage;
            AcquisitionRequestStatusTypeId = acquisitionRequestStatusTypeId;
            CreatedAt = createdAt;
        }

        [DataField(typeof(AcquisitionRequestFields), nameof(AcquisitionRequestFields.Id))]
        public long Id { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.Id))]
        public long WorkId { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.CanonicalTitle))]
        public string CanonicalTitle { get; }

        [DataField(typeof(UiLocaleFields), nameof(UiLocaleFields.Name))]
        public string RequestedLanguage { get; }

        [DataField(typeof(AcquisitionRequestFields), nameof(AcquisitionRequestFields.AcquisitionRequestStatusTypeId))]
        public AcquisitionRequestStatusType AcquisitionRequestStatusTypeId { get; }

        [DataField(typeof(AcquisitionRequestFields), nameof(AcquisitionRequestFields.CreatedAt))]
        public DateTime CreatedAt { get; }
    }
}
