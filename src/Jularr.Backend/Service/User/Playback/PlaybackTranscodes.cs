using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Service.Core;
using Jularr.Integrations.Data.Media;
using Jularr.Integrations.Logic.Media;
using Microsoft.Extensions.Options;

namespace Jularr.Backend.Service.User.Playback
{
    internal sealed record PlaybackTranscodeSource(Guid SessionId, long ProfileId, string InputPath, int? VideoStreamIndex, int? AudioStreamIndex, bool CopyVideo, bool CopyAudio,
        long DurationMs)
    {
        public int SegmentCount => (int)Math.Ceiling(DurationMs / (PlaybackTranscodes.SegmentSeconds * 1000d));
    }

    // One demand-driven FFmpeg job per playback session.
    internal sealed class PlaybackTranscodes : IDisposable
    {
        public const int SegmentSeconds = 6;
        private const int Lookahead = 1;
        private const int HighWater = 30;
        private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(90);
        private static readonly TimeSpan ExpiryTimeout = TimeSpan.FromHours(6);
        private static readonly TimeSpan SegmentTimeout = TimeSpan.FromSeconds(30);

        private readonly ConcurrentDictionary<Guid, Job> _jobs = new();
        private readonly MediaTranscoder _transcoder;
        private readonly MediaOptions _options;
        private readonly Timer _sweep;
        private readonly Lock _capacity = new();

