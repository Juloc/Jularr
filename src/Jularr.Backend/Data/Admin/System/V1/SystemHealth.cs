using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Instance;
using Jularr.Backend.Data.Fields.Instance;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Fields.Media;

namespace Jularr.Backend.Data.Admin.System.V1
{
    public sealed class ReadSystemHealthParametersV1 : BaseParameters
    {
    }

    // What the Owner needs to see at a glance: tools, queues, storage, backups and the library's size.
    public sealed class SystemHealthV1 : BaseResponse
    {
        public SystemHealthV1(long databaseBytes, long queuedOperations, long failedOperations, long profiles, long storageOffline, DateTime? lastBackupAt, BaseDataList<ToolStateV1> tools,
            BaseDataList<MediaTypeCountV1> library)
        {
            DatabaseBytes = databaseBytes;
            QueuedOperations = queuedOperations;
            FailedOperations = failedOperations;
            Profiles = profiles;
            StorageOffline = storageOffline;
            LastBackupAt = lastBackupAt;
            Tools = tools;
            Library = library;
        }

        [DataField(typeof(StatisticsFields), nameof(StatisticsFields.Bytes))]
        public long DatabaseBytes { get; }

        [DataField(typeof(StatisticsFields), nameof(StatisticsFields.Count))]
        public long QueuedOperations { get; }

        // Failures not yet dismissed.
        [DataField(typeof(StatisticsFields), nameof(StatisticsFields.Count))]
        public long FailedOperations { get; }

        [DataField(typeof(StatisticsFields), nameof(StatisticsFields.Count))]
        public long Profiles { get; }

        [DataField(typeof(StatisticsFields), nameof(StatisticsFields.Count))]
        public long StorageOffline { get; }

        [DataField(typeof(StatisticsFields), nameof(StatisticsFields.At))]
        public DateTime? LastBackupAt { get; }

        public BaseDataList<ToolStateV1> Tools { get; }

        // Works and stored bytes per media type.
        public BaseDataList<MediaTypeCountV1> Library { get; }
    }

    public sealed class ToolStateV1 : BaseResponse
    {
        public ToolStateV1(string key, bool isAvailable)
        {
            Key = key;
            IsAvailable = isAvailable;
        }

        [DataField(typeof(StatisticsFields), nameof(StatisticsFields.ToolKey))]
        public string Key { get; }

        [DataField(typeof(StatisticsFields), nameof(StatisticsFields.IsAvailable))]
        public bool IsAvailable { get; }
    }

    public sealed class MediaTypeCountV1 : BaseResponse
    {
        public MediaTypeCountV1(MediaType mediaTypeId, long count, long bytes)
        {
            MediaTypeId = mediaTypeId;
            Count = count;
            Bytes = bytes;
        }

        [DataField(typeof(WorkFields), nameof(WorkFields.MediaTypeId))]
        public MediaType MediaTypeId { get; }

        [DataField(typeof(StatisticsFields), nameof(StatisticsFields.Count))]
        public long Count { get; }

        [DataField(typeof(StatisticsFields), nameof(StatisticsFields.Bytes))]
        public long Bytes { get; }
    }

    public sealed class ReadRecentLogsParametersV1 : BaseParameters
    {
    }

    public sealed class RecentLogV1 : BaseResponse
    {
        public RecentLogV1(DateTime at, RecentLogLevel level, string category, string message)
        {
            At = at;
            Level = level;
            Category = category;
            Message = message;
        }

        [DataField(typeof(RecentLogFields), nameof(RecentLogFields.At))]
        public DateTime At { get; }

        [DataField(typeof(RecentLogFields), nameof(RecentLogFields.Level))]
        public RecentLogLevel Level { get; }

        [DataField(typeof(RecentLogFields), nameof(RecentLogFields.Category))]
        public string Category { get; }

        [DataField(typeof(RecentLogFields), nameof(RecentLogFields.Message))]
        public string Message { get; }
    }

    public sealed class ReadPerformanceParametersV1 : BaseParameters
    {
    }

    public sealed class ResourceSampleV1 : BaseResponse
    {
        public ResourceSampleV1(DateTime at, decimal processorPercent, long memoryBytes)
        {
            At = at;
            ProcessorPercent = processorPercent;
            MemoryBytes = memoryBytes;
        }

        [DataField(typeof(PerformanceFields), nameof(PerformanceFields.At))]
        public DateTime At { get; }

        [DataField(typeof(PerformanceFields), nameof(PerformanceFields.ProcessorPercent))]
        public decimal ProcessorPercent { get; }

        [DataField(typeof(PerformanceFields), nameof(PerformanceFields.MemoryBytes))]
        public long MemoryBytes { get; }
    }

    public sealed class ServiceTimingV1 : BaseResponse
    {
        public ServiceTimingV1(string service, long count, long errors, int averageMilliseconds, int maximumMilliseconds)
        {
            Service = service;
            Count = count;
            Errors = errors;
            AverageMilliseconds = averageMilliseconds;
            MaximumMilliseconds = maximumMilliseconds;
        }

        [DataField(typeof(PerformanceFields), nameof(PerformanceFields.Service))]
        public string Service { get; }

        [DataField(typeof(PerformanceFields), nameof(PerformanceFields.Count))]
        public long Count { get; }

        [DataField(typeof(PerformanceFields), nameof(PerformanceFields.Count))]
        public long Errors { get; }

        [DataField(typeof(PerformanceFields), nameof(PerformanceFields.Milliseconds))]
        public int AverageMilliseconds { get; }

        [DataField(typeof(PerformanceFields), nameof(PerformanceFields.Milliseconds))]
        public int MaximumMilliseconds { get; }
    }
}
