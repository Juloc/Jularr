using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Enums.Library;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Enums.Operation;
using Jularr.Backend.Data.Fields.Acquisition;
using Jularr.Backend.Data.Fields.Library;
using Jularr.Backend.Data.Fields.Live;
using Jularr.Backend.Data.Fields.Media;
using Jularr.Backend.Data.Fields.Operation;
using Jularr.Backend.Data.Fields.Storage;
using Jularr.Backend.Logic.Acquisition;

namespace Jularr.Backend.Data.Admin.Activity.V1
{
    public sealed class ReadActivityParametersV1 : BaseParameters
    {
        public ReadActivityParametersV1(ActivityFilterType activityFilterTypeId = ActivityFilterType.All, int page = 1, int pageSize = 25, bool torrentOnly = false, bool downloadsOnly = false,
            bool failedOnly = false, DownloadClientKind? downloadClientKindId = null, long? acquisitionConnectionId = null, MediaType? mediaTypeId = null,
            DownloadPriorityType? downloadPriorityTypeId = null, MediaType[]? mediaTypeIds = null, MediaSubtype[]? mediaSubtypeIds = null, bool tasksOnly = false, bool queueOnly = false)
        {
            MediaTypeIds = mediaTypeIds ?? [];
            MediaSubtypeIds = mediaSubtypeIds ?? [];
            DownloadPriorityTypeId = downloadPriorityTypeId;
            ActivityFilterTypeId = activityFilterTypeId;
            TorrentOnly = torrentOnly;
            DownloadsOnly = downloadsOnly;
            TasksOnly = tasksOnly;
            QueueOnly = queueOnly;
            FailedOnly = failedOnly;
            DownloadClientKindId = downloadClientKindId;
            AcquisitionConnectionId = acquisitionConnectionId;
            MediaTypeId = mediaTypeId;
            Page = page;
            PageSize = pageSize;
        }

        [DataField(typeof(OperationFields), nameof(OperationFields.ActivityFilterTypeId))]
        public ActivityFilterType ActivityFilterTypeId { get; }

        [DataField(typeof(OperationFields), nameof(OperationFields.TorrentOnly))]
        public bool TorrentOnly { get; }

        // Only downloads, as the Downloads page lists them: with All what is still open, with History every finished one.
        [DataField(typeof(OperationFields), nameof(OperationFields.DownloadsOnly))]
        public bool DownloadsOnly { get; }

        [DataField(typeof(OperationFields), nameof(OperationFields.FailedOnly))]
        public bool FailedOnly { get; }

        // Only Jularr's own tasks, as the Jobs entry's Tasks lists them: scans, backups, media imports, searches and the notices of failed background jobs, no downloads.
        [DataField(typeof(OperationFields), nameof(OperationFields.TasksOnly))]
        public bool TasksOnly { get; }

        // The Queue, as Sonarr's: the open downloads and a grab that waits for its indexer's pause; a failed download is the History's.
        [DataField(typeof(OperationFields), nameof(OperationFields.QueueOnly))]
        public bool QueueOnly { get; }

        // One client's downloads: a kind, and for SABnzbd and qBittorrent the connection.
        [DataField(typeof(OperationFields), nameof(OperationFields.DownloadClientKindId), false)]
        public DownloadClientKind? DownloadClientKindId { get; }

        [DataField(typeof(AcquisitionConnectionFields), nameof(AcquisitionConnectionFields.Id), false)]
        public long? AcquisitionConnectionId { get; }

        // The category: the media type of the downloaded title.
        [DataField(typeof(WorkFields), nameof(WorkFields.MediaTypeId), false)]
        public MediaType? MediaTypeId { get; }

        [DataField(typeof(OperationFields), nameof(OperationFields.DownloadPriorityTypeId), false)]
        public DownloadPriorityType? DownloadPriorityTypeId { get; }

