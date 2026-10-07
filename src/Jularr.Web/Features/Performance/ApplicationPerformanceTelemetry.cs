namespace Jularr.Web.Features.Performance;

/// <summary>Where a timed operation belongs. Every key inside a category is a stable, low-cardinality identity (a route template, a work name, a provider client name).</summary>
public enum PerformanceCategory
{
    Route = 0,
    Background = 1,
    Provider = 2
}

public enum PerformanceOutcome
{
    Succeeded = 0,
    Failed = 1,
    Cancelled = 2
}

/// <summary>One operation's timings over the recent window. Percentiles are the upper edge of the histogram bucket that holds them.</summary>
public sealed record PerformanceRow(
    PerformanceCategory Category,
    string Key,
    long Count,
    long Failed,
    long Cancelled,
    double TotalMilliseconds,
    double MaxMilliseconds,
    double P95Milliseconds,
    double MeanQueueWaitMilliseconds,
    DateTimeOffset LastAtUtc)
{
    public double MeanMilliseconds => Count == 0 ? 0 : TotalMilliseconds / Count;
}

/// <summary>
/// The one in-process aggregate behind Admin's "what is making Jularr slow or busy". It keeps per-operation timings over the last hour in
/// five-minute slices, so the view answers "recently" without any history table, external collector or per-request row. Keys beyond
/// <see cref="MaxKeysPerCategory"/> collapse into one overflow row so a bug can never make the memory or the page grow without bound.
/// </summary>
public sealed class ApplicationPerformanceTelemetry(TimeProvider clock)
{
    public const int MaxKeysPerCategory = 200;
    public const string OverflowKey = "other";
    public static readonly TimeSpan SliceLength = TimeSpan.FromMinutes(5);
    public const int SliceCount = 12;

    // Upper edges in milliseconds; the last bucket is open-ended.
    private static readonly double[] BucketEdges = [25, 100, 500, 2_000, 10_000];

    private readonly Dictionary<(PerformanceCategory, string), Accumulator> operations = [];
    private readonly Dictionary<string, long> rateLimited = [];
    private readonly Lock gate = new();

    /// <summary>The window the rows describe.</summary>
    public static TimeSpan Window => SliceLength * SliceCount;

    public DateTimeOffset StartedAtUtc { get; } = clock.GetUtcNow();

    public void Record(PerformanceCategory category, string key, TimeSpan duration, PerformanceOutcome outcome, TimeSpan queueWait = default)
    {
        var now = clock.GetUtcNow();
        var slice = now.UtcTicks / SliceLength.Ticks;
        lock (gate)
        {
            var identity = (category, key);
            if (!operations.TryGetValue(identity, out var accumulator))
            {
                if (operations.Count(pair => pair.Key.Item1 == category) >= MaxKeysPerCategory)
                {
                    identity = (category, OverflowKey);
                    if (!operations.TryGetValue(identity, out accumulator))
                    {
                        operations[identity] = accumulator = new Accumulator();
                    }
                }
                else
                {
                    operations[identity] = accumulator = new Accumulator();
                }
            }

            accumulator.Add(slice, duration.TotalMilliseconds, outcome, queueWait.TotalMilliseconds, now);
        }
    }

    /// <summary>One call to a policy-limited endpoint that was refused; the policy name is a fixed configuration identity.</summary>
    public void RecordRateLimited(string policy)
    {
        lock (gate)
        {
            rateLimited[policy] = rateLimited.GetValueOrDefault(policy) + 1;
        }
    }

    public IReadOnlyDictionary<string, long> RateLimited()
    {
        lock (gate)
        {
            return new Dictionary<string, long>(rateLimited);
        }
    }

    /// <summary>Every operation active inside the window, busiest by total time first.</summary>
    public IReadOnlyList<PerformanceRow> Snapshot()
    {
        var oldest = clock.GetUtcNow().UtcTicks / SliceLength.Ticks - SliceCount + 1;
        lock (gate)
        {
            return [.. operations
                .Select(pair => pair.Value.Summarize(pair.Key.Item1, pair.Key.Item2, oldest))
                .Where(row => row is not null)
                .Select(row => row!)
                .OrderByDescending(row => row.TotalMilliseconds)];
        }
    }

    private sealed class Accumulator
    {
        private readonly Dictionary<long, Slice> slices = [];
        private DateTimeOffset last;

        public void Add(long sliceIndex, double milliseconds, PerformanceOutcome outcome, double queueWaitMilliseconds, DateTimeOffset now)
        {
            if (!slices.TryGetValue(sliceIndex, out var slice))
            {
                foreach (var expired in slices.Keys.Where(index => index <= sliceIndex - SliceCount).ToArray())
                {
                    slices.Remove(expired);
                }

                slices[sliceIndex] = slice = new Slice();
            }

            slice.Count++;
            slice.TotalMilliseconds += milliseconds;
            slice.QueueWaitMilliseconds += queueWaitMilliseconds;
            slice.MaxMilliseconds = Math.Max(slice.MaxMilliseconds, milliseconds);
            slice.Buckets[BucketOf(milliseconds)]++;
            if (outcome == PerformanceOutcome.Failed)
            {
                slice.Failed++;
            }
            else if (outcome == PerformanceOutcome.Cancelled)
            {
                slice.Cancelled++;
            }

            last = now;
        }

        public PerformanceRow? Summarize(PerformanceCategory category, string key, long oldestSlice)
        {
            var live = slices.Where(pair => pair.Key >= oldestSlice).Select(pair => pair.Value).ToArray();
            var count = live.Sum(slice => slice.Count);
            if (count == 0)
            {
                return null;
            }

            var buckets = new long[BucketEdges.Length + 1];
            foreach (var slice in live)
            {
                for (var index = 0; index < buckets.Length; index++)
                {
                    buckets[index] += slice.Buckets[index];
                }
            }

            var max = live.Max(slice => slice.MaxMilliseconds);
            var threshold = (long)Math.Ceiling(count * 0.95);
            long seen = 0;
            var p95 = max;
            for (var index = 0; index < buckets.Length; index++)
            {
                seen += buckets[index];
                if (seen >= threshold)
                {
                    p95 = index < BucketEdges.Length ? Math.Min(BucketEdges[index], max) : max;
                    break;
                }
            }

            return new PerformanceRow(
                category,
                key,
                count,
                live.Sum(slice => slice.Failed),
                live.Sum(slice => slice.Cancelled),
                live.Sum(slice => slice.TotalMilliseconds),
                max,
                p95,
                live.Sum(slice => slice.QueueWaitMilliseconds) / count,
                last);
        }

        private static int BucketOf(double milliseconds)
        {
            for (var index = 0; index < BucketEdges.Length; index++)
            {
                if (milliseconds <= BucketEdges[index])
                {
                    return index;
                }
            }

            return BucketEdges.Length;
        }
    }

    private sealed class Slice
    {
        public long Count;
        public long Failed;
        public long Cancelled;
        public double TotalMilliseconds;
        public double QueueWaitMilliseconds;
        public double MaxMilliseconds;
        public long[] Buckets = new long[BucketEdges.Length + 1];
    }
}
