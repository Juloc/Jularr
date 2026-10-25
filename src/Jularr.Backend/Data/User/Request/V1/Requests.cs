using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Enums.Library;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Fields.Acquisition;
using Jularr.Backend.Data.Fields.Library;
using Jularr.Backend.Data.Fields.Localization;
using Jularr.Backend.Data.Fields.Media;
using Jularr.Backend.Data.Fields.Operation;

namespace Jularr.Backend.Data.User.Request.V1
{
    public sealed class ReadRequestsParametersV1 : BaseParameters
    {
        public ReadRequestsParametersV1(int page = 1, int pageSize = 25, RequestListFilterType requestListFilterTypeId = RequestListFilterType.All, RequestSortType requestSortTypeId = RequestSortType.Newest,
            MediaType? mediaTypeId = null, MediaSubtype? mediaSubtypeId = null)
        {
            Page = page;
            PageSize = pageSize;
            RequestListFilterTypeId = requestListFilterTypeId;
            RequestSortTypeId = requestSortTypeId;
            MediaTypeId = mediaTypeId;
            MediaSubtypeId = mediaSubtypeId;
        }

        public int Page { get; }
        public int PageSize { get; }

        [DataField(typeof(AcquisitionRequestFields), nameof(AcquisitionRequestFields.RequestListFilterTypeId))]
        public RequestListFilterType RequestListFilterTypeId { get; }

        [DataField(typeof(AcquisitionRequestFields), nameof(AcquisitionRequestFields.RequestSortTypeId))]
        public RequestSortType RequestSortTypeId { get; }

        // A type alone lists its Works without subtype; a subtype lists that subtype across types.
        [DataField(typeof(WorkFields), nameof(WorkFields.MediaTypeId), false)]
        public MediaType? MediaTypeId { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.MediaSubtypeId), false)]
        public MediaSubtype? MediaSubtypeId { get; }
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
        public CreateRequestDataV1(long uiLocaleId, long? gamePlatformId = null, long? acquisitionProfileId = null, int? startSeasonNumber = null)
        {
            UiLocaleId = uiLocaleId;
            GamePlatformId = gamePlatformId;
            AcquisitionProfileId = acquisitionProfileId;
            StartSeasonNumber = startSeasonNumber;
        }

        [DataField(typeof(UiLocaleFields), nameof(UiLocaleFields.Id))]
        public long UiLocaleId { get; }

        // Games are requested for one of their platforms.
        [DataField(typeof(GamePlatformFields), nameof(GamePlatformFields.Id), false)]
        public long? GamePlatformId { get; }

        // The starting TVDB season for this request; null requests all seasons.
        [DataField(typeof(AcquisitionRequestFields), nameof(AcquisitionRequestFields.StartSeasonNumber), false)]
        public int? StartSeasonNumber { get; }

        // One of the qualities the request rule offers for the Work.
        [DataField(typeof(AcquisitionProfileFields), nameof(AcquisitionProfileFields.Id), false)]
        public long? AcquisitionProfileId { get; }
    }

    public sealed class RequestProfileOptionV1 : BaseResponse
    {
        public RequestProfileOptionV1(long id, string name)
        {
            Id = id;
            Name = name;
        }

        [DataField(typeof(AcquisitionProfileFields), nameof(AcquisitionProfileFields.Id))]
        public long Id { get; }

        [DataField(typeof(AcquisitionProfileFields), nameof(AcquisitionProfileFields.Name))]
        public string Name { get; }
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
            long? operationId = null, string? manualUrl = null, ActivityEventType? latestStepTypeId = null, DateTime? latestStepAt = null, bool readsTranslated = false,
            MediaType mediaTypeId = MediaType.Movie, MediaSubtype? mediaSubtypeId = null, string? posterUrl = null, string? qualityName = null, bool isWithdrawable = false)
        {
            MediaTypeId = mediaTypeId;
            MediaSubtypeId = mediaSubtypeId;
            PosterUrl = posterUrl;
            QualityName = qualityName;
            IsWithdrawable = isWithdrawable;
            ReadsTranslated = readsTranslated;
            LatestStepTypeId = latestStepTypeId;
            LatestStepAt = latestStepAt;
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

        // The last step since the request: grabbed, failed, imported or upgraded.
        [DataField(typeof(ActivityEventFields), nameof(ActivityEventFields.ActivityEventTypeId), false)]
        public ActivityEventType? LatestStepTypeId { get; }

        [DataField(typeof(ActivityEventFields), nameof(ActivityEventFields.OccurredAt), false)]
        public DateTime? LatestStepAt { get; }

        // A book not published in the requested language comes in its own and is read translated.
        public bool ReadsTranslated { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.MediaTypeId))]
        public MediaType MediaTypeId { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.MediaSubtypeId), false)]
        public MediaSubtype? MediaSubtypeId { get; }

        [DataField(typeof(WorkMetadataFields), nameof(WorkMetadataFields.ImageUrl))]
        public string? PosterUrl { get; }

        // The quality the request asked for; without a choice the defaults apply.
        [DataField(typeof(AcquisitionProfileFields), nameof(AcquisitionProfileFields.Name), false)]
        public string? QualityName { get; }

        // Until it is imported the requester can take the request back.
        public bool IsWithdrawable { get; }
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

    // A step of a request as its requester sees it.
    public sealed class RequestStepV1 : BaseResponse
    {
        public RequestStepV1(ActivityEventType activityEventTypeId, DateTime occurredAt, WorkUnitType? workUnitTypeId, decimal? parentUnitNumber, decimal? unitNumber, string? qualityKey)
        {
            ActivityEventTypeId = activityEventTypeId;
            OccurredAt = occurredAt;
            WorkUnitTypeId = workUnitTypeId;
            ParentUnitNumber = parentUnitNumber;
            UnitNumber = unitNumber;
            QualityKey = qualityKey;
        }

        [DataField(typeof(ActivityEventFields), nameof(ActivityEventFields.ActivityEventTypeId))]
        public ActivityEventType ActivityEventTypeId { get; }

        [DataField(typeof(ActivityEventFields), nameof(ActivityEventFields.OccurredAt))]
        public DateTime OccurredAt { get; }

        // The episode, volume or track, where the event names one.
        [DataField(typeof(ActivityEventFields), nameof(ActivityEventFields.WorkUnitTypeId))]
        public WorkUnitType? WorkUnitTypeId { get; }

        [DataField(typeof(ActivityEventFields), nameof(ActivityEventFields.UnitNumber))]
        public decimal? ParentUnitNumber { get; }

        [DataField(typeof(ActivityEventFields), nameof(ActivityEventFields.UnitNumber))]
        public decimal? UnitNumber { get; }

        [DataField(typeof(ActivityEventFields), nameof(ActivityEventFields.QualityKey))]
        public string? QualityKey { get; }
    }
}
