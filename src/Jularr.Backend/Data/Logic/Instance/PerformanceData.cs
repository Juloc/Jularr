using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Fields.Instance;

namespace Jularr.Backend.Data.Logic.Instance
{
    // A processor and memory sample as the database keeps it.
    internal sealed class PerformanceSampleRowData : BaseData
    {
        public PerformanceSampleRowData(DateTime sampledAt, decimal processorPercent, long memoryBytes)
        {
            SampledAt = sampledAt;
            ProcessorPercent = processorPercent;
            MemoryBytes = memoryBytes;
        }

        [DataField(typeof(PerformanceFields), nameof(PerformanceFields.At))]
        public DateTime SampledAt { get; }

        [DataField(typeof(PerformanceFields), nameof(PerformanceFields.ProcessorPercent))]
        public decimal ProcessorPercent { get; }

        [DataField(typeof(PerformanceFields), nameof(PerformanceFields.MemoryBytes))]
        public long MemoryBytes { get; }
    }

    // One hour of one service as the database keeps it.
    internal sealed class ServiceTimingRowData : BaseData
    {
        public ServiceTimingRowData(string service, DateTime sliceStartedAt, long calls, long errors, long totalMilliseconds, long maximumMilliseconds)
        {
            Service = service;
            SliceStartedAt = sliceStartedAt;
            Calls = calls;
            Errors = errors;
            TotalMilliseconds = totalMilliseconds;
            MaximumMilliseconds = maximumMilliseconds;
        }

        [DataField(typeof(PerformanceFields), nameof(PerformanceFields.Service))]
        public string Service { get; }

        [DataField(typeof(PerformanceFields), nameof(PerformanceFields.SliceStartedAt))]
        public DateTime SliceStartedAt { get; }

        [DataField(typeof(PerformanceFields), nameof(PerformanceFields.Count))]
        public long Calls { get; }

        [DataField(typeof(PerformanceFields), nameof(PerformanceFields.Count))]
        public long Errors { get; }

        [DataField(typeof(PerformanceFields), nameof(PerformanceFields.TotalMilliseconds))]
        public long TotalMilliseconds { get; }

        [DataField(typeof(PerformanceFields), nameof(PerformanceFields.TotalMilliseconds))]
        public long MaximumMilliseconds { get; }
    }

    // New samples and changed slices for one write, as parallel lists: row n of a kind is the n-th entry of each of its lists.
    internal sealed class PerformanceSaveData : BaseData
    {
        public PerformanceSaveData(long[] sampleSeconds, long[] sampleTenths, long[] sampleMemory, string[] services, long[] sliceSeconds, long[] calls, long[] errors, long[] totalMilliseconds,
            long[] maximumMilliseconds)
        {
            SampleSeconds = sampleSeconds;
            SampleTenths = sampleTenths;
            SampleMemory = sampleMemory;
            Services = services;
            SliceSeconds = sliceSeconds;
            Calls = calls;
            Errors = errors;
            TotalMilliseconds = totalMilliseconds;
            MaximumMilliseconds = maximumMilliseconds;
        }

        [DataField(typeof(PerformanceFields), nameof(PerformanceFields.Seconds))]
        public long[] SampleSeconds { get; }

        // The processor share in tenths of a percent.
        [DataField(typeof(PerformanceFields), nameof(PerformanceFields.Numbers))]
        public long[] SampleTenths { get; }

        [DataField(typeof(PerformanceFields), nameof(PerformanceFields.Numbers))]
        public long[] SampleMemory { get; }

        [DataField(typeof(PerformanceFields), nameof(PerformanceFields.Services))]
        public string[] Services { get; }

        [DataField(typeof(PerformanceFields), nameof(PerformanceFields.Seconds))]
        public long[] SliceSeconds { get; }

        [DataField(typeof(PerformanceFields), nameof(PerformanceFields.Numbers))]
        public long[] Calls { get; }

        [DataField(typeof(PerformanceFields), nameof(PerformanceFields.Numbers))]
        public long[] Errors { get; }

        [DataField(typeof(PerformanceFields), nameof(PerformanceFields.Numbers))]
        public long[] TotalMilliseconds { get; }

        [DataField(typeof(PerformanceFields), nameof(PerformanceFields.Numbers))]
        public long[] MaximumMilliseconds { get; }
    }
}
