using System.Diagnostics;
using System.Globalization;
using Jularr.Web.Features.Playback.Transcoding;

namespace Jularr.Web.Features.Playback;

public static class LivePlaybackCommand
{
    /// <summary>
    /// Builds the ffmpeg command for one live fragmented-MP4 stream.
    /// <paramref name="audioStreamIndex"/> selects a specific audio stream by
    /// its ffprobe index instead of the first audio stream; the quality cap
    /// only affects an H.264 encode the plan already requires.
    /// </summary>
    public static IReadOnlyList<string> BuildArguments(
        string sourcePath,
        PlaybackPreparationPlan plan,
        double startSeconds = 0,
        int? audioStreamIndex = null,
        PlaybackQualityCap qualityCap = PlaybackQualityCap.Auto)
    {
        if (!plan.CanPrepare || plan.Kind is null)
        {
            throw new ArgumentException("Playback plan cannot be streamed.", nameof(plan));
        }

        if (!double.IsFinite(startSeconds) || startSeconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(startSeconds));
        }

        if (audioStreamIndex is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(audioStreamIndex));
        }

        var arguments = new List<string>
        {
            "-v", "error",
            "-nostdin",
            "-fflags", "+genpts"
        };

        if (startSeconds > 0)
        {
            arguments.AddRange([
                "-ss",
                startSeconds.ToString("0.###", CultureInfo.InvariantCulture)
            ]);
        }

        arguments.AddRange([
            "-i", Path.GetFullPath(sourcePath),
            "-map", "0:v:0",
            "-sn",
            "-dn"
        ]);

        if (plan.VideoMode == PlaybackVideoMode.Copy)
        {
            arguments.AddRange(["-c:v", "copy"]);
            if (plan.TagHevcAsHvc1)
            {
                arguments.AddRange(["-tag:v", "hvc1"]);
            }
        }
        else
        {
            arguments.AddRange([
                "-c:v", "libx264",
                "-preset", "veryfast",
                "-crf", "22",
                "-pix_fmt", "yuv420p"
            ]);
            arguments.AddRange(PlaybackQuality.EncodeArguments(qualityCap));
        }

        var audioMap = AudioMap(audioStreamIndex);
        switch (plan.AudioMode)
        {
            case PlaybackAudioMode.Copy:
                arguments.AddRange(["-map", audioMap, "-c:a", "copy"]);
                break;
            case PlaybackAudioMode.Aac:
                arguments.AddRange([
                    "-map", audioMap,
                    "-c:a", "aac",
                    "-b:a", PlaybackQuality.AudioBitrate(qualityCap)
                ]);
                break;
        }

        arguments.AddRange([
            "-max_muxing_queue_size", "2048",
            "-avoid_negative_ts", "make_zero",
            "-movflags", "+frag_keyframe+empty_moov+default_base_moof",
            "-frag_duration", "1000000",
            "-f", "mp4",
            "pipe:1"
        ]);

        return arguments;
    }

    /// <summary>ffmpeg map selector: a concrete stream index, or the first audio stream when present.</summary>
    public static string AudioMap(int? audioStreamIndex) =>
        audioStreamIndex is { } index
            ? $"0:{index.ToString(CultureInfo.InvariantCulture)}"
            : "0:a:0?";
}

public sealed class LivePlaybackStream : Stream
{
    private const int PrefixBufferSize = 16 * 1024;
    private const int MaxDiagnosticLines = 20;
    private const int MaxDiagnosticLineLength = 200;

    private readonly Stream _output;
    private readonly Task<string> _errors;
    private readonly Action _endProcess;
    private readonly IDisposable? _lease;
    private bool _disposed;
    private byte[] _prefix = [];
    private int _prefixOffset;

    private LivePlaybackStream(Stream output, Task<string> errors, Action endProcess, IDisposable? lease)
    {
        _output = output;
        _errors = errors;
        _endProcess = endProcess;
        _lease = lease;
    }

    /// <summary>
    /// Wraps the output of an already running encoder. <paramref name="endProcess"/> kills and cleans up the process; it runs
    /// on dispose, together with the pipe and the lease. This is the seam that tests a silent or dying encoder without ffmpeg.
    /// </summary>
    public static LivePlaybackStream Wrap(Stream output, Task<string> errors, Action endProcess, IDisposable? lease = null) =>
        new(output, errors, endProcess, lease);

    public static LivePlaybackStream Start(
        string sourcePath,
        PlaybackPreparationPlan plan,
        double startSeconds = 0,
        int? audioStreamIndex = null,
        PlaybackQualityCap qualityCap = PlaybackQualityCap.Auto,
        IDisposable? lease = null) =>
        Start(
            LivePlaybackCommand.BuildArguments(
                sourcePath,
                plan,
                startSeconds,
                audioStreamIndex,
                qualityCap),
            lease);

