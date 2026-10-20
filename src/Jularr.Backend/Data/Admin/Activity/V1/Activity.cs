using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Library;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Enums.Operation;
using Jularr.Backend.Data.Fields.Acquisition;
using Jularr.Backend.Data.Fields.Library;
using Jularr.Backend.Data.Fields.Media;
using Jularr.Backend.Data.Fields.Operation;

namespace Jularr.Backend.Data.Admin.Activity.V1
{
    public sealed class ReadActivityParametersV1 : BaseParameters
    {
        public ReadActivityParametersV1(ActivityTabType activityTabTypeId = ActivityTabType.Queue, int page = 1, int pageSize = 25)
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
            string? reasonKey, DateTime createdAt, DateTime? finishedAt, string? connectionName, long? sizeBytes, long? remainingBytes, int? etaSeconds, DownloadStateType? downloadStateTypeId, string? manualUrl = null, bool canPause = false, bool canRetryImport = false, bool isWaiting = false, bool canImportManually = false)
        {
            CanPause = canPause;
            CanRetryImport = canRetryImport;
            IsWaiting = isWaiting;
            CanImportManually = canImportManually;
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

        // SABnzbd can be told to wait while the download runs.
        [DataField(typeof(OperationFields), nameof(OperationFields.CanPause))]
        public bool CanPause { get; }

        // A waiting download is read again on request.
        [DataField(typeof(OperationFields), nameof(OperationFields.CanRetryImport))]
        public bool CanRetryImport { get; }

        // It waits for a person, not for the client.
        [DataField(typeof(OperationFields), nameof(OperationFields.IsWaiting))]
        public bool IsWaiting { get; }

        // It kept where its files are, so a person can import them by hand.
        [DataField(typeof(OperationFields), nameof(OperationFields.CanImportManually))]
        public bool CanImportManually { get; }
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

    public sealed class ReadActivityEventsParametersV1 : BaseParameters
    {
        public ReadActivityEventsParametersV1(long? workId = null, int page = 1, int pageSize = 50)
        {
            WorkId = workId;
            Page = page;
            PageSize = pageSize;
        }

        // One Work's history, else everything.
        [DataField(typeof(WorkFields), nameof(WorkFields.Id), false)]
        public long? WorkId { get; }

        public int Page { get; }
        public int PageSize { get; }
    }

    // What happened, with the texts it kept: the release and its quality, the indexer and client, the file's place and who did it.
    public sealed class ActivityEventV1 : BaseResponse
    {
        public ActivityEventV1(long id, ActivityEventType activityEventTypeId, DateTime occurredAt, long? workId, string workTitle, WorkUnitType? workUnitTypeId, decimal? parentUnitNumber,
            decimal? unitNumber, string? releaseTitle, string? qualityKey, string? indexerName, string? clientName, string? path, string? previousPath, string? reasonKey, string? accountName)
        {
            Id = id;
            ActivityEventTypeId = activityEventTypeId;
            OccurredAt = occurredAt;
            WorkId = workId;
            WorkTitle = workTitle;
            WorkUnitTypeId = workUnitTypeId;
            ParentUnitNumber = parentUnitNumber;
            UnitNumber = unitNumber;
            ReleaseTitle = releaseTitle;
            QualityKey = qualityKey;
            IndexerName = indexerName;
            ClientName = clientName;
            Path = path;
            PreviousPath = previousPath;
            ReasonKey = reasonKey;
            AccountName = accountName;
        }

        [DataField(typeof(ActivityEventFields), nameof(ActivityEventFields.Id))]
        public long Id { get; }

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

        // The Work while it exists.
        [DataField(typeof(WorkFields), nameof(WorkFields.Id), false)]
        public long? WorkId { get; }

        [DataField(typeof(ActivityEventFields), nameof(ActivityEventFields.WorkTitle))]
        public string WorkTitle { get; }

        [DataField(typeof(ActivityEventFields), nameof(ActivityEventFields.ReleaseTitle))]
        public string? ReleaseTitle { get; }

        [DataField(typeof(ActivityEventFields), nameof(ActivityEventFields.ConnectionName))]
        public string? IndexerName { get; }

        [DataField(typeof(ActivityEventFields), nameof(ActivityEventFields.ConnectionName))]
        public string? ClientName { get; }

        [DataField(typeof(ActivityEventFields), nameof(ActivityEventFields.Path))]
        public string? Path { get; }

        [DataField(typeof(ActivityEventFields), nameof(ActivityEventFields.Path))]
        public string? PreviousPath { get; }

        [DataField(typeof(ActivityEventFields), nameof(ActivityEventFields.ReasonKey))]
        public string? ReasonKey { get; }

        // Who did it; none is Jularr itself.
        [DataField(typeof(ActivityEventFields), nameof(ActivityEventFields.AccountName))]
        public string? AccountName { get; }
    }

    // Out of the queue; the release may be blocked so that the target is searched again at once for another.
    public sealed class RemoveActivityDownloadDataV1 : BaseData
    {
        public RemoveActivityDownloadDataV1(bool blocksRelease)
        {
            BlocksRelease = blocksRelease;
        }

        [DataField(typeof(OperationFields), nameof(OperationFields.BlocksRelease))]
        public bool BlocksRelease { get; }
    }

    public sealed class PauseActivityQueueParametersV1 : BaseParameters
    {
    }

    public sealed class PauseActivityDownloadDataV1 : BaseData
    {
        public PauseActivityDownloadDataV1(bool isPaused)
        {
            IsPaused = isPaused;
        }

        [DataField(typeof(OperationFields), nameof(OperationFields.IsPaused))]
        public bool IsPaused { get; }
    }
}
