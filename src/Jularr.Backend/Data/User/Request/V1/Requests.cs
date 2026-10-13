using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Fields.Acquisition;
using Jularr.Backend.Data.Fields.Localization;
using Jularr.Backend.Data.Fields.Media;
using Jularr.Backend.Data.Fields.Operation;

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
        public CreateRequestDataV1(long uiLocaleId, long? gamePlatformId = null)
        {
            UiLocaleId = uiLocaleId;
            GamePlatformId = gamePlatformId;
        }

        [DataField(typeof(UiLocaleFields), nameof(UiLocaleFields.Id))]
        public long UiLocaleId { get; }

        // Games are requested for one of their platforms.
        [DataField(typeof(GamePlatformFields), nameof(GamePlatformFields.Id), false)]
        public long? GamePlatformId { get; }
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
        public RequestDetailsV1(long id, long workId, string canonicalTitle, string requestedLanguage, AcquisitionRequestStatusType acquisitionRequestStatusTypeId, DateTime createdAt,
            long? operationId = null, string? manualUrl = null)
        {
            OperationId = operationId;
            ManualUrl = manualUrl;
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

        // A download of this request waits for the user to pass the source's check.
        [DataField(typeof(OperationFields), nameof(OperationFields.Id), false)]
        public long? OperationId { get; }

        [DataField(typeof(OperationFields), nameof(OperationFields.AttentionUrl), false)]
        public string? ManualUrl { get; }
    }

    public sealed class RequestParametersV1 : BaseParameters
    {
        public RequestParametersV1(long acquisitionRequestId)
        {
            AcquisitionRequestId = acquisitionRequestId;
        }

        [DataField(typeof(AcquisitionRequestFields), nameof(AcquisitionRequestFields.Id))]
        public long AcquisitionRequestId { get; }
    }

    public sealed class SaveRequestSourceLinkDataV1 : BaseData
    {
        public SaveRequestSourceLinkDataV1(long operationId, string sourceLinkUrl)
        {
            OperationId = operationId;
            SourceLinkUrl = sourceLinkUrl;
        }

        [DataField(typeof(OperationFields), nameof(OperationFields.Id))]
        public long OperationId { get; }

        [DataField(typeof(OperationFields), nameof(OperationFields.SourceLinkUrl), true)]
        public string SourceLinkUrl { get; }
    }
}
