using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Fields.Acquisition;

namespace Jularr.Backend.Data.Admin.Acquisition.V1
{
    // Snapshot data comes from the selected SABnzbd instance, never from demo values.
    public sealed class DownloadClientDashboardV1 : BaseResponse
    {
        public DownloadClientDashboardV1(long? speedBytesPerSecond, long? freeBytes, bool isPaused, bool queueLimited, bool historyLimited,
            BaseDataList<DownloadClientQueueItemV1> queue, BaseDataList<DownloadClientHistoryItemV1> history, string[] categories,
            long? todayBytes, long? weekBytes, long? monthBytes, long? totalBytes, string[] orphanFolders,
            string? completePath, string? incompletePath, bool completeAvailable, bool incompleteAvailable, BaseDataList<DownloadClientFolderEntryV1> files,
            string? reportedCompletePath = null, string? reportedIncompletePath = null)
        {
            SpeedBytesPerSecond = speedBytesPerSecond;
            FreeBytes = freeBytes;
            IsPaused = isPaused;
            QueueLimited = queueLimited;
            HistoryLimited = historyLimited;
            Queue = queue;
            History = history;
            Categories = categories;
            TodayBytes = todayBytes;
            WeekBytes = weekBytes;
            MonthBytes = monthBytes;
            TotalBytes = totalBytes;
            OrphanFolders = orphanFolders;
            CompletePath = completePath;
            IncompletePath = incompletePath;
            CompleteAvailable = completeAvailable;
            IncompleteAvailable = incompleteAvailable;
            Files = files;
            ReportedCompletePath = reportedCompletePath;
            ReportedIncompletePath = reportedIncompletePath;
        }

        [DataField(typeof(DownloadClientConsoleFields), nameof(DownloadClientConsoleFields.Bytes))]
        public long? SpeedBytesPerSecond { get; }

        [DataField(typeof(DownloadClientConsoleFields), nameof(DownloadClientConsoleFields.Bytes))]
        public long? FreeBytes { get; }

        [DataField(typeof(DownloadClientConsoleFields), nameof(DownloadClientConsoleFields.Flag))]
        public bool IsPaused { get; }

        [DataField(typeof(DownloadClientConsoleFields), nameof(DownloadClientConsoleFields.Flag))]
        public bool QueueLimited { get; }

        [DataField(typeof(DownloadClientConsoleFields), nameof(DownloadClientConsoleFields.Flag))]
        public bool HistoryLimited { get; }

        public BaseDataList<DownloadClientQueueItemV1> Queue { get; }
        public BaseDataList<DownloadClientHistoryItemV1> History { get; }

        [DataField(typeof(DownloadClientConsoleFields), nameof(DownloadClientConsoleFields.Categories))]
        public string[] Categories { get; }

        [DataField(typeof(DownloadClientConsoleFields), nameof(DownloadClientConsoleFields.Bytes))]
        public long? TodayBytes { get; }

        [DataField(typeof(DownloadClientConsoleFields), nameof(DownloadClientConsoleFields.Bytes))]
        public long? WeekBytes { get; }

        [DataField(typeof(DownloadClientConsoleFields), nameof(DownloadClientConsoleFields.Bytes))]
        public long? MonthBytes { get; }

        [DataField(typeof(DownloadClientConsoleFields), nameof(DownloadClientConsoleFields.Bytes))]
        public long? TotalBytes { get; }

        [DataField(typeof(DownloadClientConsoleFields), nameof(DownloadClientConsoleFields.OrphanFolders))]
        public string[] OrphanFolders { get; }

        [DataField(typeof(DownloadClientConsoleFields), nameof(DownloadClientConsoleFields.Path))]
        public string? CompletePath { get; }

        [DataField(typeof(DownloadClientConsoleFields), nameof(DownloadClientConsoleFields.Path))]
        public string? IncompletePath { get; }

        [DataField(typeof(DownloadClientConsoleFields), nameof(DownloadClientConsoleFields.Flag))]
        public bool CompleteAvailable { get; }

        [DataField(typeof(DownloadClientConsoleFields), nameof(DownloadClientConsoleFields.Flag))]
        public bool IncompleteAvailable { get; }

        public BaseDataList<DownloadClientFolderEntryV1> Files { get; }

        [DataField(typeof(DownloadClientConsoleFields), nameof(DownloadClientConsoleFields.Path))]
        public string? ReportedCompletePath { get; }

