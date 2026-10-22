using System.ComponentModel;
using System.Diagnostics;

namespace Jularr.Integrations.Logic.Media
{
    // libjxl's cjxl, shipped in the image: a picture as JPEG XL for the cover cache. Without the tool the cache keeps WebP.
    internal sealed class JxlEncoder
    {
        private readonly SemaphoreSlim _gate = new(1, 1);
        private volatile bool _progressiveRefused;
        private volatile int _state; // 0 not tried, 1 runs, 2 does not run

        public string? ExecutablePath { get; set; }

        // The first line of `cjxl --version` once the tool was seen to run.
        public string? Version { get; private set; }

        // Configured, and not found broken by the start-up check or a run.
        public bool IsAvailable => !string.IsNullOrWhiteSpace(ExecutablePath) && _state != 2;

        // Arguments only, never a shell string: visually near-lossless (distance 1.0), effort 7 to stay fast, progressive decoding so a poster appears at once, one thread so a backfill leaves the host usable.
        internal static IReadOnlyList<string> BuildArguments(string inputPath, string outputPath, bool progressive) =>
            [inputPath, outputPath, "-d", "1.0", "-e", "7", .. progressive ? (string[])["--progressive"] : [], "--num_threads=1", "--quiet"];

        // `cjxl --version` once at start; a tool that does not answer is switched off for the run of the process.
        public async Task<bool> DetectAsync(CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(ExecutablePath))
            {
                return false;
            }

            var (exitCode, output) = await RunAsync(["--version"], TimeSpan.FromSeconds(15), ct);
            _state = exitCode == 0 ? 1 : 2;
            Version = exitCode == 0 ? output.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() : null;
            return exitCode == 0;
        }

        // The picture (PNG or JPEG) as JPEG XL at the output path; false when the tool is missing or refuses it, and nothing is left at the output then. A cjxl too old for --progressive is run once more without it.
        public async Task<bool> EncodeAsync(string inputPath, string outputPath, CancellationToken ct)
        {
            if (!IsAvailable)
            {
                return false;
            }

            if (_state == 0)
            {
                await _gate.WaitAsync(ct);
                try
                {
                    if (_state == 0)
                    {
                        await DetectAsync(ct);
                    }
                }
                finally
                {
                    _gate.Release();
                }

                if (!IsAvailable)
                {
                    return false;
                }
            }

            if (!_progressiveRefused)
            {
                if (await TryAsync(inputPath, outputPath, true, ct))
                {
                    return true;
                }

                File.Delete(outputPath);
            }

            if (await TryAsync(inputPath, outputPath, false, ct))
            {
                _progressiveRefused = true;
                return true;
            }

            File.Delete(outputPath);
            return false;
        }

        private async Task<bool> TryAsync(string inputPath, string outputPath, bool progressive, CancellationToken ct)
        {
            var (exitCode, _) = await RunAsync(BuildArguments(inputPath, outputPath, progressive), TimeSpan.FromSeconds(60), ct);
            return exitCode == 0 && File.Exists(outputPath) && new FileInfo(outputPath).Length > 0;
        }

        // No shell, a bounded run time, and both streams drained; the exit code is -1 when the tool cannot start or runs out of time.
        private async Task<(int ExitCode, string Output)> RunAsync(IEnumerable<string> arguments, TimeSpan limit, CancellationToken ct)
        {
            var start = new ProcessStartInfo(ExecutablePath!) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var argument in arguments)
            {
                start.ArgumentList.Add(argument);
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(limit);
            try
            {
                using var process = Process.Start(start) ?? throw new Win32Exception();
                var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
                var errors = process.StandardError.ReadToEndAsync(timeout.Token);
                try
                {
                    await process.WaitForExitAsync(timeout.Token);
                    await Task.WhenAll(output, errors);
                    return (process.ExitCode, await output + "\n" + await errors);
                }
                catch (OperationCanceledException)
                {
                    process.Kill(true);
                    ct.ThrowIfCancellationRequested();
                    return (-1, "");
                }
            }
            catch (Win32Exception)
            {
                _state = 2;
                return (-1, "");
            }
        }
    }
}
