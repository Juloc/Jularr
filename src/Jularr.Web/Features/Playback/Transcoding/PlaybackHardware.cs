using System.Text.RegularExpressions;
using Jularr.Web.Features.Playback.Decision;
using Jularr.Web.Infrastructure;

namespace Jularr.Web.Features.Playback.Transcoding;

/// <summary>The H.264 encoders Jularr can drive, in no particular order; see <see cref="PlaybackHardwareBackends.SelectionOrder"/>.</summary>
public enum PlaybackHardwareBackend
{
    Software,
    Nvenc,
    Qsv,
    Vaapi,
    Amf
}

public static class PlaybackHardwareBackends
{
    /// <summary>The binding preference (#403): the first backend that passed its test encode and whose breaker is closed wins, else software.</summary>
    public static readonly IReadOnlyList<PlaybackHardwareBackend> SelectionOrder =
    [
        PlaybackHardwareBackend.Nvenc,
        PlaybackHardwareBackend.Qsv,
        PlaybackHardwareBackend.Vaapi,
        PlaybackHardwareBackend.Amf
    ];

    public static string Name(PlaybackHardwareBackend backend) =>
        backend switch
        {
            PlaybackHardwareBackend.Software => "software",
            PlaybackHardwareBackend.Nvenc => "nvenc",
            PlaybackHardwareBackend.Qsv => "qsv",
            PlaybackHardwareBackend.Vaapi => "vaapi",
            PlaybackHardwareBackend.Amf => "amf",
            _ => throw new ArgumentOutOfRangeException(nameof(backend))
        };

    public static string DisplayName(PlaybackHardwareBackend backend) => backend == PlaybackHardwareBackend.Software ? "Software" : Name(backend).ToUpperInvariant();

    /// <summary>The ffmpeg encoder name; values come from this fixed set only, never from a client.</summary>
    public static string H264Encoder(PlaybackHardwareBackend backend) =>
        backend switch
        {
            PlaybackHardwareBackend.Software => PlaybackServerCapabilities.SoftwareH264Encoder,
            PlaybackHardwareBackend.Nvenc => "h264_nvenc",
            PlaybackHardwareBackend.Qsv => "h264_qsv",
            PlaybackHardwareBackend.Vaapi => "h264_vaapi",
            PlaybackHardwareBackend.Amf => "h264_amf",
            _ => throw new ArgumentOutOfRangeException(nameof(backend))
        };

    /// <summary>The ffmpeg <c>-hwaccel</c> that decodes into the encoder's own frame format; AMF has none Jularr relies on.</summary>
    public static string? HardwareDecoder(PlaybackHardwareBackend backend) =>
        backend switch
        {
            PlaybackHardwareBackend.Nvenc => "cuda",
            PlaybackHardwareBackend.Qsv => "qsv",
            PlaybackHardwareBackend.Vaapi => "vaapi",
            _ => null
        };

    public static PlaybackHardwareBackend FromEncoder(string? encoder) =>
        SelectionOrder.FirstOrDefault(backend => string.Equals(H264Encoder(backend), encoder, StringComparison.Ordinal));
}

/// <summary>
/// The encoder one delivery uses. <see cref="Device"/> is the VAAPI render node;
/// <see cref="HardwareDecoding"/> only says the ffmpeg build offers the matching decoder, the
/// command decides per source whether decoding on the device is safe.
/// </summary>
public sealed record PlaybackEncoderTarget(PlaybackHardwareBackend Backend, string? Device = null, bool HardwareDecoding = false)
{
    public static PlaybackEncoderTarget Software { get; } = new(PlaybackHardwareBackend.Software);

    public bool IsHardware => Backend != PlaybackHardwareBackend.Software;
}

public enum PlaybackBackendState
{
    Available,
    FfmpegUnavailable,
    EncoderMissing,
    NoDevice,
    TestFailed
}

/// <summary>One backend's detection result; <see cref="Detail"/> is the short reason of a failed test, never a command line.</summary>
public sealed record PlaybackBackendStatus(
    PlaybackHardwareBackend Backend,
    PlaybackBackendState State,
    string? Device = null,
    bool HardwareDecoding = false,
    string? Detail = null);

