using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Library;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Enums.Operation;
using Jularr.Backend.Data.Fields.Acquisition;
using Jularr.Backend.Data.Fields.Library;
using Jularr.Backend.Data.Fields.Live;
using Jularr.Backend.Data.Fields.Media;
using Jularr.Backend.Data.Fields.Operation;

namespace Jularr.Backend.Data.Admin.Activity.V1
{
    public sealed class ReadActivityParametersV1 : BaseParameters
    {
        public ReadActivityParametersV1(ActivityFilterType activityFilterTypeId = ActivityFilterType.All, int page = 1, int pageSize = 25)
        {
            ActivityFilterTypeId = activityFilterTypeId;
            Page = page;
            PageSize = pageSize;
        }

        [DataField(typeof(OperationFields), nameof(OperationFields.ActivityFilterTypeId))]
        public ActivityFilterType ActivityFilterTypeId { get; }

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
            string? reasonKey, DateTime createdAt, DateTime? finishedAt, string? connectionName, long? sizeBytes, long? remainingBytes, int? etaSeconds, DownloadStateType? downloadStateTypeId, string? manualUrl = null, bool canPause = false, bool canRetryImport = false, bool isWaiting = false, bool canImportManually = false, string? indexerName = null, long? bytesPerSecond = null, bool canBrowseFiles = false)
        {
            IndexerName = indexerName;
            BytesPerSecond = bytesPerSecond;
            CanPause = canPause;
            CanBrowseFiles = canBrowseFiles;
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

        // The indexer the release came from.
        [DataField(typeof(AcquisitionConnectionFields), nameof(AcquisitionConnectionFields.Name), false)]
        public string? IndexerName { get; }

        // The client's own pace: what is left over the time it still needs.
        [DataField(typeof(LiveFields), nameof(LiveFields.BytesPerSecond), false)]
        public long? BytesPerSecond { get; }

        // The source page where a person passes the check and copies the file link.
        [DataField(typeof(OperationFields), nameof(OperationFields.AttentionUrl), false)]
        public string? ManualUrl { get; }

        // SABnzbd can be told to wait while the download runs.
        [DataField(typeof(OperationFields), nameof(OperationFields.CanPause))]
        public bool CanPause { get; }

        // A qBittorrent job can expose its individual file progress in the Activity drawer.
        [DataField(typeof(OperationFields), nameof(OperationFields.CanPause))]
        public bool CanBrowseFiles { get; }

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

    public sealed class ReadActivitySummaryParametersV1 : BaseParameters
    {
    }

    // The queue in four numbers: what runs, the pace of the downloads, what is downloaded but not yet imported (waiting for a person or importing) and what failed unseen.
    public sealed class ActivitySummaryV1 : BaseResponse
    {
        public ActivitySummaryV1(int activeCount, long bytesPerSecond, int importCount, int errorCount, int allCount = 0, int waitingCount = 0, int unpackingCount = 0, int historyCount = 0)
        {
            AllCount = allCount;
            WaitingCount = waitingCount;
            UnpackingCount = unpackingCount;
            HistoryCount = historyCount;
            ActiveCount = activeCount;
            BytesPerSecond = bytesPerSecond;
            ImportCount = importCount;
            ErrorCount = errorCount;
        }

        [DataField(typeof(OperationFields), nameof(OperationFields.QueueCount))]
        public int ActiveCount { get; }

        [DataField(typeof(LiveFields), nameof(LiveFields.BytesPerSecond))]
        public long BytesPerSecond { get; }

        [DataField(typeof(OperationFields), nameof(OperationFields.QueueCount))]
        public int ImportCount { get; }

        [DataField(typeof(OperationFields), nameof(OperationFields.QueueCount))]
        public int ErrorCount { get; }

        // The filter chips of the queue: everything in it, what waits for a person, what the client unpacks and what has finished.
        [DataField(typeof(OperationFields), nameof(OperationFields.QueueCount))]
        public int AllCount { get; }

        [DataField(typeof(OperationFields), nameof(OperationFields.QueueCount))]
        public int WaitingCount { get; }

        [DataField(typeof(OperationFields), nameof(OperationFields.QueueCount))]
        public int UnpackingCount { get; }

        [DataField(typeof(OperationFields), nameof(OperationFields.QueueCount))]
        public int HistoryCount { get; }
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
        public ReadActivityEventsParametersV1(long? workId = null, int page = 1, int pageSize = 50, ActivityEventGroupType activityEventGroupTypeId = ActivityEventGroupType.All)
        {
            WorkId = workId;
            Page = page;
            PageSize = pageSize;
            ActivityEventGroupTypeId = activityEventGroupTypeId;
        }

        // One group of events (the history's chips), else all.
        [DataField(typeof(ActivityEventFields), nameof(ActivityEventFields.ActivityEventGroupTypeId))]
        public ActivityEventGroupType ActivityEventGroupTypeId { get; }

        // One Work's history, else everything.
        [DataField(typeof(WorkFields), nameof(WorkFields.Id), false)]
        public long? WorkId { get; }

        public int Page { get; }
        public int PageSize { get; }
    }

    // How many events each chip of the history holds, for one Work or all.
    public sealed class ActivityEventsSummaryV1 : BaseResponse
    {
        public ActivityEventsSummaryV1(int totalCount, int grabbedCount, int arrivedCount, int failedCount, int filesCount)
        {
            TotalCount = totalCount;
            GrabbedCount = grabbedCount;
            ArrivedCount = arrivedCount;
            FailedCount = failedCount;
            FilesCount = filesCount;
        }

        [DataField(typeof(ActivityEventFields), nameof(ActivityEventFields.EventCount))]
        public int TotalCount { get; }

        [DataField(typeof(ActivityEventFields), nameof(ActivityEventFields.EventCount))]
        public int GrabbedCount { get; }

        [DataField(typeof(ActivityEventFields), nameof(ActivityEventFields.EventCount))]
        public int ArrivedCount { get; }

        [DataField(typeof(ActivityEventFields), nameof(ActivityEventFields.EventCount))]
        public int FailedCount { get; }

        [DataField(typeof(ActivityEventFields), nameof(ActivityEventFields.EventCount))]
        public int FilesCount { get; }
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
