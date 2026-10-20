using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Jularr.Integrations.Data.Common;
using Jularr.Integrations.Data.Media;

namespace Jularr.Integrations.Logic.Media
{
    internal sealed class MediaProbeClient
    {
        public string? ExecutablePath { get; set; }
        public bool IsAvailable => !string.IsNullOrWhiteSpace(ExecutablePath);

        public async Task<MediaProbe> ProbeAsync(string path, CancellationToken ct)
        {
            if (!IsAvailable)
            {
                throw new ProviderUnavailableException();
            }

            // No shell and a bounded run time.
            var start = new ProcessStartInfo(ExecutablePath!)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var argument in new[] { "-v", "error", "-print_format", "json", "-show_format", "-show_streams", "--", path })
            {
                start.ArgumentList.Add(argument);
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            Process process;
            try
            {
                process = Process.Start(start) ?? throw new ProviderUnavailableException();
            }
            catch (Win32Exception)
            {
                throw new ProviderUnavailableException();
            }

            using var owned = process;

            // Library analysis yields to active playback.
            try
            {
                process.PriorityClass = ProcessPriorityClass.BelowNormal;
            }
            catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
            {
            }

            try
            {
                var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
                var errors = process.StandardError.ReadToEndAsync(timeout.Token);
                await process.WaitForExitAsync(timeout.Token);
                await errors;
                if (process.ExitCode != 0)
                {
                    throw new InvalidDataException("The media file could not be analysed.");
                }

                return Parse(await output);
            }
            catch (OperationCanceledException)
            {
                process.Kill(true);
                ct.ThrowIfCancellationRequested();
                throw new InvalidDataException("The media analysis timed out.");
            }
        }

        internal static MediaProbe Parse(string json)
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            // Container facts.
            string? container = null;
            long? durationMs = null;
            long? bitRate = null;
            if (root.TryGetProperty("format", out var format))
            {
                container = Text(format, "format_name") is { } name ? name.Split(',')[0] : null;
                durationMs = Text(format, "duration") is { } duration && decimal.TryParse(duration, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
                    ? (long)Math.Round(seconds * 1000m) : null;
                bitRate = Text(format, "bit_rate") is { } rate && long.TryParse(rate, CultureInfo.InvariantCulture, out var bits) ? bits : null;
            }

            // Video, audio and subtitle streams.
            var streams = new List<MediaProbeStream>();
            if (root.TryGetProperty("streams", out var items))
            {
                foreach (var stream in items.EnumerateArray())
                {
                    var type = Text(stream, "codec_type");
                    if (type is not ("video" or "audio" or "subtitle") || !stream.TryGetProperty("index", out var index))
                    {
                        continue;
                    }

                    var tags = stream.TryGetProperty("tags", out var tagValues) ? tagValues : default;
                    var disposition = stream.TryGetProperty("disposition", out var flags) ? flags : default;
                    var language = tags.ValueKind == JsonValueKind.Object ? Text(tags, "language") : null;
                    streams.Add(new MediaProbeStream(index.GetInt32(), type, Text(stream, "codec_name"), language is "und" ? null : language,
                        tags.ValueKind == JsonValueKind.Object ? Text(tags, "title") : null, Number(stream, "width"), Number(stream, "height"), Number(stream, "channels"),
                        Flag(disposition, "default"), Flag(disposition, "forced"),
                        type == "video" ? BitDepth(stream) : null, type == "video" ? DynamicRange(stream) : null, type == "video" && IsStereo(stream, tags)));
                }
            }

            return new MediaProbe(container, durationMs, bitRate, streams);
        }

        // Bits per colour: the raw sample size, else the pixel format's own (yuv420p10le is ten).
        private static int? BitDepth(JsonElement stream)
        {
            if (Text(stream, "bits_per_raw_sample") is { } raw && int.TryParse(raw, CultureInfo.InvariantCulture, out var bits) && bits is > 0 and <= 32)
            {
                return bits;
            }

            return Text(stream, "pix_fmt") is { } format && System.Text.RegularExpressions.Regex.Match(format, @"(?<bits>9|10|12|14|16)(?:le|be)?$") is { Success: true } match
                ? int.Parse(match.Groups["bits"].Value, CultureInfo.InvariantCulture)
                : Text(stream, "pix_fmt") is not null ? 8 : null;
        }

        // The dynamic range the *arrs name: from the transfer function and the side data of the stream.
        private static string? DynamicRange(JsonElement stream)
        {
            var sideData = new List<JsonElement>();
            if (stream.TryGetProperty("side_data_list", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                sideData.AddRange(list.EnumerateArray());
            }

            bool Has(string type) => sideData.Any(item => Text(item, "side_data_type") is { } name && name.Contains(type, StringComparison.OrdinalIgnoreCase));
            var transfer = Text(stream, "color_transfer");
            var baseRange = transfer switch
            {
                "smpte2084" => Has("Mastering display") ? "HDR10" : "PQ",
                "arib-std-b67" => "HLG",
                _ => null
            };
            if (Has("HDR10+") || Has("2094-40"))
            {
                baseRange = "HDR10Plus";
            }

            if (sideData.FirstOrDefault(item => Text(item, "side_data_type") is { } name && (name.Contains("DOVI", StringComparison.OrdinalIgnoreCase) || name.Contains("Dolby Vision", StringComparison.OrdinalIgnoreCase)))
                is { ValueKind: JsonValueKind.Object } dolby)
            {
                // The base layer's compatibility id tells what a player without Dolby Vision sees.
                return dolby.TryGetProperty("dv_bl_signal_compatibility_id", out var id) && id.ValueKind == JsonValueKind.Number
                    ? id.GetInt32() switch { 1 => "DV HDR10", 2 => "DV SDR", 4 => "DV HLG", _ => "DV" }
                    : "DV";
            }

            return baseRange;
        }

        private static bool IsStereo(JsonElement stream, JsonElement tags) =>
            tags.ValueKind == JsonValueKind.Object && Text(tags, "stereo_mode") is not null
            || stream.TryGetProperty("side_data_list", out var list) && list.ValueKind == JsonValueKind.Array
                && list.EnumerateArray().Any(item => Text(item, "side_data_type") is "Stereo 3D");

        private static string? Text(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text ? text : null;

        private static int? Number(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.GetInt32() > 0 ? value.GetInt32() : null;

        private static bool Flag(JsonElement element, string name) =>
            element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.GetInt32() == 1;
    }
}