/// <summary>What the ffmpeg check found: a timeout or a failing run is transient and says nothing about whether ffmpeg is installed.</summary>
public enum PlaybackFfmpegState
{
    Available,
    NotFound,
    TimedOut,
    Failed
}

public sealed record PlaybackHardwareCapabilities(DateTimeOffset DetectedAtUtc, PlaybackFfmpegState FfmpegState, IReadOnlyList<PlaybackBackendStatus> Backends)
{
    public bool FfmpegAvailable => FfmpegState == PlaybackFfmpegState.Available;

    public PlaybackBackendStatus? Status(PlaybackHardwareBackend backend) => Backends.FirstOrDefault(x => x.Backend == backend);
}

/// <summary>Where VAAPI render nodes live. Only plain <c>renderD&lt;n&gt;</c> nodes are ever handed to ffmpeg.</summary>
public static partial class PlaybackRenderDevices
{
    private const string DirectoryPath = "/dev/dri";

    [GeneratedRegex(@"^/dev/dri/renderD[0-9]{1,3}$", RegexOptions.CultureInvariant)]
    private static partial Regex RenderNodePattern();

    public static bool IsRenderNode(string? device) => device is not null && RenderNodePattern().IsMatch(device);

    public static IReadOnlyList<string> Discover()
    {
        if (!OperatingSystem.IsLinux() || !Directory.Exists(DirectoryPath))
        {
            return [];
        }

        return [.. Directory.EnumerateFileSystemEntries(DirectoryPath, "renderD*").Where(IsRenderNode).Order(StringComparer.Ordinal)];
    }
}

