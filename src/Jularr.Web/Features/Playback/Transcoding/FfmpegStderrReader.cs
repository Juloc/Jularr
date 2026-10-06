namespace Jularr.Web.Features.Playback.Transcoding;

/// <summary>
/// What one running ffmpeg writes to stderr, split into the two things it carries: <c>-progress</c> blocks (reported as samples) and
/// diagnostic lines (kept as a bounded tail whose last line explains a failure). Every process owner feeds its lines here, so progress
/// can never replace the failing step as the error summary and a stream that runs for hours holds a bounded amount of text. Feeding is
/// thread-safe; lines arrive on whichever thread reads the pipe.
/// </summary>
public sealed class FfmpegStderrReader
{
    public const int MaxTailLines = 20;
    public const int MaxLineLength = 200;

    private readonly Lock _gate = new();
    private readonly FfmpegProgressParser _parser = new();
    private readonly Queue<string> _tail = new();

    /// <summary>Raised for every finished progress block, on the thread that fed the closing line.</summary>
    public event Action<PlaybackTranscodeSample>? ProgressReported;

    /// <summary>The last diagnostic line, cut to <see cref="MaxLineLength"/>; empty when ffmpeg has said nothing but progress.</summary>
    public string LastLine
    {
        get
        {
            lock (_gate)
            {
                return _tail.Count == 0 ? "" : _tail.Last();
            }
        }
    }

    /// <summary>The last diagnostic lines, oldest first.</summary>
    public string Tail
    {
        get
        {
            lock (_gate)
            {
                return string.Join('\n', _tail);
            }
        }
    }

    public void Feed(string? rawLine)
    {
        if (string.IsNullOrWhiteSpace(rawLine))
        {
            return;
        }

        var line = rawLine.Trim();
        PlaybackTranscodeSample? sample;
        lock (_gate)
        {
            if (!_parser.TryFeed(line, out sample))
            {
                _tail.Enqueue(line.Length <= MaxLineLength ? line : line[..MaxLineLength]);
                if (_tail.Count > MaxTailLines)
                {
                    _tail.Dequeue();
                }
            }
        }

        if (sample is not null)
        {
            ProgressReported?.Invoke(sample);
        }
    }
}
