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

        public string? ExecutablePath { get; set; }
        public bool IsAvailable => !string.IsNullOrWhiteSpace(ExecutablePath);

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
            foreach (var argument in BuildArguments(request))
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
            foreach (var argument in (string[])["-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-protocol_whitelist", InputProtocols(inputPath), "-skip_frame", "nokey", "-i", inputPath,
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

        // One image as a JPEG or WebP (by the output's extension) at most this wide, never enlarged; a single frame, so an animation becomes its first picture.
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
                .. outputPath.EndsWith(".webp", StringComparison.Ordinal) ? (string[])["-c:v", "libwebp", "-quality", "80"] : ["-q:v", "3"], outputPath])
            {
                start.ArgumentList.Add(argument);
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
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

                return process.ExitCode == 0 && File.Exists(outputPath);
            }
            catch (Win32Exception)
            {
                return false;
            }
        }

        // A text subtitle stream or file as WebVTT; bounded so a stuck read never holds a request.
        public async Task<bool> ConvertSubtitleAsync(string inputPath, string? inputFormat, int? streamIndex, string outputPath, CancellationToken ct)
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
            foreach (var argument in (string[])["-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-protocol_whitelist", InputProtocols(inputPath)])
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

            foreach (var argument in (string[])["-f", "webvtt", outputPath])
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

        // Direct SMB inputs are exposed only through Jularr's loopback stream server.
        // FFmpeg must permit that HTTP transport, but never arbitrary remote URLs.
        private static string InputProtocols(string inputPath) =>
            Uri.TryCreate(inputPath, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttp && uri.Host == "127.0.0.1"
                ? "file,http,tcp" : "file";

        internal static IReadOnlyList<string> BuildArguments(TranscodeRequest request)
        {
            var offset = (request.StartSegment * request.SegmentSeconds).ToString(CultureInfo.InvariantCulture);
            var segment = request.SegmentSeconds.ToString(CultureInfo.InvariantCulture);
            var arguments = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin" };

            // Input seeking and pacing.
            if (!request.CopyVideo) arguments.AddRange(request.HardwareAccelerationArguments);
            arguments.AddRange(["-readrate", ReadRate, "-readrate_initial_burst", InitialBurstSeconds, "-ss", offset, "-i", request.InputPath]);
            arguments.AddRange(["-map", request.VideoStreamIndex is { } video ? "0:" + video.ToString(CultureInfo.InvariantCulture) : "0:v:0?"]);
            arguments.AddRange(["-map", request.AudioStreamIndex is { } audio ? "0:" + audio.ToString(CultureInfo.InvariantCulture) : "0:a:0?", "-sn", "-dn"]);

            // Copy what the client plays; segment boundaries follow the grid.
            if (request.CopyVideo)
            {
                arguments.AddRange(["-c:v", "copy"]);
            }
            else
            {
                arguments.AddRange(request.VideoEncoderArguments);
                arguments.AddRange(["-force_key_frames", "expr:gte(t,n_forced*" + segment + ")"]);
            }

            arguments.AddRange(request.CopyAudio ? ["-c:a", "copy"] : ["-c:a", "aac", "-ac", "2", "-b:a", request.AudioKilobits.ToString(CultureInfo.InvariantCulture) + "k"]);
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
