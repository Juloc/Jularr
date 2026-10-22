using System.Collections.Concurrent;

namespace Jularr.Backend.Service.Core
{
    // What each service did during the last 24 hours, in hourly slices. Recording costs a lock on one slice and nothing else; the slices that changed
    // are written to the database in batches (SavePerformanceHistoryV1) and read back once after a restart.
    internal sealed class ServiceTimings(TimeProvider time)
    {
        private static readonly TimeSpan SliceLength = TimeSpan.FromHours(1);
        private const int SliceCount = 24;
        private readonly ConcurrentDictionary<string, Slice[]> _services = new(StringComparer.Ordinal);

        // Follows the Diagnostics module.
        public bool IsEnabled { get; private set; } = true;

        public void Enable(bool enabled)
        {
            IsEnabled = enabled;
            if (!enabled) _services.Clear();
        }

        public void Record(string service, TimeSpan duration, bool failed)
        {
            if (!IsEnabled)
            {
                return;
            }

            var slice = SliceOf(service, CurrentIndex());
            lock (slice)
            {
                slice.Count++;
                if (failed) slice.Errors++;
                slice.TotalMilliseconds += duration.TotalMilliseconds;
                slice.MaxMilliseconds = Math.Max(slice.MaxMilliseconds, duration.TotalMilliseconds);
                slice.Version++;
            }
        }

        // The services that took the most time in total first, over the window of 24 hourly slices.
        public IReadOnlyList<ServiceTiming> Read(int count) => [.. Window().OrderByDescending(timing => timing.TotalMilliseconds).Take(count)];

        // Everything together, for the figures at the top of the Performance page.
        public ServiceTimingTotals Totals()
        {
            long calls = 0, errors = 0;
            double total = 0;
            foreach (var timing in Window())
            {
                calls += timing.Count;
                errors += timing.Errors;
                total += timing.TotalMilliseconds;
            }

            return new ServiceTimingTotals(calls, errors, total);
        }

        // The slices that changed since they were last saved; the saver confirms with MarkSaved once the database has them.
        public IReadOnlyList<ServiceTimingSlice> ReadUnsaved()
        {
            var unsaved = new List<ServiceTimingSlice>();
            var oldest = CurrentIndex() - SliceCount + 1;
            foreach (var (service, slices) in _services)
            {
                foreach (var slice in slices)
                {
                    lock (slice)
                    {
                        if (slice.Index >= oldest && slice.Count > 0 && slice.Version != slice.SavedVersion)
                        {
                            unsaved.Add(new ServiceTimingSlice(service, slice.Index, slice.Count, slice.Errors, (long)Math.Round(slice.TotalMilliseconds), (long)Math.Round(slice.MaxMilliseconds), slice.Version));
                        }
                    }
                }
            }

            return unsaved;
        }

        public void MarkSaved(IEnumerable<ServiceTimingSlice> saved)
        {
            foreach (var item in saved)
            {
                if (!_services.TryGetValue(item.Service, out var slices)) continue;

                var slice = slices[item.Index % SliceCount];
                lock (slice)
                {
                    if (slice.Index == item.Index) slice.SavedVersion = Math.Max(slice.SavedVersion, item.Version);
                }
            }
        }

        // What an earlier run of the process kept; calls made since the start stay and add up.
        public void Load(IEnumerable<ServiceTimingSlice> kept)
        {
            if (!IsEnabled)
            {
                return;
            }

            var oldest = CurrentIndex() - SliceCount + 1;
            foreach (var item in kept.Where(item => item.Index >= oldest && item.Index <= CurrentIndex()))
            {
                var slice = SliceOf(item.Service, item.Index);
                lock (slice)
                {
                    var hadCalls = slice.Count > 0;
                    slice.Count += item.Count;
                    slice.Errors += item.Errors;
                    slice.TotalMilliseconds += item.TotalMilliseconds;
                    slice.MaxMilliseconds = Math.Max(slice.MaxMilliseconds, item.MaximumMilliseconds);

                    // Calls made before the load are not in the database yet; otherwise it holds what was just read.
                    if (!hadCalls) slice.SavedVersion = slice.Version;
                }
            }
        }

        // The first hour of a slice's number, counted from the Unix epoch.
        public static DateTime StartOf(long index) => new DateTime(index * SliceLength.Ticks, DateTimeKind.Utc);

        public static long IndexOf(DateTime at) => at.ToUniversalTime().Ticks / SliceLength.Ticks;

        private long CurrentIndex() => time.GetUtcNow().UtcTicks / SliceLength.Ticks;

        // The slice of the service for an hour; one that still holds an older hour starts over.
        private Slice SliceOf(string service, long index)
        {
            var slices = _services.GetOrAdd(service, _ => [.. Enumerable.Range(0, SliceCount).Select(_ => new Slice())]);
            var slice = slices[index % SliceCount];
            lock (slice)
            {
                if (slice.Index != index)
                {
                    slice.Index = index;
                    slice.Count = 0;
                    slice.Errors = 0;
                    slice.TotalMilliseconds = 0;
                    slice.MaxMilliseconds = 0;
                    slice.Version = 1;
                    slice.SavedVersion = 0;
                }
            }

            return slice;
        }

        private List<ServiceTiming> Window()
        {
            var oldest = CurrentIndex() - SliceCount + 1;
            var timings = new List<ServiceTiming>();
            foreach (var (service, slices) in _services)
            {
                long calls = 0, errors = 0;
                double total = 0, max = 0;
                foreach (var slice in slices)
                {
                    lock (slice)
                    {
                        if (slice.Index < oldest) continue;
                        calls += slice.Count;
                        errors += slice.Errors;
                        total += slice.TotalMilliseconds;
                        max = Math.Max(max, slice.MaxMilliseconds);
                    }
                }

                if (calls > 0) timings.Add(new ServiceTiming(service, calls, errors, total, max));
            }

            return timings;
        }

        private sealed class Slice
        {
            public long Index = -1;
            public long Count;
            public long Errors;
            public double TotalMilliseconds;
            public double MaxMilliseconds;
            public long Version;
            public long SavedVersion;
        }
    }

    internal sealed record ServiceTiming(string Service, long Count, long Errors, double TotalMilliseconds, double MaxMilliseconds);

    internal sealed record ServiceTimingTotals(long Calls, long Errors, double TotalMilliseconds);

    // One hour of one service as it is kept; the version tells the saver whether it changed since.
    internal sealed record ServiceTimingSlice(string Service, long Index, long Count, long Errors, long TotalMilliseconds, long MaximumMilliseconds, long Version);
}
