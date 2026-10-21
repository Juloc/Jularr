using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Jularr.Integrations.Data.Common;
using Jularr.Integrations.Data.Media;

namespace Jularr.Integrations.Logic.Media
{
    // What one FFmpeg run printed and how long it took; a run that was stopped has the exit code -1.
    internal sealed record FfmpegRun(int ExitCode, string Output, TimeSpan Elapsed);

    // Finds the encoders this machine offers and times short real conversions through each of them.
    internal sealed partial class TranscodeBenchmarkClient(MediaTranscoder transcoder)
    {
        // A clip of synthetic picture, converted by every available pipeline; a slow one is stopped.
        public const int ClipSeconds = 4;
        public static readonly int[] ClipHeights = [1080, 2160];
        private const int ScaledHeight = 1080;
        private static readonly TimeSpan RunLimit = TimeSpan.FromSeconds(40);
        private static readonly TimeSpan ProbeLimit = TimeSpan.FromSeconds(15);

        public bool IsAvailable => transcoder.IsAvailable;

        // Runs FFmpeg with these arguments; tests replace it.
        internal Func<IReadOnlyList<string>, TimeSpan, CancellationToken, Task<FfmpegRun>>? Runner { get; set; }

        // The render nodes and device files of the machine; tests replace them.
        internal Func<IReadOnlyList<string>> RenderNodes { get; set; } = () => Directory.Exists("/dev/dri") ? [.. Directory.GetFiles("/dev/dri", "renderD*").Order(StringComparer.Ordinal)] : [];
        internal Func<string, bool> DeviceExists { get; set; } = File.Exists;

        public async Task<IReadOnlyList<TranscodeHardwareData>> DetectAsync(CancellationToken ct)
        {
            if (!IsAvailable)
            {
                throw new ProviderUnavailableException();
            }

            var methods = ParseHardwareMethods((await RunAsync(["-hide_banner", "-hwaccels"], ProbeLimit, ct)).Output);
            var encoders = ParseEncoders((await RunAsync(["-hide_banner", "-encoders"], ProbeLimit, ct)).Output);
            var node = RenderNodes().FirstOrDefault();
            var result = new List<TranscodeHardwareData>();
            foreach (var pipeline in Enum.GetValues<TranscodePipeline>())
            {
                var (encoder, acceleration) = TranscodePipelines.Names(pipeline);
                var device = pipeline is TranscodePipeline.Vaapi or TranscodePipeline.Qsv ? node : null;
                var present = encoders.Contains(encoder) && (acceleration is null || methods.Contains(acceleration));
                var reachable = pipeline switch
                {
                    TranscodePipeline.Vaapi or TranscodePipeline.Qsv => node is not null,
                    TranscodePipeline.Nvenc => !OperatingSystem.IsLinux() || DeviceExists("/dev/nvidiactl"),
                    TranscodePipeline.VideoToolbox => OperatingSystem.IsMacOS(),
                    _ => true
                };
                result.Add(new TranscodeHardwareData(pipeline, encoder, device, present && reachable));
            }

            return result;
        }