        [DataField(typeof(DownloadClientConsoleFields), nameof(DownloadClientConsoleFields.Path))]
        public string? ReportedIncompletePath { get; }
    }

    public sealed class DownloadClientQueueItemV1 : BaseResponse
    {
        public DownloadClientQueueItemV1(string jobId, string name, string? status, int? progressPercent, long? sizeBytes, long? remainingBytes, int? timeLeftSeconds)
        {
            JobId = jobId;
            Name = name;
            Status = status;
            ProgressPercent = progressPercent;
            SizeBytes = sizeBytes;
            RemainingBytes = remainingBytes;
            TimeLeftSeconds = timeLeftSeconds;
        }

        [DataField(typeof(DownloadClientConsoleFields), nameof(DownloadClientConsoleFields.JobId))]
        public string JobId { get; }

        [DataField(typeof(DownloadClientConsoleFields), nameof(DownloadClientConsoleFields.Name))]
        public string Name { get; }

        [DataField(typeof(DownloadClientConsoleFields), nameof(DownloadClientConsoleFields.Status))]
        public string? Status { get; }

        [DataField(typeof(DownloadClientConsoleFields), nameof(DownloadClientConsoleFields.ProgressPercent))]
        public int? ProgressPercent { get; }

        [DataField(typeof(DownloadClientConsoleFields), nameof(DownloadClientConsoleFields.Bytes))]
        public long? SizeBytes { get; }

        [DataField(typeof(DownloadClientConsoleFields), nameof(DownloadClientConsoleFields.Bytes))]
        public long? RemainingBytes { get; }

        [DataField(typeof(DownloadClientConsoleFields), nameof(DownloadClientConsoleFields.TimeLeftSeconds))]
        public int? TimeLeftSeconds { get; }
    }

    public sealed class DownloadClientHistoryItemV1 : BaseResponse
    {
        public DownloadClientHistoryItemV1(string jobId, string name, string? status, string? storagePath, bool failed)
        {
            JobId = jobId;
            Name = name;
            Status = status;
            StoragePath = storagePath;
            Failed = failed;
        }

        [DataField(typeof(DownloadClientConsoleFields), nameof(DownloadClientConsoleFields.JobId))]
        public string JobId { get; }

        [DataField(typeof(DownloadClientConsoleFields), nameof(DownloadClientConsoleFields.Name))]
        public string Name { get; }

        [DataField(typeof(DownloadClientConsoleFields), nameof(DownloadClientConsoleFields.Status))]
        public string? Status { get; }

        [DataField(typeof(DownloadClientConsoleFields), nameof(DownloadClientConsoleFields.Path))]
        public string? StoragePath { get; }

        [DataField(typeof(DownloadClientConsoleFields), nameof(DownloadClientConsoleFields.Flag))]
        public bool Failed { get; }
    }

    public sealed class DownloadClientFolderEntryV1 : BaseResponse
    {
        public DownloadClientFolderEntryV1(string folder, string name, long? sizeBytes, string assignment)
        {
            Folder = folder;
            Name = name;
            SizeBytes = sizeBytes;
            Assignment = assignment;
        }

        [DataField(typeof(DownloadClientConsoleFields), nameof(DownloadClientConsoleFields.Folder))]
        public string Folder { get; }

        [DataField(typeof(DownloadClientConsoleFields), nameof(DownloadClientConsoleFields.FileName))]
        public string Name { get; }

        [DataField(typeof(DownloadClientConsoleFields), nameof(DownloadClientConsoleFields.Bytes))]
        public long? SizeBytes { get; }

        [DataField(typeof(DownloadClientConsoleFields), nameof(DownloadClientConsoleFields.Assignment))]
        public string Assignment { get; }
    }

    public sealed class DownloadClientJobCommandV1 : BaseData
    {
        public DownloadClientJobCommandV1(string jobId, string action, bool confirmed = false)
        {
            JobId = jobId;
            Action = action;
            Confirmed = confirmed;
        }

        [DataField(typeof(DownloadClientConsoleFields), nameof(DownloadClientConsoleFields.JobId))]
        public string JobId { get; }

        [DataField(typeof(DownloadClientConsoleFields), nameof(DownloadClientConsoleFields.Action))]
        public string Action { get; }

        [DataField(typeof(DownloadClientConsoleFields), nameof(DownloadClientConsoleFields.Flag))]
        public bool Confirmed { get; }
    }
}
