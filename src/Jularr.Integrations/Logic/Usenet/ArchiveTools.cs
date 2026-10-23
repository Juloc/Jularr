using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;

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

        public async Task<long?> ReadExtractionSizeAsync(string archive, CancellationToken ct)
        {
            if (!CanExtract) return null;
            long bytes = 0;
            var hasSize = false;
            var exit = await RunAsync(SevenZipPath!, ["l", "-slt", "-ba", "-pjularr", "--", archive], Path.GetDirectoryName(archive)!, ct, null, async (stream, token) =>
            {
                using var reader = new StreamReader(stream);
                while (await reader.ReadLineAsync(token) is { } line)
                {
                    if (!line.StartsWith("Size = ", StringComparison.Ordinal)) continue;
                    if (!long.TryParse(line.AsSpan(7), NumberStyles.None, CultureInfo.InvariantCulture, out var size) || size > long.MaxValue - bytes)
                    {
                        throw new IOException("The archive contains an invalid expanded size.");
                    }

                    hasSize = true;
                    bytes += size;
                }
            });
            return exit == 0 && hasSize ? bytes : null;
        }

        public async Task<bool> RepairAsync(string par2File, CancellationToken ct, Action? checkStorage = null) =>
            CanRepair && await RunAsync(Par2Path!, ["r", "-q", "--", par2File], Path.GetDirectoryName(par2File)!, ct, checkStorage) == 0;

        // A placeholder password makes protected archives fail instead of waiting for input.
        public async Task<bool> ExtractAsync(string archive, string destination, CancellationToken ct, Action? checkStorage = null) =>
            CanExtract && await RunAsync(SevenZipPath!, ["x", "-y", "-pjularr", "-o" + destination, "--", archive], destination, ct, checkStorage) == 0;

        private static async Task<int> RunAsync(string executable, IReadOnlyList<string> arguments, string directory, CancellationToken ct, Action? checkStorage,
            Func<Stream, CancellationToken, Task>? readOutput = null)
        {
            checkStorage?.Invoke();
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
            Task? output = null;
            Task? errors = null;
            try
            {
                // Output is drained so the program never blocks on a full pipe.
                output = readOutput is null ? process.StandardOutput.BaseStream.CopyToAsync(Stream.Null, timeout.Token) : readOutput(process.StandardOutput.BaseStream, timeout.Token);
                errors = process.StandardError.BaseStream.CopyToAsync(Stream.Null, timeout.Token);
                var exited = process.WaitForExitAsync(timeout.Token);
                if (checkStorage is not null)
                {
                    while (!exited.IsCompleted)
                    {
                        checkStorage();
                        if (output.IsFaulted) await output;
                        await Task.WhenAny(exited, Task.Delay(TimeSpan.FromMilliseconds(200), timeout.Token));
                        timeout.Token.ThrowIfCancellationRequested();
                    }
                }

                if (readOutput is not null)
                {
                    // A failed listing parser must not leave a child blocked on stdout.
                    var first = await Task.WhenAny(exited, output);
                    if (first == output) await output;
                }

                await exited;
                await Task.WhenAll(output, errors);
                checkStorage?.Invoke();
                return process.ExitCode;
            }
            catch (OperationCanceledException)
            {
                ct.ThrowIfCancellationRequested();
                return -1;
            }
            finally
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
                if (output is not null && errors is not null)
                {
                    // Observe drains even when a storage check stops the child.
                    await Task.WhenAll(output, errors).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                }
            }
        }
    }
}
