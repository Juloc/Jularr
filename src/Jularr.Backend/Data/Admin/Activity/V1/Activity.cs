using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Operation;
using Jularr.Backend.Data.Fields.Acquisition;
using Jularr.Backend.Data.Fields.Media;
using Jularr.Backend.Data.Fields.Operation;

namespace Jularr.Backend.Data.Admin.Activity.V1
{
    public sealed class ReadActivityParametersV1 : BaseParameters
    {
        public ReadActivityParametersV1(ActivityTabType activityTabTypeId = ActivityTabType.Activity, int page = 1, int pageSize = 25)
        {
            ActivityTabTypeId = activityTabTypeId;
            Page = page;
            PageSize = pageSize;
        }

        [DataField(typeof(OperationFields), nameof(OperationFields.ActivityTabTypeId))]
        public ActivityTabType ActivityTabTypeId { get; }

        public int Page { get; }
        public int PageSize { get; }
    }

    public sealed class ActivityOperationParametersV1 : BaseParameters
    {
        public ActivityOperationParametersV1(long operationId)
        {
            OperationId = operationId;
        }

        [DataField(typeof(OperationFields), nameof(OperationFields.Id))]
        public long OperationId { get; }
    }

    public sealed class ActivityItemV1 : BaseResponse
    {
        public ActivityItemV1(long id, OperationType operationTypeId, OperationStatusType operationStatusTypeId, string title, long? workId, string? workTitle, int? progressPercent,
            string? reasonKey, DateTime createdAt, DateTime? finishedAt, string? connectionName, long? sizeBytes, long? remainingBytes, int? etaSeconds, DownloadStateType? downloadStateTypeId, string? manualUrl = null)
        {
            ManualUrl = manualUrl;
            Id = id;
            OperationTypeId = operationTypeId;
            OperationStatusTypeId = operationStatusTypeId;
            Title = title;
            WorkId = workId;
            WorkTitle = workTitle;
            ProgressPercent = progressPercent;
            ReasonKey = reasonKey;
            CreatedAt = createdAt;
            FinishedAt = finishedAt;
            ConnectionName = connectionName;
            SizeBytes = sizeBytes;
            RemainingBytes = remainingBytes;
            EtaSeconds = etaSeconds;
            DownloadStateTypeId = downloadStateTypeId;
        }

        [DataField(typeof(OperationFields), nameof(OperationFields.Id))]
        public long Id { get; }

        [DataField(typeof(OperationFields), nameof(OperationFields.OperationTypeId))]
        public OperationType OperationTypeId { get; }

        [DataField(typeof(OperationFields), nameof(OperationFields.OperationStatusTypeId))]
        public OperationStatusType OperationStatusTypeId { get; }

        [DataField(typeof(OperationFields), nameof(OperationFields.Title))]
        public string Title { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.Id), false)]
        public long? WorkId { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.CanonicalTitle), false)]
        public string? WorkTitle { get; }

        [DataField(typeof(OperationFields), nameof(OperationFields.ProgressPercent))]
        public int? ProgressPercent { get; }

        [DataField(typeof(OperationFields), nameof(OperationFields.ErrorKey))]
        public string? ReasonKey { get; }

        [DataField(typeof(OperationFields), nameof(OperationFields.CreatedAt))]
        public DateTime CreatedAt { get; }

        [DataField(typeof(OperationFields), nameof(OperationFields.FinishedAt))]
        public DateTime? FinishedAt { get; }

        [DataField(typeof(AcquisitionConnectionFields), nameof(AcquisitionConnectionFields.Name), false)]
        public string? ConnectionName { get; }

        [DataField(typeof(OperationFields), nameof(OperationFields.SizeBytes))]
        public long? SizeBytes { get; }

        [DataField(typeof(OperationFields), nameof(OperationFields.RemainingBytes))]
        public long? RemainingBytes { get; }

        [DataField(typeof(OperationFields), nameof(OperationFields.EtaSeconds))]
        public int? EtaSeconds { get; }

        [DataField(typeof(OperationFields), nameof(OperationFields.DownloadStateTypeId), false)]
        public DownloadStateType? DownloadStateTypeId { get; }

        // The source page where a person passes the check and copies the file link.
        [DataField(typeof(OperationFields), nameof(OperationFields.AttentionUrl), false)]
        public string? ManualUrl { get; }
    }

    public sealed class SaveActivitySourceLinkDataV1 : BaseData
    {
        public SaveActivitySourceLinkDataV1(string sourceLinkUrl)
        {
            SourceLinkUrl = sourceLinkUrl;
        }

        [DataField(typeof(OperationFields), nameof(OperationFields.SourceLinkUrl), true)]
        public string SourceLinkUrl { get; }
    }
}
