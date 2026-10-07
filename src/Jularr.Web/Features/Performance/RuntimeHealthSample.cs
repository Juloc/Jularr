using System.Diagnostics;

namespace Jularr.Web.Features.Performance;

/// <summary>
/// The .NET runtime's own cumulative counters at one moment. Rates (allocation per second, share of time paused in garbage collection, CPU)
/// are differences between two samples, which the Admin view takes from consecutive stack samples; the runtime already exposes everything here
/// without any collector or extra instrumentation.
/// </summary>
public sealed record RuntimeHealthSample(
    long WorkingSetBytes,
    long ManagedHeapBytes,
    long AllocatedBytes,
    int Gen0Collections,
    int Gen1Collections,
    int Gen2Collections,
    TimeSpan GcPause,
    int ThreadPoolThreads,
    long ThreadPoolQueueLength,
    long LockContentions,
    TimeSpan ProcessCpuTime)
{
    public static RuntimeHealthSample Capture()
    {
        using var process = Process.GetCurrentProcess();
        return new RuntimeHealthSample(
            process.WorkingSet64,
            GC.GetGCMemoryInfo().HeapSizeBytes,
            GC.GetTotalAllocatedBytes(),
            GC.CollectionCount(0),
            GC.CollectionCount(1),
            GC.CollectionCount(2),
            GC.GetTotalPauseDuration(),
            ThreadPool.ThreadCount,
            ThreadPool.PendingWorkItemCount,
            Monitor.LockContentionCount,
            process.TotalProcessorTime);
    }
}

/// <summary>What changed between two samples, per second of the time between them.</summary>
public sealed record RuntimeHealthRates(double AllocatedBytesPerSecond, double GcPausePercent, double Gen2PerMinute, double CpuPercentOfOneCore)
{
    public static RuntimeHealthRates Between(RuntimeHealthSample earlier, RuntimeHealthSample later, TimeSpan elapsed)
    {
        var seconds = Math.Max(elapsed.TotalSeconds, 0.001);
        return new RuntimeHealthRates(
            Math.Max(0, later.AllocatedBytes - earlier.AllocatedBytes) / seconds,
            Math.Max(0, (later.GcPause - earlier.GcPause).TotalSeconds) * 100d / seconds,
            Math.Max(0, later.Gen2Collections - earlier.Gen2Collections) * 60d / seconds,
            Math.Max(0, (later.ProcessCpuTime - earlier.ProcessCpuTime).TotalSeconds) * 100d / seconds);
    }
}
