using System.ComponentModel;
using System.Diagnostics;

namespace Jularr.Integrations.Logic.Usenet
{
    // The approved par2 and 7-Zip programs repair and unpack downloads; no shell, bounded run time.
    internal sealed class ArchiveTools
    {
        private static readonly TimeSpan MaximumRun = TimeSpan.FromHours(2);

        public string? Par2Path { get; set; }
        public string? SevenZipPath { get; set; }
        public bool CanRepair => !string.IsNullOrWhiteSpace(Par2Path);
        public bool CanExtract => !string.IsNullOrWhiteSpace(SevenZipPath);

        public async Task<bool> RepairAsync(string par2File, CancellationToken ct) =>
            CanRepair && await RunAsync(Par2Path!, ["r", "-q", "--", par2File], Path.GetDirectoryName(par2File)!, ct) == 0;

        // A placeholder password makes protected archives fail instead of waiting for input.
        public async Task<bool> ExtractAsync(string archive, string destination, CancellationToken ct) =>
            CanExtract && await RunAsync(SevenZipPath!, ["x", "-y", "-pjularr", "-o" + destination, "--", archive], destination, ct) == 0;

        private static async Task<int> RunAsync(string executable, IReadOnlyList<string> arguments, string directory, CancellationToken ct)
        {
            var start = new ProcessStartInfo(executable)
            {
                WorkingDirectory = directory,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var argument in arguments)
            {
                start.ArgumentList.Add(argument);
            }

            Process process;
            try
            {
                process = Process.Start(start) ?? throw new InvalidOperationException("The program did not start.");
            }
            catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
            {
                return -1;
            }

            using var owned = process;
            process.StandardInput.Close();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(MaximumRun);
            try
            {
                // Output is drained so the program never blocks on a full pipe.
                var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
                var errors = process.StandardError.ReadToEndAsync(timeout.Token);
                await process.WaitForExitAsync(timeout.Token);
                await Task.WhenAll(output, errors);
                return process.ExitCode;
            }
            catch (OperationCanceledException)
            {
                process.Kill(true);
                ct.ThrowIfCancellationRequested();
                return -1;
            }
        }
    }
}
