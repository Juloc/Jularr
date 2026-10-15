using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using Jularr.Integrations.Data.Common;
using Jularr.Integrations.Data.Media;

namespace Jularr.Integrations.Logic.Media
{
    // Chromaprint's fpcalc: the fingerprint of an audio file's first two minutes; without the tool there are none.
    internal sealed class AudioFingerprinter
    {
        public string? ExecutablePath { get; set; }
        public bool IsAvailable => !string.IsNullOrWhiteSpace(ExecutablePath);

        public async Task<AudioFingerprint> FingerprintAsync(string path, CancellationToken ct)
        {
            if (!IsAvailable)
            {
                throw new ProviderUnavailableException();
            }

            // No shell and a bounded run time; library analysis yields to playback.
            var start = new ProcessStartInfo(ExecutablePath!) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var argument in new[] { "-json", "-length", "120", path })
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
                return process.ExitCode == 0 ? Parse(await output) : throw new InvalidDataException("The audio file could not be fingerprinted.");
            }
            catch (OperationCanceledException)
            {
                process.Kill(true);
                ct.ThrowIfCancellationRequested();
                throw new InvalidDataException("Fingerprinting timed out.");
            }
        }

        internal static AudioFingerprint Parse(string json)
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            return root.TryGetProperty("duration", out var duration) && duration.TryGetDouble(out var seconds) && seconds is > 0 and < 86400
                && root.TryGetProperty("fingerprint", out var fingerprint) && fingerprint.GetString() is { Length: > 0 and <= 100_000 } value
                ? new AudioFingerprint((int)Math.Round(seconds), value)
                : throw new InvalidDataException("fpcalc answered without a fingerprint.");
        }
    }
}
