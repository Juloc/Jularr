using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Jularr.Integrations.Data.Codex;
using Jularr.Integrations.Data.Common;

namespace Jularr.Integrations.Logic.Codex
{
    // The official Codex CLI of the image. Its sign-in lives under HomePath (CODEX_HOME) on the data volume; nothing here reads or returns the credential files.
    internal sealed partial class CodexCli
    {
        private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(15);
        private static readonly TimeSpan ExecTimeout = TimeSpan.FromMinutes(4);
        private static readonly TimeSpan StreamTimeout = TimeSpan.FromMinutes(6);

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

        // One prompt, one answer, nothing else: no tools, no files (read-only sandbox, an empty working folder), no stored session, the user's own config and rules ignored.
        // The sign-in is the one under CODEX_HOME. A failure of any kind (exit code, empty answer, timeout) is an InvalidDataException; the caller decides about fallbacks.
        public async Task<CodexAnswer> ExecAsync(string model, string? effort, string prompt, CancellationToken ct)
        {
            if (!IsAvailable) throw new ProviderUnavailableException();
            var work = Path.Combine(HomePath, "work");
            Directory.CreateDirectory(work);
            var output = Path.Combine(work, Guid.NewGuid().ToString("N") + ".txt");
            List<string> arguments = ["exec", "--skip-git-repo-check", "--ephemeral", "--ignore-user-config", "--ignore-rules", "-s", "read-only", "--color", "never", "--json", "-C", work, "-m", model];
            if (effort is not null) arguments.AddRange(["-c", "model_reasoning_effort=\"" + effort + "\""]);
            arguments.AddRange(["-o", output, "-"]);

            var start = Prepare([.. arguments]);
            start.StandardInputEncoding = new UTF8Encoding(false);
            start.StandardOutputEncoding = Encoding.UTF8;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(ExecTimeout);
            try
            {
                using var process = Process.Start(start) ?? throw new Win32Exception();
                try
                {
                    await process.StandardInput.WriteAsync(prompt.AsMemory(), timeout.Token);
                    process.StandardInput.Close();
                    var lines = process.StandardOutput.ReadToEndAsync(timeout.Token);
                    // Diagnostics on stderr may name the account; they are read away and dropped.
                    var errors = process.StandardError.ReadToEndAsync(timeout.Token);
                    await process.WaitForExitAsync(timeout.Token);
                    var events = await lines;
                    await errors;
                    if (PlanLimitOf(events) is { } limit) throw limit;
                    if (process.ExitCode != 0 || !File.Exists(output)) throw new InvalidDataException("Codex did not answer.");
                    var text = (await File.ReadAllTextAsync(output, timeout.Token)).Trim();
                    if (text.Length == 0) throw new InvalidDataException("Codex answered with nothing.");
                    return new CodexAnswer(text, TokensOf(events));
                }
                catch
                {
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                    throw;
                }
            }
            catch (Win32Exception)
            {
                throw new ProviderUnavailableException();
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new InvalidDataException("Codex took too long.");
            }
            finally
            {
                try
                {
                    File.Delete(output);
                }
                catch (IOException)
                {
                }
            }
        }

        // One prompt in a thread of its own on the app-server, answered as the text arrives: `onText` gets each piece, so the caller sees the translation grow instead of
        // waiting for the whole answer. The thread is as bare as it gets (a short instruction, no tools, no plugins, no environment notes), which is why a request costs about
        // a fifth of an `exec` request. A failure of any kind (limit, error, empty answer, timeout) is an exception as with ExecAsync.
        public async Task<CodexAnswer> StreamAsync(string model, string? effort, string prompt, Action<string> onText, CancellationToken ct)
        {
            if (!IsAvailable) throw new ProviderUnavailableException();
            var work = Path.Combine(HomePath, "work");
            Directory.CreateDirectory(work);
            try
            {
                return await CodexAppServer.StreamAsync(Prepare(["app-server"]), work, model, effort, prompt, onText, StreamTimeout, ct);
            }
            catch (Win32Exception)
            {
                throw new ProviderUnavailableException();
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new InvalidDataException("Codex took too long.");
            }
            catch (Exception exception) when (exception is IOException or JsonException)
            {
                throw new InvalidDataException("Codex did not answer.", exception);
            }
        }

        // The events of a turn the account's usage limit stopped (`turn.failed`: "You've hit your usage limit ... try again at Nov 4th, 2026 7:41 AM."), else null.
        internal static CodexPlanLimitException? PlanLimitOf(string events)
        {
            foreach (var line in events.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                try
                {
                    using var document = JsonDocument.Parse(line);
                    var root = document.RootElement;
                    if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var type) || type.GetString() is not ("turn.failed" or "error")) continue;
                    var message = root.TryGetProperty("message", out var text) && text.ValueKind == JsonValueKind.String ? text.GetString()
                        : root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out var inner) && inner.ValueKind == JsonValueKind.String ? inner.GetString() : null;
                    if (PlanLimitIn(message) is { } limit) return limit;
                }
                catch (JsonException)
                {
                }
            }

            return null;
        }

        // The limit a message of Codex names ("You've hit your usage limit ... try again at Nov 4th, 2026 7:41 AM."), else null.
        internal static CodexPlanLimitException? PlanLimitIn(string? message)
        {
            if (message is null || !message.Contains("usage limit", StringComparison.OrdinalIgnoreCase)) return null;
            var until = TryAgainPattern().Match(message);
            var cleaned = until.Success ? OrdinalPattern().Replace(until.Groups[1].Value.Trim().TrimEnd('.'), "$1") : null;
            return new CodexPlanLimitException(cleaned is not null && DateTime.TryParseExact(cleaned, ["MMM d, yyyy h:mm tt", "MMMM d, yyyy h:mm tt"], System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeLocal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var parsed) ? new DateTimeOffset(parsed, TimeSpan.Zero) : null);
        }

        [GeneratedRegex(@"try again at ([^\r\n]+)", RegexOptions.IgnoreCase)]
        private static partial Regex TryAgainPattern();

        [GeneratedRegex(@"(\d)(?:st|nd|rd|th)\b")]
        private static partial Regex OrdinalPattern();

        // The usage of the turn that ended (`turn.completed`): input plus output tokens; 0 when the events do not carry it.
        internal static long TokensOf(string events)
        {
            long tokens = 0;
            foreach (var line in events.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                try
                {
                    using var document = JsonDocument.Parse(line);
                    var root = document.RootElement;
                    if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var type) || type.GetString() != "turn.completed"
                        || !root.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object) continue;
                    foreach (var name in new[] { "input_tokens", "output_tokens" })
                        if (usage.TryGetProperty(name, out var count) && count.ValueKind == JsonValueKind.Number && count.TryGetInt64(out var value) && value > 0) tokens += value;
                }
                catch (JsonException)
                {
                }
            }

            return tokens;
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
