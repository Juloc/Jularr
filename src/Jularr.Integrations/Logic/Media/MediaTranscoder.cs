using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using Jularr.Integrations.Data.Common;
using Jularr.Integrations.Data.Media;

namespace Jularr.Integrations.Logic.Media
{
    internal sealed class MediaTranscoder
    {
        // Fast start and seek, then paced near real time.
        private const string ReadRate = "1.5";
        private const string InitialBurstSeconds = "30";

        // A read from the loopback stream server (a file on a direct share) that stalls this long, such as on a NAS that stopped answering, ends the run.
        internal static readonly TimeSpan DefaultInputTimeout = TimeSpan.FromSeconds(30);

        public string? ExecutablePath { get; set; }
        public bool IsAvailable => !string.IsNullOrWhiteSpace(ExecutablePath);
        public TimeSpan InputTimeout { get; set; } = DefaultInputTimeout;

        public Process Start(TranscodeRequest request)
        {
            if (!IsAvailable)
            {
                throw new ProviderUnavailableException();
            }

            Directory.CreateDirectory(request.OutputDirectory);
            var start = new ProcessStartInfo(ExecutablePath!)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var argument in BuildArguments(request, InputTimeout))
            {
                start.ArgumentList.Add(argument);
            }

            try
            {
                var process = Process.Start(start) ?? throw new ProviderUnavailableException();

                // Drain diagnostics so a full pipe never blocks encoding.
                process.OutputDataReceived += (_, _) => { };
                process.ErrorDataReceived += (_, _) => { };
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                return process;
            }
            catch (Win32Exception)
            {
                throw new ProviderUnavailableException();
            }
        }