    /// <summary>
    /// Starts ffmpeg with prepared arguments writing fragmented MP4 to stdout. The optional
    /// lease (a transcode slot) is released when the response stream is disposed; when the
    /// start itself throws, the lease is not touched and stays with the caller. <paramref name="onProgress"/> receives the measured
    /// progress of the encoder while it runs (its <c>-progress</c> blocks share the stderr pipe).
    /// </summary>
    public static LivePlaybackStream Start(
        IReadOnlyList<string> arguments,
        IDisposable? lease = null,
        Action<PlaybackTranscodeSample>? onProgress = null)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "ffmpeg",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        if (!process.Start())
        {
            process.Dispose();
            throw new InvalidOperationException("Could not start ffmpeg playback stream.");
        }

        return new LivePlaybackStream(process.StandardOutput.BaseStream, ReadDiagnosticsAsync(process.StandardError, onProgress), () => EndProcess(process), lease);
    }

    /// <summary>
    /// Drains stderr for the life of the process (a full pipe would stall the encoder). Progress blocks go to <paramref name="onProgress"/>;
    /// only the last few diagnostic lines are kept, so a stream that runs for hours holds a bounded amount of text.
    /// </summary>
    public static async Task<string> ReadDiagnosticsAsync(TextReader reader, Action<PlaybackTranscodeSample>? onProgress)
    {
        var parser = new FfmpegProgressParser();
        var tail = new Queue<string>();
        try
        {
            while (await reader.ReadLineAsync() is { } line)
            {
                if (parser.TryFeed(line, out var sample))
                {
                    if (sample is not null)
                    {
                        onProgress?.Invoke(sample);
                    }

                    continue;
                }

                if (!string.IsNullOrWhiteSpace(line))
                {
                    tail.Enqueue(line.Length <= MaxDiagnosticLineLength ? line : line[..MaxDiagnosticLineLength]);
                    if (tail.Count > MaxDiagnosticLines)
                    {
                        tail.Dequeue();
                    }
                }
            }
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            // Ending the stream disposes the process and its pipes; what was read until then is the diagnostic.
        }

        return string.Join('\n', tail);
    }

    /// <summary>
    /// Waits until ffmpeg has produced its first bytes, which the stream then serves first. A response
    /// that has not started can still fail over to another encoder; once bytes flow it cannot. Throws
    /// <see cref="InvalidOperationException"/> when ffmpeg ends without output and
    /// <see cref="TimeoutException"/> when it stays silent for <paramref name="timeout"/>.
    /// </summary>
    public async Task WaitForFirstBytesAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        // A pipe read of a child process does not honour cancellation once it started, so the read is raced against the
        // timeout and the caller's token. Losing the race ends the process and closes the pipe, which also frees the lease.
        var buffer = new byte[PrefixBufferSize];
        var read = _output.ReadAsync(buffer, CancellationToken.None).AsTask();
        using var waitSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var wait = Task.Delay(timeout, waitSource.Token);
        var finished = await Task.WhenAny(read, wait);
        await waitSource.CancelAsync();
        if (finished != read)
        {
            Dispose();
            _ = read.ContinueWith(static finished => _ = finished.Exception, TaskContinuationOptions.OnlyOnFaulted);
            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException("ffmpeg produced no output in time.");
        }

        var bytes = await read;
        if (bytes == 0)
        {
            var detail = await Task.WhenAny(_errors, Task.Delay(TimeSpan.FromSeconds(1), cancellationToken)) == _errors
                ? _errors.Result.Trim().Split('\n')[^1].Trim()
                : "";
            throw new InvalidOperationException($"ffmpeg exited before producing output: {(detail.Length <= 200 ? detail : detail[..200])}");
        }

        _prefix = buffer[..bytes];
        _prefixOffset = 0;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        TakePrefix(buffer.AsSpan(offset, count)) is var served and > 0 ? served : _output.Read(buffer, offset, count);

    public override async Task<int> ReadAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken) =>
        TakePrefix(buffer.AsSpan(offset, count)) is var served and > 0 ? served : await _output.ReadAsync(buffer, offset, count, cancellationToken);

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default) =>
        TakePrefix(buffer.Span) is var served and > 0 ? served : await _output.ReadAsync(buffer, cancellationToken);

    public override long Seek(long offset, SeekOrigin origin) =>
        throw new NotSupportedException();

    public override void SetLength(long value) =>
        throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException();

    // The bytes read ahead by WaitForFirstBytesAsync belong at the front of the response.
    private int TakePrefix(Span<byte> destination)
    {
        var available = _prefix.Length - _prefixOffset;
        if (available <= 0 || destination.IsEmpty)
        {
            return 0;
        }

        var count = Math.Min(available, destination.Length);
        _prefix.AsSpan(_prefixOffset, count).CopyTo(destination);
        _prefixOffset += count;
        return count;
    }

    protected override void Dispose(bool disposing)
    {
        if (!disposing || _disposed)
        {
            base.Dispose(disposing);
            return;
        }

        _disposed = true;
        try
        {
            // The process is ended first so a blocked pipe read is released by its exit, then the pipe is closed.
            _endProcess();
        }
        finally
        {
            try
            {
                _output.Dispose();
            }
            finally
            {
                _lease?.Dispose();
            }
        }

        base.Dispose(disposing);
    }

    private static void EndProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // The process exited between the check and the kill: it is gone, which is what the caller wants.
        }

        process.Dispose();
    }
}
