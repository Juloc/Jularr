using System.Collections.Concurrent;
using Jularr.Integrations.Logic.Common;

namespace Jularr.Backend.Service.Core
{
    // What this process knows of its downloaders' pace: the speed limits of Jularr's own Usenet and torrent downloaders, the short speed history of every client
    // for the Downloads page and what the last pass of each news server measured. Nothing here is stored; a restart starts the lines again.
    internal sealed class DownloadSpeeds(TimeProvider time)
    {
        // A sample every few seconds, two minutes and a half of them.
        public static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(5);
        public const int SampleCount = 30;

        // A pass that ended longer ago than this holds no connections any more.
        private static readonly TimeSpan PassMemory = TimeSpan.FromMinutes(2);

        private readonly ConcurrentDictionary<string, SpeedLine> _lines = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<long, NewsServerPass> _servers = new();

        public ByteRateLimiter Usenet { get; } = new();
        public ByteRateLimiter Torrent { get; } = new();

        // The client's speed now, as one sample of its line; within one interval only the newest value counts.
        public long[] Record(string client, long bytesPerSecond)
        {
            var line = _lines.GetOrAdd(client, _ => new SpeedLine());
            lock (line)
            {
                var slot = time.GetUtcNow().Ticks / SampleInterval.Ticks;
                if (line.Slot == slot && line.Samples.Count > 0)
                {
                    line.Samples[^1] = bytesPerSecond;
                }
                else
                {
                    // Intervals nobody looked at were quiet or unknown: the line restarts after a long gap.
                    if (slot - line.Slot > SampleCount) line.Samples.Clear();
                    line.Samples.Add(bytesPerSecond);
                    if (line.Samples.Count > SampleCount) line.Samples.RemoveAt(0);
                    line.Slot = slot;
                }

                return [.. line.Samples];
            }
        }

        // One pass of Jularr's Usenet downloader against a news server: its connections, what it fetched in how long, and its articles with the missing ones.
        public void RecordServer(long connectionId, int connections, long bytes, TimeSpan duration, int articles, int missing)
        {
            _servers.AddOrUpdate(connectionId, _ => new NewsServerPass(connections, Pace(bytes, duration), articles, missing, time.GetUtcNow()),
                (_, last) => new NewsServerPass(connections, Pace(bytes, duration), last.Articles + articles, last.Missing + missing, time.GetUtcNow()));
        }

        // What the server's last pass measured; a pass long over holds no connections and fetches nothing.
        public NewsServerPass? Server(long connectionId)
        {
            if (!_servers.TryGetValue(connectionId, out var pass)) return null;
            return time.GetUtcNow() - pass.At > PassMemory ? pass with { Connections = 0, BytesPerSecond = 0 } : pass;
        }

        private static long Pace(long bytes, TimeSpan duration) => duration.TotalSeconds >= 1 ? (long)(bytes / duration.TotalSeconds) : bytes;

        private sealed class SpeedLine
        {
            public long Slot { get; set; }
            public List<long> Samples { get; } = [];
        }
    }

    internal sealed record NewsServerPass(int Connections, long BytesPerSecond, int Articles, int Missing, DateTimeOffset At);
}
