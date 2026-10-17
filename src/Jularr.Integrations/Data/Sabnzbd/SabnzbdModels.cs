using System.Text.Json.Serialization;

namespace Jularr.Integrations.Data.Sabnzbd
{
    internal sealed class SabnzbdConnection
    {
        public SabnzbdConnection(string baseUrl, string apiKey)
        {
            BaseUrl = baseUrl;
            ApiKey = apiKey;
        }

        public string BaseUrl { get; }

        [JsonIgnore]
        public string ApiKey { get; }
    }

    // Version alone proves reachability; the queue read proves the full API key.
    internal sealed class SabnzbdConnectionTestResult
    {
        public SabnzbdConnectionTestResult(bool isAvailable, string? version)
        {
            IsAvailable = isAvailable;
            Version = version;
        }

        public bool IsAvailable { get; }
        public string? Version { get; }
    }

    internal sealed class SabnzbdGrabRequest
    {
        public SabnzbdGrabRequest(Uri nzbUrl, string? nzbName, string? category)
        {
            NzbUrl = nzbUrl;
            NzbName = nzbName;
            Category = category;
        }

        public Uri NzbUrl { get; }
        public string? NzbName { get; }
        public string? Category { get; }
    }

    internal sealed class SabnzbdGrabResult
    {
        public SabnzbdGrabResult(bool success, IReadOnlyList<string> nzoIds)
        {
            Success = success;
            NzoIds = nzoIds;
        }

        public bool Success { get; }
        public IReadOnlyList<string> NzoIds { get; }
    }

    internal enum SabnzbdFailureKind : byte
    {
        None,
        Download,
        Unpack,
        Verification,
        Password,
        Script,
        Storage,
        Unknown
    }

    internal static class SabnzbdFailureKinds
    {
        // Only these say something about the release; client problems never exclude it.
        public static bool IsReleaseFault(SabnzbdFailureKind kind) =>
            kind is SabnzbdFailureKind.Download or SabnzbdFailureKind.Unpack or SabnzbdFailureKind.Verification or SabnzbdFailureKind.Password;
    }

    internal sealed class SabnzbdQueueJob
    {
        public SabnzbdQueueJob(string nzoId, string name, double? percentage, long? sizeBytes = null, long? remainingBytes = null, int? timeLeftSeconds = null, string? status = null)
        {
            NzoId = nzoId;
            Name = name;
            Percentage = percentage;
            SizeBytes = sizeBytes;
            RemainingBytes = remainingBytes;
            TimeLeftSeconds = timeLeftSeconds;
            Status = status;
        }

        public string NzoId { get; }
        public string Name { get; }
        public double? Percentage { get; }
        public long? SizeBytes { get; }
        public long? RemainingBytes { get; }
        public int? TimeLeftSeconds { get; }

        // Downloading, Queued, Paused or Fetching; a paused queue pauses every job.
        public string? Status { get; }
    }

    internal sealed class SabnzbdHistoryJob
    {
        public SabnzbdHistoryJob(string nzoId, string name, string? status, string? storagePath, SabnzbdFailureKind failureKind)
        {
            NzoId = nzoId;
            Name = name;
            Status = status;
            StoragePath = storagePath;
            FailureKind = failureKind;
        }

        public string NzoId { get; }
        public string Name { get; }
        public string? Status { get; }
        public string? StoragePath { get; }
        public SabnzbdFailureKind FailureKind { get; }

        public bool IsCompleted => FailureKind == SabnzbdFailureKind.None && string.Equals(Status, "Completed", StringComparison.OrdinalIgnoreCase);

        public bool IsFailed => FailureKind != SabnzbdFailureKind.None || string.Equals(Status, "Failed", StringComparison.OrdinalIgnoreCase);
    }

    internal sealed class SabnzbdQueueSnapshot
    {
        public SabnzbdQueueSnapshot(IReadOnlyList<SabnzbdQueueJob> jobs)
        {
            Jobs = jobs;
        }

        public IReadOnlyList<SabnzbdQueueJob> Jobs { get; }
    }

    internal sealed class SabnzbdHistorySnapshot
    {
        public SabnzbdHistorySnapshot(IReadOnlyList<SabnzbdHistoryJob> jobs)
        {
            Jobs = jobs;
        }

        public IReadOnlyList<SabnzbdHistoryJob> Jobs { get; }
    }


    internal enum SabnzbdJobCommand
    {
        Pause,
        Resume,
        RemoveFromQueue,
        RemoveFromHistory,
        RemoveOrphan
    }

    internal sealed class SabnzbdConsoleSnapshot
    {
        public SabnzbdConsoleSnapshot(IReadOnlyList<SabnzbdQueueJob> queue, IReadOnlyList<SabnzbdHistoryJob> history, long? speedBytesPerSecond, long? freeBytes, bool isPaused,
            long? todayBytes = null, long? weekBytes = null, long? monthBytes = null, long? totalBytes = null, IReadOnlyList<string>? orphanFolders = null)
        {
            Queue = queue;
            History = history;
            SpeedBytesPerSecond = speedBytesPerSecond;
            FreeBytes = freeBytes;
            IsPaused = isPaused;
            TodayBytes = todayBytes;
            WeekBytes = weekBytes;
            MonthBytes = monthBytes;
            TotalBytes = totalBytes;
            OrphanFolders = orphanFolders ?? [];
        }

        public IReadOnlyList<SabnzbdQueueJob> Queue { get; }
        public IReadOnlyList<SabnzbdHistoryJob> History { get; }
        public long? SpeedBytesPerSecond { get; }
        public long? FreeBytes { get; }
        public bool IsPaused { get; }
        public long? TodayBytes { get; }
        public long? WeekBytes { get; }
        public long? MonthBytes { get; }
        public long? TotalBytes { get; }
        public IReadOnlyList<string> OrphanFolders { get; }
    }

    // Messages never contain the API key.
    internal sealed class SabnzbdException(string message, Exception? innerException = null) : Exception(message, innerException);
}