        // The type bar's choice: types list their titles without subtype and subtypes list theirs across types; none lets every entry through.
        [DataField(typeof(WorkFields), nameof(WorkFields.MediaTypeIds))]
        public MediaType[] MediaTypeIds { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.MediaSubtypeIds))]
        public MediaSubtype[] MediaSubtypeIds { get; }

        public int Page { get; }
        public int PageSize { get; }
    }

    // The blocked releases of every target, as the type bar chooses them; a person lifts a block through the target's release service.
    public sealed class ReadBlockedReleasesParametersV1 : BaseParameters
    {
        public ReadBlockedReleasesParametersV1(int page = 1, int pageSize = 25, MediaType[]? mediaTypeIds = null, MediaSubtype[]? mediaSubtypeIds = null)
        {
            MediaTypeIds = mediaTypeIds ?? [];
            MediaSubtypeIds = mediaSubtypeIds ?? [];
            Page = page;
            PageSize = pageSize;
        }

        [DataField(typeof(WorkFields), nameof(WorkFields.MediaTypeIds))]
        public MediaType[] MediaTypeIds { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.MediaSubtypeIds))]
        public MediaSubtype[] MediaSubtypeIds { get; }

        public int Page { get; }
        public int PageSize { get; }
    }

    // A release that is not taken again: blocked by hand or by a fault that blocks like it, with its target and why.
    public sealed class BlockedReleaseV1 : BaseResponse
    {
        public BlockedReleaseV1(long id, long wantedItemId, long? workId, string? workTitle, MediaType? mediaTypeId, string title, string? connectionName, string? qualityKey, string? reasonKey, DateTime blockedAt)
        {
            Id = id;
            WantedItemId = wantedItemId;
            WorkId = workId;
            WorkTitle = workTitle;
            MediaTypeId = mediaTypeId;
            Title = title;
            ConnectionName = connectionName;
            QualityKey = qualityKey;
            ReasonKey = reasonKey;
            BlockedAt = blockedAt;
        }

        [DataField(typeof(AcquisitionReleaseFields), nameof(AcquisitionReleaseFields.Id))]
        public long Id { get; }

        [DataField(typeof(WantedItemFields), nameof(WantedItemFields.Id))]
        public long WantedItemId { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.Id), false)]
        public long? WorkId { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.CanonicalTitle), false)]
        public string? WorkTitle { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.MediaTypeId), false)]
        public MediaType? MediaTypeId { get; }

        [DataField(typeof(AcquisitionReleaseFields), nameof(AcquisitionReleaseFields.Title))]
        public string Title { get; }

        // The indexer the release came from.
        [DataField(typeof(AcquisitionConnectionFields), nameof(AcquisitionConnectionFields.Name), false)]
        public string? ConnectionName { get; }

        [DataField(typeof(AcquisitionReleaseFields), nameof(AcquisitionReleaseFields.QualityKey))]
        public string? QualityKey { get; }

        [DataField(typeof(AcquisitionReleaseFields), nameof(AcquisitionReleaseFields.ReasonKey))]
        public string? ReasonKey { get; }

        [DataField(typeof(AcquisitionReleaseFields), nameof(AcquisitionReleaseFields.UpdatedAt))]
        public DateTime BlockedAt { get; }
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
            string? reasonKey, DateTime createdAt, DateTime? finishedAt, string? connectionName, long? sizeBytes, long? remainingBytes, int? etaSeconds, DownloadStateType? downloadStateTypeId, string? manualUrl = null, bool canPause = false, bool canRetryImport = false, bool isWaiting = false, bool canImportManually = false, string? indexerName = null, long? bytesPerSecond = null, bool canBrowseFiles = false, long? sabnzbdConnectionId = null,
            long? acquisitionConnectionId = null, DownloadClientKind? downloadClientKindId = null, MediaType? mediaTypeId = null, bool canRetry = false,
            DownloadPriorityType downloadPriorityTypeId = DownloadPriorityType.Normal, string[]? unmatchedFiles = null, string[]? unmatchedFileReasons = null,
            string? qualityKey = null, int? parentUnitNumber = null, int? unitStart = null, int? unitEnd = null, string? unitTitle = null, DateTime? waitUntil = null)
        {
            QualityKey = qualityKey;
            ParentUnitNumber = parentUnitNumber;
            UnitStart = unitStart;
            UnitEnd = unitEnd;
            UnitTitle = unitTitle;
            WaitUntil = waitUntil;
            Languages = operationTypeId == OperationType.Download ? ReleaseWords.ReadLanguages(title) : [];
            UnmatchedFiles = unmatchedFiles;
            UnmatchedFileReasons = unmatchedFileReasons;
            DownloadPriorityTypeId = downloadPriorityTypeId;
            CanRetry = canRetry;
            AcquisitionConnectionId = acquisitionConnectionId;
            DownloadClientKindId = downloadClientKindId;
            MediaTypeId = mediaTypeId;
            IndexerName = indexerName;
            BytesPerSecond = bytesPerSecond;
            CanPause = canPause;
            CanBrowseFiles = canBrowseFiles;
            SabnzbdConnectionId = sabnzbdConnectionId;
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

        // A torrent shows individual file progress; SABnzbd opens its guarded folder browser.
        [DataField(typeof(OperationFields), nameof(OperationFields.CanPause))]
        public bool CanBrowseFiles { get; }

        // Only the SABnzbd client ID, never its credentials or filesystem path.
        [DataField(typeof(AcquisitionConnectionFields), nameof(AcquisitionConnectionFields.Id), false)]
        public long? SabnzbdConnectionId { get; }

        // A waiting download is read again on request.
        [DataField(typeof(OperationFields), nameof(OperationFields.CanRetryImport))]
        public bool CanRetryImport { get; }

        // It waits for a person, not for the client.
        [DataField(typeof(OperationFields), nameof(OperationFields.IsWaiting))]
        public bool IsWaiting { get; }

        // It kept where its files are, so a person can import them by hand.
        [DataField(typeof(OperationFields), nameof(OperationFields.CanImportManually))]
        public bool CanImportManually { get; }

        // The files of a waiting book download that were not placed; they stayed in the download.
        [DataField(typeof(OperationFields), nameof(OperationFields.UnmatchedFiles))]
        public string[]? UnmatchedFiles { get; }

        // Why each of them was not placed, in the same order (a text key below acquisition.file.; unknown when none was recorded).
        [DataField(typeof(OperationFields), nameof(OperationFields.UnmatchedFileReasons))]
        public string[]? UnmatchedFileReasons { get; }

        // The client that carries the download out, and the media type of its title.
        [DataField(typeof(AcquisitionConnectionFields), nameof(AcquisitionConnectionFields.Id), false)]
        public long? AcquisitionConnectionId { get; }

        [DataField(typeof(OperationFields), nameof(OperationFields.DownloadClientKindId), false)]
        public DownloadClientKind? DownloadClientKindId { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.MediaTypeId), false)]
        public MediaType? MediaTypeId { get; }

        // A failure nobody put aside yet can be tried again.
        [DataField(typeof(OperationFields), nameof(OperationFields.CanPause))]
        public bool CanRetry { get; }

        // How soon its client takes it.
        [DataField(typeof(OperationFields), nameof(OperationFields.DownloadPriorityTypeId))]
        public DownloadPriorityType DownloadPriorityTypeId { get; }

        // What the release is, as the queue's columns show it: its quality, the season (or volume) and the episodes it holds, the title of a single episode, and the languages its name says.
        [DataField(typeof(AcquisitionReleaseFields), nameof(AcquisitionReleaseFields.QualityKey))]
        public string? QualityKey { get; }

        [DataField(typeof(AcquisitionReleaseFields), nameof(AcquisitionReleaseFields.ParentUnitNumber))]
        public int? ParentUnitNumber { get; }

        [DataField(typeof(AcquisitionReleaseFields), nameof(AcquisitionReleaseFields.UnitStart))]
        public int? UnitStart { get; }

        [DataField(typeof(AcquisitionReleaseFields), nameof(AcquisitionReleaseFields.UnitEnd))]
        public int? UnitEnd { get; }

        [DataField(typeof(WorkUnitFields), nameof(WorkUnitFields.Title))]
        public string? UnitTitle { get; }

        [DataField(typeof(AcquisitionReleaseFields), nameof(AcquisitionReleaseFields.Languages))]
        public string[] Languages { get; }

        // A grab the indexer limited waits, not failed: it is tried again when the indexer's pause ends.
        [DataField(typeof(OperationFields), nameof(OperationFields.DownloadWaitUntil))]
        public DateTime? WaitUntil { get; }
    }

    public sealed class ReadDownloadClientsParametersV1 : BaseParameters
    {
    }

    // One tab of the Downloads page: a client with its open downloads, how fast it is and how much it still has to fetch.
    public sealed class DownloadClientV1 : BaseResponse
    {
        public DownloadClientV1(DownloadClientKind downloadClientKindId, long? acquisitionConnectionId, long settingsConnectionId, string name, int queueCount, int activeCount, int pausedCount,
            int warningCount, long bytesPerSecond, long remainingBytes, long[]? speedSamples = null)
        {
            SpeedSamples = speedSamples ?? [];
            DownloadClientKindId = downloadClientKindId;
            AcquisitionConnectionId = acquisitionConnectionId;
            SettingsConnectionId = settingsConnectionId;
            Name = name;
            QueueCount = queueCount;
            ActiveCount = activeCount;
            PausedCount = pausedCount;
            WarningCount = warningCount;
            BytesPerSecond = bytesPerSecond;
            RemainingBytes = remainingBytes;
        }

        [DataField(typeof(OperationFields), nameof(OperationFields.DownloadClientKindId))]
        public DownloadClientKind DownloadClientKindId { get; }

        // SABnzbd and qBittorrent are a client per connection; the others are one client of Jularr whatever its servers or sources.
        [DataField(typeof(AcquisitionConnectionFields), nameof(AcquisitionConnectionFields.Id), false)]
        public long? AcquisitionConnectionId { get; }

        // The connection whose settings the client's settings open (the first of its servers or sources).
        [DataField(typeof(AcquisitionConnectionFields), nameof(AcquisitionConnectionFields.Id))]
        public long SettingsConnectionId { get; }

        [DataField(typeof(AcquisitionConnectionFields), nameof(AcquisitionConnectionFields.Name))]
        public string Name { get; }

        // Open downloads, those that fetch or unpack now, those paused, and those that failed unseen or wait for a person.
        [DataField(typeof(OperationFields), nameof(OperationFields.QueueCount))]
        public int QueueCount { get; }

        [DataField(typeof(OperationFields), nameof(OperationFields.QueueCount))]
        public int ActiveCount { get; }

        [DataField(typeof(OperationFields), nameof(OperationFields.QueueCount))]
        public int PausedCount { get; }

        [DataField(typeof(OperationFields), nameof(OperationFields.QueueCount))]
        public int WarningCount { get; }

        [DataField(typeof(LiveFields), nameof(LiveFields.BytesPerSecond))]
        public long BytesPerSecond { get; }

        [DataField(typeof(OperationFields), nameof(OperationFields.SizeBytes))]
        public long RemainingBytes { get; }

        // The client's speed over the last minutes, oldest first, one sample every few seconds while the page or the stream was read.
        [DataField(typeof(OperationFields), nameof(OperationFields.SpeedSamples))]
        public long[] SpeedSamples { get; }

        public DownloadClientV1 WithSamples(long[] samples) => new(DownloadClientKindId, AcquisitionConnectionId, SettingsConnectionId, Name, QueueCount, ActiveCount, PausedCount, WarningCount,
            BytesPerSecond, RemainingBytes, samples);
    }

    // What a news server of Jularr's Usenet downloader did in its last pass: the connections it held, its pace, and its articles with the missing ones since the start.
    public sealed class NewsServerV1 : BaseResponse
    {
        public NewsServerV1(long acquisitionConnectionId, int connections, long bytesPerSecond, int articles, int missingArticles, int maxConnections)
        {
            MaxConnections = maxConnections;
            AcquisitionConnectionId = acquisitionConnectionId;
            Connections = connections;
            BytesPerSecond = bytesPerSecond;
            Articles = articles;
            MissingArticles = missingArticles;
        }

        [DataField(typeof(AcquisitionConnectionFields), nameof(AcquisitionConnectionFields.Id))]
        public long AcquisitionConnectionId { get; }

        [DataField(typeof(OperationFields), nameof(OperationFields.QueueCount))]
        public int Connections { get; }

        [DataField(typeof(LiveFields), nameof(LiveFields.BytesPerSecond))]
        public long BytesPerSecond { get; }

        [DataField(typeof(OperationFields), nameof(OperationFields.QueueCount))]
        public int Articles { get; }

        [DataField(typeof(OperationFields), nameof(OperationFields.QueueCount))]
        public int MissingArticles { get; }

        // The connections the server allows (a server without its own number gets the downloader's eight).
        [DataField(typeof(OperationFields), nameof(OperationFields.QueueCount))]
        public int MaxConnections { get; }
    }

    // One client of the Downloads page: a kind, and for SABnzbd and qBittorrent the connection.
    public sealed class DownloadClientParametersV1 : BaseParameters
    {
        public DownloadClientParametersV1(DownloadClientKind? downloadClientKindId = null, long? acquisitionConnectionId = null)
        {
            DownloadClientKindId = downloadClientKindId;
            AcquisitionConnectionId = acquisitionConnectionId;
        }

        [DataField(typeof(OperationFields), nameof(OperationFields.DownloadClientKindId), false)]
        public DownloadClientKind? DownloadClientKindId { get; }

        [DataField(typeof(AcquisitionConnectionFields), nameof(AcquisitionConnectionFields.Id), false)]
        public long? AcquisitionConnectionId { get; }
    }

    // A client's speed limit in bytes per second; none when empty.
    public sealed class DownloadSpeedLimitV1 : BaseResponse
    {
        public DownloadSpeedLimitV1(long? maxBytesPerSecond)
        {
            MaxBytesPerSecond = maxBytesPerSecond;
        }

        [DataField(typeof(DownloadStorageFields), nameof(DownloadStorageFields.MaxBytesPerSecond), false)]
        public long? MaxBytesPerSecond { get; }
    }

    public sealed class SetDownloadSpeedLimitDataV1 : BaseData
    {
        public SetDownloadSpeedLimitDataV1(long? maxBytesPerSecond)
        {
            MaxBytesPerSecond = maxBytesPerSecond;
        }

        [DataField(typeof(DownloadStorageFields), nameof(DownloadStorageFields.MaxBytesPerSecond), false)]
        public long? MaxBytesPerSecond { get; }
    }

    // The chosen downloads take one priority.
    public sealed class PrioritizeActivityDownloadsDataV1 : BaseData
    {
        public PrioritizeActivityDownloadsDataV1(long[] operationIds, DownloadPriorityType downloadPriorityTypeId)
        {
            OperationIds = operationIds;
            DownloadPriorityTypeId = downloadPriorityTypeId;
        }

        [DataField(typeof(OperationFields), nameof(OperationFields.Ids))]
        public long[] OperationIds { get; }

        [DataField(typeof(OperationFields), nameof(OperationFields.DownloadPriorityTypeId))]
        public DownloadPriorityType DownloadPriorityTypeId { get; }
    }

    // A download moves before another of its client's queue (dragged there), or to the end without one.
    public sealed class MoveActivityDownloadDataV1 : BaseData
    {
        public MoveActivityDownloadDataV1(long? beforeOperationId)
        {
            BeforeOperationId = beforeOperationId;
        }

        [DataField(typeof(OperationFields), nameof(OperationFields.Id), false)]
        public long? BeforeOperationId { get; }
    }

    // Finished downloads that leave the history.
    public sealed class DeleteActivityHistoryDataV1 : BaseData
    {
        public DeleteActivityHistoryDataV1(long[] operationIds)
        {
            OperationIds = operationIds;
        }

        [DataField(typeof(OperationFields), nameof(OperationFields.Ids))]
        public long[] OperationIds { get; }
    }

    // A download added by hand: the title it is for and the NZB, torrent or magnet address.
    public sealed class AddActivityDownloadDataV1 : BaseData
    {
        public AddActivityDownloadDataV1(long wantedItemId, string downloadUrl)
        {
            WantedItemId = wantedItemId;
            DownloadUrl = downloadUrl;
        }

        [DataField(typeof(WantedItemFields), nameof(WantedItemFields.Id))]
        public long WantedItemId { get; }

        [DataField(typeof(OperationFields), nameof(OperationFields.DownloadUrl))]
        public string DownloadUrl { get; }
    }

    public sealed class ApplyActivityDownloadsParametersV1 : BaseParameters
    {
    }

    // The chosen downloads of the queue and what happens to them; each client is told as for one download, and those it cannot do this for are left as they are.
    public sealed class ApplyActivityDownloadsDataV1 : BaseData
    {
        public ApplyActivityDownloadsDataV1(long[] operationIds, DownloadBulkActionType downloadBulkActionTypeId)
        {
            OperationIds = operationIds;
            DownloadBulkActionTypeId = downloadBulkActionTypeId;
        }

        [DataField(typeof(OperationFields), nameof(OperationFields.Ids))]
        public long[] OperationIds { get; }

        [DataField(typeof(OperationFields), nameof(OperationFields.DownloadBulkActionTypeId))]
        public DownloadBulkActionType DownloadBulkActionTypeId { get; }
    }

    public sealed class ReadActivitySummaryParametersV1 : BaseParameters
    {
    }

    // The queue in four numbers: what runs, the pace of the downloads, what is downloaded but not yet imported (waiting for a person or importing) and what failed unseen.
    public sealed class ActivitySummaryV1 : BaseResponse
    {
        public ActivitySummaryV1(int activeCount, long bytesPerSecond, int importCount, int errorCount, int allCount = 0, int waitingCount = 0, int unpackingCount = 0, int historyCount = 0, int taskHelpCount = 0)
        {
            TaskHelpCount = taskHelpCount;
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

        // Jularr's own tasks that need a person: failed and not put aside, or waiting.
        [DataField(typeof(OperationFields), nameof(OperationFields.QueueCount))]
        public int TaskHelpCount { get; }
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
        public ReadActivityEventsParametersV1(long? workId = null, int page = 1, int pageSize = 50, ActivityEventGroupType activityEventGroupTypeId = ActivityEventGroupType.All,
            MediaType[]? mediaTypeIds = null, MediaSubtype[]? mediaSubtypeIds = null)
        {
            MediaTypeIds = mediaTypeIds ?? [];
            MediaSubtypeIds = mediaSubtypeIds ?? [];
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

        // The type bar's choice, as in the queue.
        [DataField(typeof(WorkFields), nameof(WorkFields.MediaTypeIds))]
        public MediaType[] MediaTypeIds { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.MediaSubtypeIds))]
        public MediaSubtype[] MediaSubtypeIds { get; }

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

    // The whole queue, or one client's part of it.
    public sealed class PauseActivityQueueParametersV1 : BaseParameters
    {
        public PauseActivityQueueParametersV1(DownloadClientKind? downloadClientKindId = null, long? acquisitionConnectionId = null)
        {
            DownloadClientKindId = downloadClientKindId;
            AcquisitionConnectionId = acquisitionConnectionId;
        }

        [DataField(typeof(OperationFields), nameof(OperationFields.DownloadClientKindId), false)]
        public DownloadClientKind? DownloadClientKindId { get; }

        [DataField(typeof(AcquisitionConnectionFields), nameof(AcquisitionConnectionFields.Id), false)]
        public long? AcquisitionConnectionId { get; }
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
