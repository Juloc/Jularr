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
                        Flag(disposition, "default"), Flag(disposition, "forced")));
                }
            }

            return new MediaProbe(container, durationMs, bitRate, streams);
        }

        private static string? Text(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text ? text : null;

        private static int? Number(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.GetInt32() > 0 ? value.GetInt32() : null;

        private static bool Flag(JsonElement element, string name) =>
            element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.GetInt32() == 1;
    }
}
