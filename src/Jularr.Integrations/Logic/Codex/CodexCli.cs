using System.ComponentModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Jularr.Integrations.Data.Codex;
using Jularr.Integrations.Data.Common;

namespace Jularr.Integrations.Logic.Codex
{
    // The official Codex CLI of the image. Its sign-in lives under HomePath (CODEX_HOME) on the data volume; nothing here reads or returns the credential files.
    internal sealed partial class CodexCli
    {
        private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(15);

        public string? ExecutablePath { get; set; }
        public string HomePath { get; set; } = "";
        public bool IsAvailable => !string.IsNullOrWhiteSpace(ExecutablePath);

        public async Task<CodexCliStatus> ReadStatusAsync(CancellationToken ct)
        {
            if (!IsAvailable) return new CodexCliStatus(false, null, false, null);

            // Version
            var version = await RunAsync(["--version"], ct);
            if (version is null || version.ExitCode != 0) return new CodexCliStatus(false, null, false, null);
            var number = VersionPattern().Match(version.Output);

            // Stored sign-in
            var login = await RunAsync(["login", "status"], ct);
            var connected = login is { ExitCode: 0 } && login.Output.Contains("Logged in", StringComparison.OrdinalIgnoreCase);
            var signIn = !connected ? null : login!.Output.Contains("ChatGPT", StringComparison.OrdinalIgnoreCase) ? "chatgpt"
                : login.Output.Contains("API key", StringComparison.OrdinalIgnoreCase) ? "apiKey" : "other";
            return new CodexCliStatus(true, number.Success ? number.Value : null, connected, signIn);
        }

        // Removes the stored sign-in with the CLI itself.
        public async Task<bool> LogoutAsync(CancellationToken ct) => await RunAsync(["logout"], ct) is { ExitCode: 0 };

        // Starts the device flow; the returned login reports the code and ends when the person finished, the CLI failed or it was cancelled.
        public CodexDeviceLogin StartDeviceLogin()
        {
            if (!IsAvailable) throw new ProviderUnavailableException();
            try
            {
                return CodexDeviceLogin.Start(Prepare(["login", "--device-auth"]));
            }
            catch (Win32Exception)
            {
                throw new ProviderUnavailableException();
            }
        }

        // The models and plan usage of the signed-in account, asked from a short app-server session.
        public async Task<CodexCatalog> ReadCatalogAsync(CancellationToken ct)
        {
            if (!IsAvailable) throw new ProviderUnavailableException();
            return await CodexAppServer.ReadCatalogAsync(Prepare(["app-server"]), ct);
        }

        private ProcessStartInfo Prepare(string[] arguments)
        {
            // No shell; the CLI has no other home than the data volume's folder.
            Directory.CreateDirectory(HomePath);
            var start = new ProcessStartInfo(ExecutablePath!)
            {
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true
            };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            start.Environment["CODEX_HOME"] = HomePath;
            start.Environment["NO_COLOR"] = "1";
            return start;
        }

        private async Task<CommandResult?> RunAsync(string[] arguments, CancellationToken ct)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(CommandTimeout);
            try
            {
                using var process = Process.Start(Prepare(arguments)) ?? throw new Win32Exception();
                process.StandardInput.Close();
                var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
                var error = process.StandardError.ReadToEndAsync(timeout.Token);
                try
                {
                    await process.WaitForExitAsync(timeout.Token);
                }
                catch
                {
                    process.Kill(entireProcessTree: true);
                    throw;
                }

                return new CommandResult(process.ExitCode, await output + "\n" + await error);
            }
            catch (Win32Exception)
            {
                return null;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return null;
            }
        }

        [GeneratedRegex(@"\d+\.\d+\.\d+(?:-[0-9A-Za-z.]+)?")]
        private static partial Regex VersionPattern();

        private sealed record CommandResult(int ExitCode, string Output);
    }

    // One running `codex login --device-auth`: the code it prints, and how it ended.
    internal sealed partial class CodexDeviceLogin : IDisposable
    {
        // The CLI's own code expires after 15 minutes.
        private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(16);
        private readonly Process _process;
        private readonly CancellationTokenSource _lifetime = new(Lifetime);
        private readonly TaskCompletionSource<CodexDeviceCode?> _code = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private string? _url;
        private int _cancelled;

        private CodexDeviceLogin(Process process)
        {
            _process = process;
        }

        // Null when the CLI ended before it showed a code.
        public Task<CodexDeviceCode?> Code => _code.Task;

        // True when the person signed in; false when the CLI failed, the code expired or the login was cancelled.
        public Task<bool> Completed { get; private set; } = Task.FromResult(false);

        public bool IsCancelled => Volatile.Read(ref _cancelled) == 1;

        internal static CodexDeviceLogin Start(ProcessStartInfo start)
        {
            var process = Process.Start(start) ?? throw new Win32Exception();
            var login = new CodexDeviceLogin(process);
            process.StandardInput.Close();
            var reading = Task.WhenAll(login.ReadAsync(process.StandardOutput), login.ReadAsync(process.StandardError));
            login.Completed = login.WatchAsync(reading);
            return login;
        }

        public void Cancel()
        {
            Interlocked.Exchange(ref _cancelled, 1);
            Stop();
        }

        public void Dispose()
        {
            Stop();
            _lifetime.Dispose();
            _process.Dispose();
        }

        private void Stop()
        {
            try
            {
                if (!_process.HasExited) _process.Kill(entireProcessTree: true);
            }
            catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
            {
            }
        }

        private async Task ReadAsync(StreamReader reader)
        {
            while (await reader.ReadLineAsync() is { } line)
            {
                var text = LinePattern().Replace(line, "").Trim();

                // The address is only taken from OpenAI's own host.
                if (_url is null && UrlPattern().Match(text) is { Success: true } url && Uri.TryCreate(url.Value, UriKind.Absolute, out var address)
                    && address.Scheme == Uri.UriSchemeHttps && (address.Host == "openai.com" || address.Host.EndsWith(".openai.com", StringComparison.Ordinal)))
                {
                    _url = address.AbsoluteUri;
                }
                else if (_url is not null && !_code.Task.IsCompleted && CodePattern().Match(text) is { Success: true } code)
                {
                    _code.TrySetResult(new CodexDeviceCode(new Uri(_url), code.Value));
                }
            }
        }

        private async Task<bool> WatchAsync(Task reading)
        {
            try
            {
                await _process.WaitForExitAsync(_lifetime.Token);
                await reading;
                return _process.ExitCode == 0 && !IsCancelled;
            }
            catch (Exception exception) when (exception is OperationCanceledException or InvalidOperationException)
            {
                Stop();
                return false;
            }
            finally
            {
                _code.TrySetResult(null);
            }
        }

        [GeneratedRegex(@"\x1B\[[0-?]*[ -/]*[@-~]")]
        private static partial Regex LinePattern();

        [GeneratedRegex(@"https://\S+")]
        private static partial Regex UrlPattern();

        [GeneratedRegex(@"^[A-Z0-9]{4,}(?:-[A-Z0-9]{4,})+$")]
        private static partial Regex CodePattern();
    }
}
