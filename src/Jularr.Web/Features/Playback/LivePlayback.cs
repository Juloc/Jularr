using System.Diagnostics;
using System.Globalization;
using Jularr.Web.Features.Playback.Decision;

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
    private readonly Process _process;
    private readonly Stream _output;
    private readonly Task<string> _stderrDrain;
    private bool _disposed;

    private readonly IDisposable? _lease;
    private readonly Action<PlaybackStartFailure?>? _onOutcome;
    private int _outcomeReported;
    private bool _producedOutput;

    private LivePlaybackStream(Process process, IDisposable? lease, Action<PlaybackStartFailure?>? onOutcome)
    {
        _process = process;
        _lease = lease;
        _onOutcome = onOutcome;
        _output = process.StandardOutput.BaseStream;
        _stderrDrain = process.StandardError.ReadToEndAsync();
    }

    public static LivePlaybackStream Start(
        string sourcePath,
        PlaybackPreparationPlan plan,
        double startSeconds = 0,
        int? audioStreamIndex = null,
        PlaybackQualityCap qualityCap = PlaybackQualityCap.Auto) =>
        Start(LivePlaybackCommand.BuildArguments(
            sourcePath,
            plan,
            startSeconds,
            audioStreamIndex,
            qualityCap));

    /// <summary>
    /// Starts ffmpeg with prepared arguments writing fragmented MP4 to stdout. The optional
    /// lease (a transcode slot) is released when the response stream is disposed. <paramref name="onOutcome"/>
    /// is called once: with null when the first bytes arrive (the encoder works) or with a failure when ffmpeg
    /// exits with an error before producing any.
    /// </summary>
    public static LivePlaybackStream Start(
        IReadOnlyList<string> arguments,
        IDisposable? lease = null,
        Action<PlaybackStartFailure?>? onOutcome = null)
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

        return new LivePlaybackStream(process, lease, onOutcome);
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
        Produced(_output.Read(buffer, offset, count));

    public override async Task<int> ReadAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken) =>
        Produced(await _output.ReadAsync(buffer, offset, count, cancellationToken));

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default) =>
        Produced(await _output.ReadAsync(buffer, cancellationToken));

    public override long Seek(long offset, SeekOrigin origin) =>
        throw new NotSupportedException();

    public override void SetLength(long value) =>
        throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException();

    // The first bytes prove the encoder works; later reads need no further reporting.
    private int Produced(int bytesRead)
    {
        if (bytesRead > 0 && !_producedOutput)
        {
            _producedOutput = true;
            ReportOutcome(null);
        }

        return bytesRead;
    }

    private void ReportOutcome(PlaybackStartFailure? failure)
    {
        if (Interlocked.Exchange(ref _outcomeReported, 1) == 0)
        {
            _onOutcome?.Invoke(failure);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (!disposing || _disposed)
        {
            base.Dispose(disposing);
            return;
        }

        _disposed = true;

        // Judged before the kill below: a process this stream ends itself must not look like an encoder failure.
        if (!_producedOutput && _onOutcome is not null && _process.HasExited && _process.ExitCode != 0)
        {
            var detail = _stderrDrain.IsCompletedSuccessfully ? _stderrDrain.Result.Trim().Split('\n')[^1].Trim() : null;
            ReportOutcome(new PlaybackStartFailure(PlaybackStartFailure.StartFailed, detail is { Length: > 200 } ? detail[..200] : detail));
        }

        try
        {
            _output.Dispose();
        }
        finally
        {
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
            }

            _process.Dispose();
            _ = _stderrDrain;
            _lease?.Dispose();
        }

        base.Dispose(disposing);
    }
}