        // Every available pipeline converts a 1080p clip and a 4K clip scaled down to 1080p, the way playback does.
        public async Task<IReadOnlyList<TranscodeSpeedData>> MeasureAsync(IReadOnlyList<TranscodeHardwareData> hardware, CancellationToken ct)
        {
            var folder = Path.Combine(Path.GetTempPath(), "jularr-benchmark-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try
            {
                var clips = new Dictionary<int, string>();
                foreach (var height in ClipHeights)
                {
                    var clip = Path.Combine(folder, "clip" + height.ToString(CultureInfo.InvariantCulture) + ".mp4");
                    var width = height * 16 / 9;
                    var made = await RunAsync(["-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-f", "lavfi", "-i", string.Create(CultureInfo.InvariantCulture, $"testsrc2=size={width}x{height}:rate=30"),
                        "-t", ClipSeconds.ToString(CultureInfo.InvariantCulture), "-an", "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", clip], RunLimit, ct);
                    if (made.ExitCode == 0) clips[height] = clip;
                }

                var results = new List<TranscodeSpeedData>();
                foreach (var candidate in hardware.Where(item => item.IsAvailable))
                {
                    var encoding = TranscodePipelines.For(candidate.Pipeline, candidate.Device);
                    foreach (var height in ClipHeights)
                    {
                        results.Add(new TranscodeSpeedData(candidate.Pipeline, height, clips.TryGetValue(height, out var clip) ? await TimeAsync(encoding, clip, height, candidate.Pipeline == TranscodePipeline.Software, ct) : null));
                    }
                }

                return results;
            }
            finally
            {
                try
                {
                    Directory.Delete(folder, true);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                }
            }
        }

        // A conversion that was stopped is slow in software, but a hardware pipeline that hangs does not work.
        private async Task<decimal?> TimeAsync(TranscodeEncoding encoding, string clip, int height, bool isSoftware, CancellationToken ct)
        {
            // The same arguments playback uses, reading the clip and discarding the result.
            List<string> arguments = ["-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-progress", "pipe:1", "-nostats", .. encoding.HardwareArguments, "-i", clip, "-an"];
            arguments.AddRange(encoding.VideoArguments);
            if (height > ScaledHeight && encoding.ScaleFilter is { } scale)
            {
                arguments.AddRange(["-vf", scale.Replace("{height}", ScaledHeight.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)]);
            }

            arguments.AddRange(["-f", "null", "-"]);
            var run = await RunAsync(arguments, RunLimit, ct);
            if (run.ExitCode == -1)
            {
                return isSoftware ? Math.Round((decimal)ClipSeconds / (decimal)RunLimit.TotalSeconds, 2) : null;
            }

            if (run.ExitCode != 0)
            {
                return null;
            }

            // FFmpeg's own speed leaves out the start of the process; without it the wall clock counts.
            var speeds = Speed().Matches(run.Output);
            if (speeds.Count > 0 && decimal.TryParse(speeds[^1].Groups[1].Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var speed) && speed > 0)
            {
                return Math.Round(speed, 2);
            }

            return run.Elapsed > TimeSpan.Zero ? Math.Round((decimal)ClipSeconds / (decimal)run.Elapsed.TotalSeconds, 2) : null;
        }

        // The names after the heading, one a line.
        internal static HashSet<string> ParseHardwareMethods(string output) =>
            [.. output.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Skip(1).Where(line => !line.Contains(' ', StringComparison.Ordinal))];

        // Lines such as " V....D h264_vaapi   H.264/AVC (VAAPI)": the flags, then the name.
        internal static HashSet<string> ParseEncoders(string output) =>
            [.. output.Split('\n', StringSplitOptions.RemoveEmptyEntries).SkipWhile(line => !line.TrimStart().StartsWith("---", StringComparison.Ordinal)).Skip(1)
                .Select(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Where(parts => parts.Length >= 2 && parts[0].Length == 6).Select(parts => parts[1])];

        private Task<FfmpegRun> RunAsync(IReadOnlyList<string> arguments, TimeSpan limit, CancellationToken ct) => (Runner ?? RunProcessAsync)(arguments, limit, ct);

        private async Task<FfmpegRun> RunProcessAsync(IReadOnlyList<string> arguments, TimeSpan limit, CancellationToken ct)
        {
            var start = new ProcessStartInfo(transcoder.ExecutablePath!)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var argument in arguments)
            {
                start.ArgumentList.Add(argument);
            }

            var watch = Stopwatch.StartNew();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(limit);
            try
            {
                using var process = Process.Start(start) ?? throw new ProviderUnavailableException();
                var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
                var errors = process.StandardError.ReadToEndAsync(timeout.Token);
                try
                {
                    await process.WaitForExitAsync(timeout.Token);
                    return new FfmpegRun(process.ExitCode, new StringBuilder(await output).Append(await errors).ToString(), watch.Elapsed);
                }
                catch (OperationCanceledException)
                {
                    process.Kill(true);
                    ct.ThrowIfCancellationRequested();
                    return new FfmpegRun(-1, "", watch.Elapsed);
                }
            }
            catch (Win32Exception)
            {
                throw new ProviderUnavailableException();
            }
        }

        [GeneratedRegex(@"speed=\s*([0-9]+(?:\.[0-9]+)?)x")]
        private static partial Regex Speed();
    }
}
