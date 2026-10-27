using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Logic.Playback;
using Jularr.Backend.Service.Core;
using Jularr.Integrations.Data.Media;
using Jularr.Integrations.Logic.Media;
using Jularr.Integrations.Logic.Storage;
using Jularr.Integrations.Data.Storage;
using Microsoft.Extensions.Options;

namespace Jularr.Backend.Service.User.Playback.V1
{
    // The input stays readable for FFmpeg until the session's job stops. The weight is what the conversion takes of the server's budget (copied video takes none) and
    // the priority is the planning account's; a source without an encoding converts the way the configuration names.
    internal sealed record PlaybackTranscodeSource(Guid SessionId, long ProfileId, StorageInput Input, int? VideoStreamIndex, int? AudioStreamIndex, bool CopyVideo, bool CopyAudio,
        long DurationMs, int? AudioKilobits = null, int? MaxHeight = null, int? VideoKilobits = null)
    {
        private readonly int? _weight;

        public int SegmentCount => (int)Math.Ceiling(DurationMs / (PlaybackTranscodes.SegmentSeconds * 1000d));

        public int Weight
        {
            get => _weight ?? (CopyVideo ? 0 : TranscodeAdmissionLogic.Weight(MaxHeight));
            init => _weight = value;
        }

        public bool HasPriority { get; init; }
        public TranscodeEncoding? Encoding { get; init; }

        // The audio runs later (positive) or earlier (negative) than the picture by this many milliseconds.
        public int AudioOffsetMs { get; init; }
    }

    // One demand-driven FFmpeg job per playback session.
    internal sealed class PlaybackTranscodes : IDisposable
    {
        public const int SegmentSeconds = 6;
        private const int Lookahead = 1;
        private const int HighWater = 30;
        private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(90);
        private static readonly TimeSpan ExpiryTimeout = TimeSpan.FromHours(6);
        // A running FFmpeg may need long for its first segment, such as on a share that is busy; without one the wait is short.
        private static readonly TimeSpan SegmentTimeout = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan RunningSegmentTimeout = TimeSpan.FromMinutes(2);

        private readonly ConcurrentDictionary<Guid, Job> _jobs = new();
        private readonly MediaTranscoder _transcoder;
        private readonly MediaOptions _options;
        private readonly Timer _sweep;
        private readonly Lock _capacity = new();
        private readonly string? _cacheFilesystemIdentity;
        private long _order;
        private volatile int _maxTranscodes;