/// <summary>
/// Finds which hardware encoders this server can really use: ffmpeg must list the encoder and a
/// short test encode of a generated picture must succeed. Nothing is assumed from the encoder
/// list alone, because ffmpeg lists NVENC on machines without an NVIDIA GPU.
/// </summary>
public sealed partial class PlaybackHardwareProbe(
    IMediaProcessRunner runner,
    TimeProvider time,
    Func<IReadOnlyList<string>> renderDevices)
{
    private const string Executable = "ffmpeg";
    public static readonly TimeSpan ListTimeout = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan TestEncodeTimeout = TimeSpan.FromSeconds(20);
    private const int MaxDetailLength = 200;

    public async Task<PlaybackHardwareCapabilities> DetectAsync(CancellationToken cancellationToken)
    {
        var detectedAt = time.GetUtcNow();
        var started = time.GetTimestamp();
        var encoderList = await runner.RunAsync(Executable, ["-hide_banner", "-encoders"], ListTimeout, cancellationToken);
        if (encoderList is not { ExitCode: 0 })
        {
            // The runner answers null both when ffmpeg cannot start and when it ran out of time; only the elapsed time tells them apart.
            var timedOut = encoderList is null && time.GetElapsedTime(started) >= ListTimeout;
            var state = encoderList is null ? (timedOut ? PlaybackFfmpegState.TimedOut : PlaybackFfmpegState.NotFound) : PlaybackFfmpegState.Failed;
            var detail = encoderList is null ? (timedOut ? "ffmpeg did not answer in time." : "ffmpeg was not found.") : Summarize(encoderList.Error);
            return new PlaybackHardwareCapabilities(
                detectedAt,
                state,
                [.. PlaybackHardwareBackends.SelectionOrder.Select(backend => new PlaybackBackendStatus(backend, PlaybackBackendState.FfmpegUnavailable, Detail: detail))]);
        }

        var encoders = ParseEncoders(encoderList.Output);
        var accelerationList = await runner.RunAsync(Executable, ["-hide_banner", "-hwaccels"], ListTimeout, cancellationToken);
        var accelerations = accelerationList is { ExitCode: 0 } ? ParseHardwareAccelerations(accelerationList.Output) : new HashSet<string>();

        var statuses = new List<PlaybackBackendStatus>();
        foreach (var backend in PlaybackHardwareBackends.SelectionOrder)
        {
            var hardwareDecoder = PlaybackHardwareBackends.HardwareDecoder(backend);
            var decoding = hardwareDecoder is not null && accelerations.Contains(hardwareDecoder);
            if (!encoders.Contains(PlaybackHardwareBackends.H264Encoder(backend)))
            {
                statuses.Add(new PlaybackBackendStatus(backend, PlaybackBackendState.EncoderMissing));
                continue;
            }

            statuses.Add(await TestBackendAsync(backend, decoding, cancellationToken));
        }

        return new PlaybackHardwareCapabilities(detectedAt, PlaybackFfmpegState.Available, statuses);
    }

    /// <summary>Encoder names from <c>ffmpeg -encoders</c> lines such as <c> V....D h264_nvenc   NVIDIA NVENC H.264 encoder</c>.</summary>
    public static IReadOnlySet<string> ParseEncoders(string output)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in output.Split('\n'))
        {
            var columns = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            // The legend line "V..... = Video" has the same shape as an encoder line but names no encoder.
            if (columns.Length >= 2 && columns[1] != "=" && EncoderFlags().IsMatch(columns[0]))
            {
                names.Add(columns[1]);
            }
        }

        return names;
    }

    /// <summary>Method names listed after the header of <c>ffmpeg -hwaccels</c>.</summary>
    public static IReadOnlySet<string> ParseHardwareAccelerations(string output)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        var afterHeader = false;
        foreach (var line in output.Split('\n').Select(x => x.Trim()))
        {
            if (line.EndsWith(':'))
            {
                afterHeader = true;
            }
            else if (afterHeader && line.Length > 0)
            {
                names.Add(line);
            }
        }

        return names;
    }

    [GeneratedRegex("^[VAS][A-Za-z.]{5}$", RegexOptions.CultureInvariant)]
    private static partial Regex EncoderFlags();

    private async Task<PlaybackBackendStatus> TestBackendAsync(PlaybackHardwareBackend backend, bool hardwareDecoding, CancellationToken cancellationToken)
    {
        if (backend != PlaybackHardwareBackend.Vaapi)
        {
            return await TestEncodeAsync(new PlaybackEncoderTarget(backend, null, hardwareDecoding), cancellationToken);
        }

        var devices = renderDevices().Where(PlaybackRenderDevices.IsRenderNode).ToArray();
        if (devices.Length == 0)
        {
            return new PlaybackBackendStatus(backend, PlaybackBackendState.NoDevice);
        }

        // The first render node that really encodes wins; the last failure explains the others.
        PlaybackBackendStatus? failure = null;
        foreach (var device in devices)
        {
            var status = await TestEncodeAsync(new PlaybackEncoderTarget(backend, device, hardwareDecoding), cancellationToken);
            if (status.State == PlaybackBackendState.Available)
            {
                return status;
            }

            failure = status;
        }

        return failure!;
    }

    private async Task<PlaybackBackendStatus> TestEncodeAsync(PlaybackEncoderTarget target, CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync(Executable, PlaybackDeliveryCommand.HardwareProbe(target), TestEncodeTimeout, cancellationToken);
        if (result is null)
        {
            return new PlaybackBackendStatus(target.Backend, PlaybackBackendState.TestFailed, target.Device, Detail: "The test encode timed out or could not start.");
        }

        return result.ExitCode == 0
            ? new PlaybackBackendStatus(target.Backend, PlaybackBackendState.Available, target.Device, target.HardwareDecoding)
            : new PlaybackBackendStatus(target.Backend, PlaybackBackendState.TestFailed, target.Device, Detail: Summarize(result.Error));
    }

    // The last stderr line names the cause (ffmpeg prints the failing step last); it is cut so a verbose driver cannot flood the page.
    private static string Summarize(string error)
    {
        var line = error.Split('\n').Select(x => x.Trim()).LastOrDefault(x => x.Length > 0) ?? "ffmpeg reported an error.";
        return line.Length <= MaxDetailLength ? line : line[..MaxDetailLength];
    }
}