        public PlaybackTranscodes(MediaTranscoder transcoder, IOptions<MediaOptions> options)
        {
            _transcoder = transcoder;
            _options = options.Value;

            // Abandoned segments of earlier processes are removed.
            if (Directory.Exists(_options.TranscodeCachePath))
            {
                foreach (var directory in new DirectoryInfo(_options.TranscodeCachePath).EnumerateDirectories())
                {
                    if (DateTime.UtcNow - directory.LastWriteTimeUtc > ExpiryTimeout) TryDelete(directory.FullName);
                }
            }

            _sweep = new Timer(_ => Sweep(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
        }

        public bool IsAvailable => _transcoder.IsAvailable;

        public void Register(PlaybackTranscodeSource source) =>
            _jobs[source.SessionId] = new Job(source, Path.Combine(_options.TranscodeCachePath, source.SessionId.ToString("N")));

        public PlaybackTranscodeSource? Find(Guid sessionId, long profileId) =>
            _jobs.TryGetValue(sessionId, out var job) && job.Source.ProfileId == profileId ? job.Source : null;

        public async Task<string> GetSegmentAsync(Guid sessionId, long profileId, int index, CancellationToken ct)
        {
            if (!_jobs.TryGetValue(sessionId, out var job) || job.Source.ProfileId != profileId || index < 0 || index >= job.Source.SegmentCount)
            {
                throw new KeyNotFoundException("The playback segment is unavailable.");
            }

            // Seeks outside the running window restart the job.
            var path = job.SegmentPath(index);
            lock (job)
            {
                job.LastRequested = index;
                job.LastAccess = Stopwatch.GetTimestamp();
                if (!File.Exists(path) && !job.Covers(index))
                {
                    Restart(job, index);
                }
            }

            var started = Stopwatch.GetTimestamp();
            while (!File.Exists(path))
            {
                lock (job)
                {
                    if (job.Process is { HasExited: true } process && !File.Exists(path))
                    {
                        // A failing hardware encoder falls back to software once.
                        if (process.ExitCode != 0 && !job.SoftwareFallback && !job.Source.CopyVideo && _options.VideoEncoderArguments.Length > 0)
                        {
                            job.SoftwareFallback = true;
                            Restart(job, index);
                        }
                        else
                        {
                            throw new ServiceUnavailableException("playback_conversion_failed");
                        }
                    }
                }

                if (Stopwatch.GetElapsedTime(started) > SegmentTimeout)
                {
                    throw new ServiceUnavailableException("playback_conversion_failed");
                }

                await Task.Delay(150, ct);
            }

            return path;
        }

        public void Stop(Guid sessionId)
        {
            if (_jobs.TryRemove(sessionId, out var job))
            {
                lock (job)
                {
                    Kill(job);
                }

                TryDelete(job.Directory);
            }
        }

        internal static string BuildPlaylist(PlaybackTranscodeSource source)
        {
            // VOD playlist on a fixed grid; segments are produced on demand.
            var playlist = new StringBuilder("#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-TARGETDURATION:" + SegmentSeconds + "\n#EXT-X-MEDIA-SEQUENCE:0\n#EXT-X-PLAYLIST-TYPE:VOD\n");
            for (var index = 0; index < source.SegmentCount; index++)
            {
                var duration = Math.Min(SegmentSeconds, source.DurationMs / 1000d - index * SegmentSeconds);
                playlist.Append("#EXTINF:").Append(duration.ToString("0.000000", CultureInfo.InvariantCulture)).Append(",\nsegments/").Append(index).Append(".ts\n");
            }

            return playlist.Append("#EXT-X-ENDLIST\n").ToString();
        }

        private void Restart(Job job, int index)
        {
            Kill(job);

            var software = job.SoftwareFallback || _options.VideoEncoderArguments.Length == 0;
            var request = new TranscodeRequest(job.Source.InputPath, job.Directory, index, SegmentSeconds, job.Source.VideoStreamIndex, job.Source.AudioStreamIndex,
                job.Source.CopyVideo, job.Source.CopyAudio, software ? MediaOptions.SoftwareEncoderArguments : _options.VideoEncoderArguments,
                software ? [] : _options.HardwareAccelerationArguments);

            // Counting and starting share one reservation, so parallel starts respect the budget.
            lock (_capacity)
            {
                if (_jobs.Values.Count(other => other != job && other.IsRunning) >= _options.MaxTranscodes)
                {
                    throw new ServiceUnavailableException("playback_capacity_exhausted");
                }

                job.Process = _transcoder.Start(request);
            }

            job.StartSegment = index;
        }

        private void Sweep()
        {
            foreach (var (sessionId, job) in _jobs)
            {
                // Paused sessions keep their cache until they expire.
                var idle = Stopwatch.GetElapsedTime(job.LastAccess);
                if (idle > ExpiryTimeout)
                {
                    Stop(sessionId);
                    continue;
                }

                // Bound work while idle or far ahead of the viewer.
                lock (job)
                {
                    if (job.IsRunning && (idle > IdleTimeout || job.Produced() > job.LastRequested + HighWater))
                    {
                        Kill(job);
                    }
                }
            }
        }

        private static void Kill(Job job)
        {
            if (job.Process is not { } process)
            {
                return;
            }

            try
            {
                if (!process.HasExited) process.Kill(true);
            }
            catch (InvalidOperationException)
            {
            }

            process.Dispose();
            job.Process = null;
        }

        private static void TryDelete(string directory)
        {
            try
            {
                Directory.Delete(directory, true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }

        public void Dispose()
        {
            _sweep.Dispose();
            foreach (var sessionId in _jobs.Keys)
            {
                Stop(sessionId);
            }
        }

        private sealed class Job(PlaybackTranscodeSource source, string directory)
        {
            public PlaybackTranscodeSource Source { get; } = source;
            public string Directory { get; } = directory;
            public Process? Process { get; set; }
            public int StartSegment { get; set; }
            public int LastRequested { get; set; }
            public long LastAccess { get; set; } = Stopwatch.GetTimestamp();
            public bool SoftwareFallback { get; set; }
            public bool IsRunning => Process is { HasExited: false };

            public string SegmentPath(int index) => Path.Combine(Directory, "segment" + index.ToString(CultureInfo.InvariantCulture) + ".ts");

            public int Produced()
            {
                var index = StartSegment;
                while (File.Exists(SegmentPath(index))) index++;
                return index - 1;
            }

            public bool Covers(int index) => IsRunning && index >= StartSegment && index <= Produced() + Lookahead;
        }
    }
}
