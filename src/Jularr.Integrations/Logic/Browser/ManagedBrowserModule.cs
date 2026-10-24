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
        public bool IsInstalled
        {
            get
            {
                try
                {
                    var binary = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture switch
                    {
                        System.Runtime.InteropServices.Architecture.X64 => "chrome-linux64",
                        System.Runtime.InteropServices.Architecture.Arm64 => "chrome-linux-arm64",
                        _ => null
                    };
                    return binary is not null
                        && File.Exists(Path.Combine(_home, "runtime", binary, "chrome"))
                        && File.ReadAllText(Path.Combine(_home, "runtime", ".version")).Trim() == "156.0.8078.47";
                }
                catch (IOException)
                {
                    // The install/removal worker can replace the runtime while the Owner reads its status.
                    return false;
                }
                catch (UnauthorizedAccessException)
                {
                    return false;
                }
            }
        }

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
                    if (options.InstallLocally && _runner is { HasExited: true } && browser.Status.IsRunning)
                    {
                        // Chrome died but the DevTools socket has not yet noticed. Recreate it.
                        await browser.StopAsync();
                    }

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
                    // Even a killed installer may have left an .install.* staging directory.
                    if (options.InstallLocally && Directory.Exists(_home))
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
                // Failed startup must not leave a broken browser exposed as available.
                if (enabled && !browser.Status.IsRunning)
                {
                    var marker = Path.Combine(_home, ".enabled");
                    if (File.Exists(marker)) File.Delete(marker);
                    StopRunner();
                }

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