        // One thumbnail every interval from keyframes only, 10 by 10 per JPEG sheet named 001.jpg onwards.
        public async Task<bool> CreateTrickplayAsync(string inputPath, string folder, int intervalSeconds, int width, int height, int columns, int rows, CancellationToken ct)
        {
            if (!IsAvailable)
            {
                throw new ProviderUnavailableException();
            }

            var start = new ProcessStartInfo(ExecutablePath!)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var argument in (string[])["-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-protocol_whitelist", InputProtocols(inputPath), .. ReadTimeout(inputPath, InputTimeout),
                "-skip_frame", "nokey", "-i", inputPath,
                "-an", "-sn", "-dn", "-vf", string.Create(CultureInfo.InvariantCulture, $"fps=1/{intervalSeconds},scale={width}:{height},tile={columns}x{rows}"),
                "-q:v", "5", Path.Combine(folder, "%03d.jpg")])
            {
                start.ArgumentList.Add(argument);
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(30));
            try
            {
                using var process = Process.Start(start) ?? throw new ProviderUnavailableException();
                YieldToPlayback(process);
                var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
                var errors = process.StandardError.ReadToEndAsync(timeout.Token);
                try
                {
                    await process.WaitForExitAsync(timeout.Token);
                    await Task.WhenAll(output, errors);
                }
                catch (OperationCanceledException)
                {
                    process.Kill(true);
                    ct.ThrowIfCancellationRequested();
                    return false;
                }

                return process.ExitCode == 0;
            }
            catch (Win32Exception)
            {
                throw new ProviderUnavailableException();
            }
        }

        // One image as a JPEG, WebP or lossless PNG (by the output's extension) at most this wide, never enlarged; a single frame, so an animation becomes its first picture.
        public async Task<bool> ScaleImageAsync(string inputPath, string outputPath, int width, CancellationToken ct)
        {
            if (!IsAvailable)
            {
                return false;
            }

            var start = new ProcessStartInfo(ExecutablePath!)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var argument in (string[])["-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-protocol_whitelist", "file", "-i", inputPath, "-an", "-sn", "-dn", "-frames:v", "1",
                "-vf", string.Create(CultureInfo.InvariantCulture, $"scale='min({width},iw)':-2:flags=lanczos"),
                .. outputPath.EndsWith(".webp", StringComparison.Ordinal) ? (string[])["-c:v", "libwebp", "-quality", "80"] : outputPath.EndsWith(".png", StringComparison.Ordinal) ? [] : ["-q:v", "3"], outputPath])
            {
                start.ArgumentList.Add(argument);
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            try
            {
                using var process = Process.Start(start) ?? throw new ProviderUnavailableException();
                YieldToPlayback(process);
                var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
                var errors = process.StandardError.ReadToEndAsync(timeout.Token);
                try
                {
                    await process.WaitForExitAsync(timeout.Token);
                    await Task.WhenAll(output, errors);
                }
                catch (OperationCanceledException)
                {
                    process.Kill(true);
                    ct.ThrowIfCancellationRequested();
                    return false;
                }

                return process.ExitCode == 0 && File.Exists(outputPath);
            }
            catch (Win32Exception)
            {
                return false;
            }
        }

        // A text subtitle stream or file as WebVTT; bounded so a stuck read never holds a request.
        public async Task<bool> ConvertSubtitleAsync(string inputPath, string? inputFormat, int? streamIndex, string outputPath, CancellationToken ct, string outputFormat = "webvtt")
        {
            if (!IsAvailable)
            {
                throw new ProviderUnavailableException();
            }

            var start = new ProcessStartInfo(ExecutablePath!)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            // Only local files; a subtitle file is read as what its name says, never sniffed.
            foreach (var argument in (string[])["-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-protocol_whitelist", InputProtocols(inputPath), .. ReadTimeout(inputPath, InputTimeout)])
            {
                start.ArgumentList.Add(argument);
            }

            if (inputFormat is not null)
            {
                start.ArgumentList.Add("-f");
                start.ArgumentList.Add(inputFormat);
            }

            start.ArgumentList.Add("-i");
            start.ArgumentList.Add(inputPath);

            if (streamIndex is { } stream)
            {
                start.ArgumentList.Add("-map");
                start.ArgumentList.Add("0:" + stream.ToString(CultureInfo.InvariantCulture));
            }

            foreach (var argument in (string[])["-f", outputFormat, outputPath])
            {
                start.ArgumentList.Add(argument);
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(5));
            try
            {
                using var process = Process.Start(start) ?? throw new ProviderUnavailableException();
                var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
                var errors = process.StandardError.ReadToEndAsync(timeout.Token);
                try
                {
                    await process.WaitForExitAsync(timeout.Token);
                    await Task.WhenAll(output, errors);
                }
                catch (OperationCanceledException)
                {
                    process.Kill(true);
                    ct.ThrowIfCancellationRequested();
                    return false;
                }

                return process.ExitCode == 0;
            }
            catch (Win32Exception)
            {
                throw new ProviderUnavailableException();
            }
        }

        // Takes one attachment of a container (a font) out into a file of our own naming; the name inside the container is never used as a path.
        // FFmpeg ends this run with a complaint that it has no output, the file is what counts.
        public async Task DumpAttachmentAsync(string inputPath, int streamIndex, string outputPath, CancellationToken ct)
        {
            if (!IsAvailable)
            {
                throw new ProviderUnavailableException();
            }

            var start = new ProcessStartInfo(ExecutablePath!)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var argument in (string[])["-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-protocol_whitelist", InputProtocols(inputPath), .. ReadTimeout(inputPath, InputTimeout),
                "-dump_attachment:" + streamIndex.ToString(CultureInfo.InvariantCulture), outputPath, "-i", inputPath])
            {
                start.ArgumentList.Add(argument);
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(2));
            try
            {
                using var process = Process.Start(start) ?? throw new ProviderUnavailableException();
                var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
                var errors = process.StandardError.ReadToEndAsync(timeout.Token);
                try
                {
                    await process.WaitForExitAsync(timeout.Token);
                    await Task.WhenAll(output, errors);
                }
                catch (OperationCanceledException)
                {
                    process.Kill(true);
                    ct.ThrowIfCancellationRequested();
                }
            }
            catch (Win32Exception)
            {
                throw new ProviderUnavailableException();
            }
        }

        // Direct SMB inputs are exposed only through Jularr's loopback stream server.
        // FFmpeg must permit that HTTP transport, but never arbitrary remote URLs.
        private static string InputProtocols(string inputPath) => IsLoopback(inputPath) ? "file,http,tcp" : "file";

        private static bool IsLoopback(string inputPath) =>
            Uri.TryCreate(inputPath, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttp && uri.Host == "127.0.0.1";

        // In front of a loopback input only: how long one read may stall, in the microseconds FFmpeg and ffprobe count. A local file needs no limit.
        internal static IReadOnlyList<string> ReadTimeout(string inputPath, TimeSpan timeout) =>
            IsLoopback(inputPath) ? ["-rw_timeout", ((long)timeout.TotalMicroseconds).ToString(CultureInfo.InvariantCulture)] : [];

        // Background pictures yield to playback; the HLS conversion a viewer waits for keeps its priority.
        private static void YieldToPlayback(Process process)
        {
            try
            {
                process.PriorityClass = ProcessPriorityClass.BelowNormal;
            }
            catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
            {
            }
        }

        internal static IReadOnlyList<string> BuildArguments(TranscodeRequest request, TimeSpan? inputTimeout = null)
        {
            var offset = (request.StartSegment * request.SegmentSeconds).ToString(CultureInfo.InvariantCulture);
            var segment = request.SegmentSeconds.ToString(CultureInfo.InvariantCulture);
            var arguments = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin" };

            // Input seeking and pacing.
            if (request.VideoStreamIndex is not null && !request.CopyVideo) arguments.AddRange(request.HardwareAccelerationArguments);
            arguments.AddRange(["-readrate", ReadRate, "-readrate_initial_burst", InitialBurstSeconds, .. ReadTimeout(request.InputPath, inputTimeout ?? DefaultInputTimeout), "-ss", offset,
                "-i", request.InputPath]);
            // No video stream means audio-only: never let FFmpeg automatically pick attached cover art.
            if (request.VideoStreamIndex is { } video) arguments.AddRange(["-map", "0:" + video.ToString(CultureInfo.InvariantCulture)]);
            else arguments.Add("-vn");
            arguments.AddRange(["-map", request.AudioStreamIndex is { } audio ? "0:" + audio.ToString(CultureInfo.InvariantCulture) : "0:a:0?", "-sn", "-dn"]);

            // Copy what the client plays; segment boundaries follow the grid.
            if (request.VideoStreamIndex is null)
            {
                // Audio-only HLS needs no video encoder, keyframes or hardware device.
            }
            else if (request.CopyVideo)
            {
                arguments.AddRange(["-c:v", "copy"]);
            }
            else
            {
                arguments.AddRange(request.VideoEncoderArguments);

                // A cap scales the picture down (never up) and limits the rate; pictures in hardware memory need their own scaler.
                var scale = request.ScaleFilter ?? (request.HardwareAccelerationArguments.Count == 0 ? TranscodePipelines.SoftwareScale : null);
                if (request.MaxHeight is { } height && scale is not null)
                {
                    arguments.AddRange(["-vf", scale.Replace("{height}", height.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)]);
                }

                if (request.VideoKilobits is { } kilobits)
                {
                    arguments.AddRange(["-maxrate", kilobits.ToString(CultureInfo.InvariantCulture) + "k", "-bufsize", (kilobits * 2).ToString(CultureInfo.InvariantCulture) + "k"]);
                }

                arguments.AddRange(["-force_key_frames", "expr:gte(t,n_forced*" + segment + ")"]);
            }

            arguments.AddRange(request.CopyAudio ? ["-c:a", "copy"] : ["-c:a", "aac", "-ac", "2", "-b:a", request.AudioKilobits.ToString(CultureInfo.InvariantCulture) + "k"]);

            // Later audio starts with silence; earlier audio drops its first moments. Both count from the point the conversion starts at, so a seek keeps the same shift.
            if (!request.CopyAudio && request.AudioOffsetMs > 0)
            {
                arguments.AddRange(["-af", "adelay=" + request.AudioOffsetMs.ToString(CultureInfo.InvariantCulture) + ":all=1"]);
            }
            else if (!request.CopyAudio && request.AudioOffsetMs < 0)
            {
                arguments.AddRange(["-af", "atrim=start=" + (-request.AudioOffsetMs / 1000d).ToString("0.###", CultureInfo.InvariantCulture) + ",asetpts=PTS-STARTPTS"]);
            }
            arguments.AddRange([
                "-output_ts_offset", offset,
                "-f", "hls",
                "-hls_time", segment,
                "-hls_segment_type", "mpegts",
                "-hls_list_size", "0",
                "-hls_flags", "temp_file",
                "-start_number", request.StartSegment.ToString(CultureInfo.InvariantCulture),
                "-hls_segment_filename", Path.Combine(request.OutputDirectory, "segment%d.ts"),
                Path.Combine(request.OutputDirectory, "ffmpeg.m3u8")
            ]);
            return arguments;
        }
    }
}
