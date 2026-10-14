using System.Collections.Concurrent;

namespace Jularr.Backend.Service.Core
{
    // How long each service took during the last hour, in five-minute slices; a restart begins empty.
    internal sealed class ServiceTimings(TimeProvider time)
    {
        private static readonly TimeSpan SliceLength = TimeSpan.FromMinutes(5);
        private const int SliceCount = 12;
        private readonly ConcurrentDictionary<string, Slice[]> _services = new(StringComparer.Ordinal);

        public void Record(string service, TimeSpan duration, bool failed)
        {
            var index = time.GetUtcNow().UtcTicks / SliceLength.Ticks;
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
                }

                slice.Count++;
                if (failed) slice.Errors++;
                slice.TotalMilliseconds += duration.TotalMilliseconds;
                slice.MaxMilliseconds = Math.Max(slice.MaxMilliseconds, duration.TotalMilliseconds);
            }
        }

        // The services that took the most time in total first.
        public IReadOnlyList<ServiceTiming> Read(int count)
        {
            var oldest = time.GetUtcNow().UtcTicks / SliceLength.Ticks - SliceCount + 1;
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

            return [.. timings.OrderByDescending(timing => timing.TotalMilliseconds).Take(count)];
        }

        private sealed class Slice
        {
            public long Index = -1;
            public long Count;
            public long Errors;
            public double TotalMilliseconds;
            public double MaxMilliseconds;
        }
    }

    internal sealed record ServiceTiming(string Service, long Count, long Errors, double TotalMilliseconds, double MaxMilliseconds);
}
