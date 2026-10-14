using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using Jularr.Integrations.Data.Database;

namespace Jularr.Integrations.Logic.Database
{
    // pg_dump and pg_restore of the Jularr database; no shell, bounded run time.
    internal sealed class PostgresBackupTools
    {
        private static readonly TimeSpan MaximumRun = TimeSpan.FromHours(6);

        public string? PgDumpPath { get; set; }
        public string? PgRestorePath { get; set; }
        public PostgresConnection? Connection { get; set; }
        public bool CanBackUp => !string.IsNullOrWhiteSpace(PgDumpPath) && Connection is not null;
        public bool CanRestore => !string.IsNullOrWhiteSpace(PgRestorePath) && Connection is not null;

        public async Task<bool> DumpAsync(string file, CancellationToken ct) =>
            CanBackUp && await RunAsync(PgDumpPath!, ["--format=custom", "--no-owner", "--no-privileges", "--file=" + file, .. Target()], ct) == 0;

        // Replaces every object of the backup in one transaction.
        public async Task<bool> RestoreAsync(string file, CancellationToken ct) =>
            CanRestore && await RunAsync(PgRestorePath!, ["--clean", "--if-exists", "--no-owner", "--no-privileges", "--single-transaction", .. Target(), "--", file], ct) == 0;

        private string[] Target() =>
            ["--host=" + Connection!.Host, "--port=" + Connection.Port.ToString(CultureInfo.InvariantCulture), "--username=" + Connection.Username, "--dbname=" + Connection.Database, "--no-password"];

        private async Task<int> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken ct)
        {
            var start = new ProcessStartInfo(executable)
            {
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

            if (Connection!.Password is { } password)
            {
                start.Environment["PGPASSWORD"] = password;
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
                // Drained output never blocks the program.
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
