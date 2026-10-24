using System.Diagnostics;
using Jularr.Integrations.Data.Browser;

namespace Jularr.Integrations.Logic.Browser
{
    // Reconcile the optional, unprivileged browser package with the instance module state.
    internal sealed class ManagedBrowserModule(BrowserOptions options, ISourceBrowser browser) : IDisposable
    {
        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly string _home = options.ProfilePath;
        private Process? _runner;

        public string State { get; private set; } = "disabled";
        public string? LastError { get; private set; }
        public bool IsInstalled => Directory.Exists(Path.Combine(_home, "runtime"))
            && File.Exists(Path.Combine(_home, "runtime", ".version"));

        public async Task ReconcileAsync(bool enabled, CancellationToken ct)
        {
            if (!options.IsManaged) return;
            await _gate.WaitAsync(ct);
            try
            {
                if (enabled)
                {
                    if (options.InstallLocally && !IsInstalled)
                    {
                        State = "installing";
                        await ExecuteAsync("install", ct);
                    }

                    Directory.CreateDirectory(_home);
                    await File.WriteAllTextAsync(Path.Combine(_home, ".enabled"), "1", ct);
                    if (!browser.Status.IsRunning)
                    {
                        State = "starting";
                        if (options.InstallLocally && (_runner is null || _runner.HasExited))
                        {
                            _runner?.Dispose();
                            _runner = Start("run");
                        }

                        for (var attempt = 0; attempt < 15; attempt++)
                        {
                            try
                            {
                                await browser.StartAsync(ct);
                                break;
                            }
                            catch (SourceBrowserException) when (attempt < 14)
                            {
                                await Task.Delay(TimeSpan.FromSeconds(1), ct);
                            }
                        }
                    }

                    State = "ready";
                }
                else
                {
                    var marker = Path.Combine(_home, ".enabled");
                    if (File.Exists(marker)) File.Delete(marker);
                    if (browser.Status.IsRunning) await browser.StopAsync();
                    StopRunner();
                    if (options.InstallLocally && (IsInstalled || Directory.Exists(Path.Combine(_home, "profile"))))
                    {
                        State = "removing";
                        await ExecuteAsync("uninstall", ct);
                    }

                    State = "disabled";
                }

                LastError = null;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                State = "error";
                LastError = exception.Message;
            }
            finally
            {
                _gate.Release();
            }
        }

        private Process Start(string action)
        {
            var start = new ProcessStartInfo("jularr-browser") { UseShellExecute = false };
            start.ArgumentList.Add(action);
            start.Environment["JULARR_BROWSER_HOME"] = _home;
            return Process.Start(start) ?? throw new IOException("Cannot start browser package management.");
        }

        private async Task ExecuteAsync(string action, CancellationToken ct)
        {
            using var process = Start(action);
            try
            {
                await process.WaitForExitAsync(ct);
                if (process.ExitCode != 0)
                {
                    throw new IOException("Browser " + action + " failed with exit code " + process.ExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture));
                }
            }
            catch
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                throw;
            }
        }

        private void StopRunner()
        {
            if (_runner is null) return;
            try
            {
                if (!_runner.HasExited) _runner.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // The process already exited.
            }
            finally
            {
                _runner.Dispose();
                _runner = null;
            }
        }

        public void Dispose()
        {
            StopRunner();
            _gate.Dispose();
        }
    }
}