        public PlaybackTranscodes(MediaTranscoder transcoder, IOptions<MediaOptions> options)
        {
            _transcoder = transcoder;
            _options = options.Value;
            _maxTranscodes = _options.MaxTranscodes;
            var parent = Path.GetDirectoryName(Path.GetFullPath(_options.TranscodeCachePath));
            _cacheFilesystemIdentity = parent is not null ? StorageFileSystem.DownloadFilesystemIdentity(parent) : null;

            // No playback session survives a process restart; reclaim only our session directories.
            if (CacheFilesystemMatches() && Directory.Exists(_options.TranscodeCachePath))
            {
                foreach (var directory in new DirectoryInfo(_options.TranscodeCachePath).EnumerateDirectories())
                {
                    if (Guid.TryParseExact(directory.Name, "N", out _)) TryDelete(directory.FullName);
                }
            }

            _sweep = new Timer(_ => Sweep(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
        }

        public bool IsAvailable => _transcoder.IsAvailable;

        // The configured number of conversions at once; the Owner's setting replaces it with every plan.
        public int MaxTranscodes
        {
            get => _maxTranscodes;
            set => _maxTranscodes = value;
        }

        public void Register(PlaybackTranscodeSource source) =>
            _jobs[source.SessionId] = new Job(source, Path.Combine(_options.TranscodeCachePath, source.SessionId.ToString("N")), Interlocked.Increment(ref _order));

        // The first candidate whose weight fits the budget is registered; an account with priority makes room by stopping the newest conversions of accounts without it.
        // Without room for any candidate the server is busy.
        public int Admit(IReadOnlyList<int> weights, bool hasPriority, int maxTranscodes, Func<int, PlaybackTranscodeSource> create)
        {
            lock (_capacity)
            {
                RequireCacheCapacity();
                _maxTranscodes = maxTranscodes;
                var admission = TranscodeAdmissionLogic.Decide(Loads(null), TranscodeAdmissionLogic.Capacity(maxTranscodes), hasPriority, weights);
                if (admission.CandidateIndex is not { } index)
                {
                    throw new ServiceUnavailableException("playback_capacity_exhausted");
                }

                Evict(admission.Evicted);
                Register(create(index));
                return index;
            }
        }

        public PlaybackTranscodeSource? Find(Guid sessionId, long profileId)
        {
            if (!_jobs.TryGetValue(sessionId, out var job) || job.Source.ProfileId != profileId)
            {
                return null;
            }

            if (job.CacheUnavailable) throw new ServiceUnavailableException("playback_cache_unavailable");
            return job.Evicted ? throw new ServiceGoneException("playback_evicted") : job.Source;
        }

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
                if (job.Evicted)
                {
                    throw new ServiceGoneException("playback_evicted");
                }
                if (job.CacheUnavailable) throw new ServiceUnavailableException("playback_cache_unavailable");

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
                    // A conversion stopped for room is no failure to retry in software.
                    if (job.Evicted)
                    {
                        throw new ServiceGoneException("playback_evicted");
                    }
                    if (job.CacheUnavailable) throw new ServiceUnavailableException("playback_cache_unavailable");

                    if (job.Process is { HasExited: true } process && !File.Exists(path))
                    {
                        // A failing hardware encoder falls back to software once.
                        if (process.ExitCode != 0 && !job.SoftwareFallback && !job.Source.CopyVideo && !ReferenceEquals(EncodingOf(job), TranscodePipelines.Software))
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

                bool running;
                lock (job)
                {
                    running = job.Process is { HasExited: false };
                }

                if (Stopwatch.GetElapsedTime(started) > (running ? RunningSegmentTimeout : SegmentTimeout))
                {
                    throw new ServiceUnavailableException("playback_conversion_failed");
                }

                await Task.Delay(150, ct);
            }

            RequireCacheCapacity();
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
                job.Source.Input.Dispose();
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

        private TranscodeEncoding EncodingOf(Job job) => job.Source.Encoding
            ?? (_options.VideoEncoderArguments.Length > 0 ? new TranscodeEncoding(_options.HardwareAccelerationArguments, _options.VideoEncoderArguments, null) : TranscodePipelines.Software);

        private void Restart(Job job, int index)
        {
            Kill(job);
            RequireCacheCapacity();

            var encoding = job.SoftwareFallback ? TranscodePipelines.Software : EncodingOf(job);
            var request = new TranscodeRequest(job.Source.Input.Location, job.Directory, index, SegmentSeconds, job.Source.VideoStreamIndex, job.Source.AudioStreamIndex,
                job.Source.CopyVideo, job.Source.CopyAudio, encoding.VideoArguments, encoding.HardwareArguments)
            {
                AudioKilobits = job.Source.AudioKilobits ?? 192,
                AudioOffsetMs = job.Source.AudioOffsetMs,
                MaxHeight = job.Source.MaxHeight,
                VideoKilobits = job.Source.VideoKilobits,
                ScaleFilter = encoding.ScaleFilter
            };

            // Counting and starting share one reservation, so parallel starts respect the budget; copying never waits for room.
            lock (_capacity)
            {
                if (job.Source.Weight > 0)
                {
                    var admission = TranscodeAdmissionLogic.Decide(Loads(job), TranscodeAdmissionLogic.Capacity(_maxTranscodes), job.Source.HasPriority, [job.Source.Weight]);
                    if (admission.CandidateIndex is null)
                    {
                        throw new ServiceUnavailableException("playback_capacity_exhausted");
                    }

                    Evict(admission.Evicted);
                }

                job.Process = _transcoder.Start(request);
            }

            job.StartSegment = index;
        }

        private List<TranscodeLoad> Loads(Job? except) => [.. _jobs.Values.Where(job => job != except && job.CountsAgainstLimit)
            .Select(job => new TranscodeLoad(job.Source.SessionId, job.Source.Weight, job.Source.HasPriority, job.Order))];

        // The player of an evicted conversion is told on its next request; its process stops at once and the sweep clears the rest.
        private void Evict(IReadOnlyList<Guid> sessionIds)
        {
            foreach (var sessionId in sessionIds)
            {
                if (_jobs.TryGetValue(sessionId, out var job))
                {
                    job.Evicted = true;
                    try
                    {
                        job.Process?.Kill(true);
                    }
                    catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
                    {
                    }
                }
            }
        }

        private void Sweep()
        {
            // An external FFmpeg process can grow its segments without an API request.
            if (!CacheWithinBudget())
            {
                var canClean = CacheFilesystemMatches();
                foreach (var job in _jobs.Values)
                {
                    lock (job)
                    {
                        job.CacheUnavailable = true;
                        Kill(job);
                    }

                    // Never delete through a stale mount after the backing filesystem changed.
                    if (canClean) TryDelete(job.Directory);
                }
            }

            foreach (var (sessionId, job) in _jobs)
            {
                // A conversion stopped for room releases what it holds.
                if (job.Evicted)
                {
                    lock (job)
                    {
                        Kill(job);
                    }

                    TryDelete(job.Directory);
                }

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

        private void RequireCacheCapacity()
        {
            if (!CacheWithinBudget()) throw new ServiceUnavailableException("playback_cache_unavailable");
        }

        private bool CacheFilesystemMatches()
        {
            var parent = Path.GetDirectoryName(Path.GetFullPath(_options.TranscodeCachePath));
            return parent is not null && Directory.Exists(parent)
                && StorageFileSystem.DownloadFilesystemIdentity(parent) == _cacheFilesystemIdentity;
        }

        private bool CacheWithinBudget()
        {
            if (_options.MaxTranscodeCacheBytes <= 0 || _options.TranscodeCacheFreeSpaceReserveBytes < 0) return false;
            try
            {
                if (!CacheFilesystemMatches()) return false;
                Directory.CreateDirectory(_options.TranscodeCachePath);
                var available = StorageFileSystem.AvailableFreeSpace(_options.TranscodeCachePath);
                if (available is null || available <= _options.TranscodeCacheFreeSpaceReserveBytes) return false;
                var state = new StorageScanState();
                var used = StorageFileSystem.EnumerateFiles(_options.TranscodeCachePath, null, state, CancellationToken.None, includeHidden: true)
                    .Sum(file => file.SizeBytes);
                return state.IsComplete && used < _options.MaxTranscodeCacheBytes;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
            {
                return false;
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

        private sealed class Job(PlaybackTranscodeSource source, string directory, long order)
        {
            public PlaybackTranscodeSource Source { get; } = source;
            public string Directory { get; } = directory;
            public long Order { get; } = order;
            public Process? Process { get; set; }
            public int StartSegment { get; set; }
            public int LastRequested { get; set; }
            public long LastAccess { get; set; } = Stopwatch.GetTimestamp();
            public bool SoftwareFallback { get; set; }
            public volatile bool Evicted;
            public volatile bool CacheUnavailable;

            public bool IsRunning
            {
                get
                {
                    try
                    {
                        return Process is { HasExited: false };
                    }
                    catch (InvalidOperationException)
                    {
                        return false;
                    }
                }
            }

            // A conversion holds budget while it runs and for the moment before its first segment or the next seek; an idle one is stopped by the sweep.
            public bool CountsAgainstLimit => Source.Weight > 0 && !Evicted && (IsRunning || Stopwatch.GetElapsedTime(LastAccess) <= IdleTimeout);

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
