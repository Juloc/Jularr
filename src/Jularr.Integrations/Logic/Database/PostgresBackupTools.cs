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

        public Task DumpAsync(string file, CancellationToken ct) =>
            RunAsync(CanBackUp ? PgDumpPath! : null, ["--format=custom", "--no-owner", "--no-privileges", "--file=" + file, .. Target()], ct);

        // Replaces every object of the backup in one transaction.
        public Task RestoreAsync(string file, CancellationToken ct) =>
            RunAsync(CanRestore ? PgRestorePath! : null, ["--clean", "--if-exists", "--no-owner", "--no-privileges", "--single-transaction", .. Target(), "--", file], ct);

        // Only the tools' own error lines; context lines quote table rows such as password hashes.
        private static string Errors(string output) => string.Join('\n', output.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Where(line => line.Contains("error:", StringComparison.Ordinal) || line.StartsWith("FATAL", StringComparison.Ordinal))
            .Select(line => line.Length > 300 ? line[..300] : line));

        // Matched in the C locale's English; anything else stays a plain failure.
        private static string Reason(string errors) =>
            errors.Contains("password authentication failed", StringComparison.OrdinalIgnoreCase) || errors.Contains("no password supplied", StringComparison.OrdinalIgnoreCase)
                ? "backup_auth_failed"
                : errors.Contains("version mismatch", StringComparison.OrdinalIgnoreCase) || errors.Contains("unsupported version", StringComparison.OrdinalIgnoreCase)
                    ? "backup_version_mismatch"
                    : errors.Contains("No space left on device", StringComparison.OrdinalIgnoreCase) ? "backup_disk_full" : "backup_failed";

        private string[] Target() =>
            ["--host=" + Connection!.Host, "--port=" + Connection.Port.ToString(CultureInfo.InvariantCulture), "--username=" + Connection.Username, "--dbname=" + Connection.Database, "--no-password"];

        private async Task RunAsync(string? executable, IReadOnlyList<string> arguments, CancellationToken ct)
        {
            if (executable is null)
            {
                throw new PostgresToolException("backup_tool_missing", "No program is configured.");
            }

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

            start.Environment["LC_MESSAGES"] = "C";

            Process process;
            try
            {
                process = Process.Start(start) ?? throw new InvalidOperationException("The program did not start.");
            }
            catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
            {
                throw new PostgresToolException("backup_tool_missing", exception.Message);
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
                if (process.ExitCode != 0)
                {
                    var text = Errors(errors.Result);
                    throw new PostgresToolException(Reason(text), text.Length > 2000 ? text[^2000..] : text);
                }
            }
            catch (OperationCanceledException)
            {
                process.Kill(true);
                ct.ThrowIfCancellationRequested();
                throw new PostgresToolException("backup_timeout", "The program ran longer than " + MaximumRun.TotalHours.ToString(CultureInfo.InvariantCulture) + " hours.");
            }
        }
    }
}
