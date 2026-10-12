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

            arguments.AddRange(request.CopyAudio ? ["-c:a", "copy"] : ["-c:a", "aac", "-ac", "2", "-b:a", "192k"]);
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
