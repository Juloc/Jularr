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

        public Task<AudioFingerprint> FingerprintAsync(string path, CancellationToken ct) => RunAsync(path, null, ct);

        // A file fpcalc cannot open itself, such as one on a direct share, arrives on its standard input.
        public Task<AudioFingerprint> FingerprintAsync(Stream audio, CancellationToken ct) => RunAsync("-", audio, ct);

        private async Task<AudioFingerprint> RunAsync(string input, Stream? audio, CancellationToken ct)
        {
            if (!IsAvailable)
            {
                throw new ProviderUnavailableException();
            }

            // No shell and a bounded run time; library analysis yields to playback.
            var start = new ProcessStartInfo(ExecutablePath!)
            {
                RedirectStandardInput = audio is not null, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true
            };
            foreach (var argument in new[] { "-json", "-length", "120", input })
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
                var feed = audio is null ? Task.CompletedTask : FeedAsync(audio, process.StandardInput.BaseStream, timeout.Token);
                await process.WaitForExitAsync(timeout.Token);
                await feed;
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

        // fpcalc stops reading after two minutes of audio; the rest is not needed.
        private static async Task FeedAsync(Stream audio, Stream input, CancellationToken ct)
        {
            try
            {
                await using (input)
                {
                    await audio.CopyToAsync(input, ct);
                }
            }
            catch (IOException)
            {
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
