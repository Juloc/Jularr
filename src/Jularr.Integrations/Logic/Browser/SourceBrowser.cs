using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;
using Jularr.Integrations.Data.Browser;
using Jularr.Integrations.Logic.Common;

namespace Jularr.Integrations.Logic.Browser
{
    // The browser the image ships: a configured path wins, empty switches it off, unset finds it.
    internal sealed class BrowserOptions
    {
        public string? Path { get; set; }
        public string? RemoteAddress { get; set; }
        public string ProfilePath { get; set; } = "";
        public int Port { get; set; }
        public int PageTimeoutSeconds { get; set; } = 30;
        public int DownloadTimeoutSeconds { get; set; } = 600;
        public bool NoSandbox { get; set; }
    }

    // A real browser reads the pages and files a plain request cannot: the source answers with a browser check.
    // The browser is started without automation flags, so the source sees an ordinary browser and no captcha is solved.
    internal sealed partial class SourceBrowser : ISourceBrowser, IDisposable
    {
        // The source's own check needs an undisturbed page; reading it too often makes it fail.
        private const int PollMilliseconds = 3000;

        // A fresh browser needs a moment before its first page counts.
        private const int StartMilliseconds = 4000;

        private readonly BrowserOptions _options;
        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly string? _path;
        private readonly string _profile;
        private readonly string _downloads;
        private Process? _process;
        private DevToolsClient? _client;
        private bool _refused;

        public SourceBrowser(BrowserOptions options)
        {
            _options = options;
            _path = ResolvePath(options.Path);
            _profile = Path.Combine(options.ProfilePath, "profile");
            _downloads = Path.Combine(options.ProfilePath, "downloads");
        }

        public bool IsAvailable => _path is not null || !string.IsNullOrWhiteSpace(_options.RemoteAddress);

        public async Task<string?> ReadPageAsync(Uri uri, CancellationToken ct)
        {
            if (!IsAvailable)
            {
                return null;
            }

            await _gate.WaitAsync(ct);
            try
            {
                await StartAsync(ct);
                await _client!.SendAsync("Page.navigate", new { url = uri.AbsoluteUri }, ct);
                var page = await WaitForPageAsync(uri, ct);
                if (page is null)
                {
                    // A source that refuses this client gets a new one next time.
                    if (_refused)
                    {
                        Reset();
                    }

                    return null;
                }

                return page;
            }
            catch (SourceBrowserException)
            {
                // A browser that cannot answer leaves the source as it was; the next attempt starts a new one.
                Reset();
                return null;
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task SaveFileAsync(Uri file, string destination, long? maximumBytes, Func<long, IDisposable>? reserveWrite, CancellationToken ct)
        {
            if (!IsAvailable)
            {
                throw new SourceBrowserException("No browser is configured.");
            }

            await _gate.WaitAsync(ct);
            try
            {
                await StartAsync(ct);
                Directory.CreateDirectory(_downloads);
                foreach (var stale in Directory.GetFiles(_downloads))
                {
                    File.Delete(stale);
                }

                await _client!.SendAsync("Browser.setDownloadBehavior", new { behavior = "allowAndName", downloadPath = _downloads }, ct);
                await _client.SendAsync("Page.navigate", new { url = file.AbsoluteUri }, ct);
                var downloaded = await WaitForFileAsync(ct);                try
                {
                    await using var source = new FileStream(downloaded, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
                    await DirectDownload.CopyAsync(source, destination, maximumBytes, reserveWrite, ct);
                }
                finally
                {
                    File.Delete(downloaded);
                }
            }
            finally
            {
                _gate.Release();
            }
        }

        public void Dispose()
        {
            Reset();
        }

        private void Reset()
        {
            _client?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _client = null;
            _refused = false;
            if (_process is not { } process)
            {
                // A browser elsewhere keeps its own profile.
                return;
            }

            try
            {
                process.Kill(true);
                process.WaitForExit(5000);
            }
            catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException or SystemException)
            {
                // The browser is already gone.
            }

            // A refused client is worth nothing; the next one starts fresh.
            if (Directory.Exists(_profile))
            {
                try
                {
                    Directory.Delete(_profile, true);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // The profile stays; the next attempt uses it as it is.
                }
            }
        }

        // The browser starts on the first use and keeps its profile, so the source's check is passed once.
        private async Task StartAsync(CancellationToken ct)
        {
            if (_client is not null)
            {
                return;
            }

            // A browser that already runs elsewhere is used as it is; its profile is its own.
            if (!string.IsNullOrWhiteSpace(_options.RemoteAddress))
            {
                var remote = await ReadPageSocketAsync(new Uri(_options.RemoteAddress), ct);
                _client = new DevToolsClient();
                await _client.ConnectAsync(remote, ct);
                await _client.SendAsync("Page.enable", null, ct);
                return;
            }

            Directory.CreateDirectory(_profile);

            // A browser with a chosen port answers the source's check; one that picks its own does not.
            var port = _options.Port > 0 ? _options.Port : FreePort();
            var arguments = $"--remote-debugging-port={port} --user-data-dir=\"{_profile}\" --no-first-run --no-default-browser-check --window-size=1280,900 about:blank";
            if (_options.NoSandbox)
            {
                arguments += " --no-sandbox";
            }

            _process = Process.Start(new ProcessStartInfo(_path!) { UseShellExecute = false, Arguments = arguments });

            // The browser needs a moment before its first page counts.
            await Task.Delay(StartMilliseconds, ct);
            var socket = await ReadPageSocketAsync(new Uri($"http://127.0.0.1:{port}"), ct);
            _client = new DevToolsClient();
            await _client.ConnectAsync(socket, ct);
            await _client.SendAsync("Page.enable", null, ct);
        }

        // A port of its own for the browser to answer on.
        private static int FreePort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        private static async Task<string> ReadPageSocketAsync(Uri address, CancellationToken ct)
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    using var document = JsonDocument.Parse(await http.GetStringAsync(new Uri(address, "/json/list"), ct));
                    foreach (var target in document.RootElement.EnumerateArray())
                    {
                        if (target.GetProperty("type").GetString() == "page" && target.TryGetProperty("webSocketDebuggerUrl", out var socket))
                        {
                            return socket.GetString()!;
                        }
                    }
                }
                catch (Exception exception) when (exception is HttpRequestException or JsonException)
                {
                    // The browser is still starting.
                }

                await Task.Delay(250, ct);
            }

            throw new SourceBrowserException("The browser did not offer a page.");
        }

        // The page answers when it is the wanted one and its own check is done; a check it cannot pass answers nothing.
        private async Task<string?> WaitForPageAsync(Uri uri, CancellationToken ct)
        {
            var deadline = DateTime.UtcNow.AddSeconds(_options.PageTimeoutSeconds);
            while (DateTime.UtcNow < deadline)
            {
                var page = await ReadStateAsync(ct);
                if (page is null)
                {
                    await Task.Delay(PollMilliseconds, ct);
                    continue;
                }

                if (page.Href.StartsWith("chrome-error://", StringComparison.Ordinal))
                {
                    return null;
                }

                if (string.Equals(Location(page.Href), Location(uri.AbsoluteUri), StringComparison.Ordinal))
                {
                    if (page.Ready == "complete" && page.Html.Length > 0)
                    {
                        if (!IsCheck(page.Html))
                        {
                            return page.Html;
                        }

                        _refused = true;
                    }
                }

                await Task.Delay(PollMilliseconds, ct);
            }

            return null;
        }

        // One round trip tells the page's address, its state and its markup.
        private async Task<BrowserPage?> ReadStateAsync(CancellationToken ct)
        {
            var answer = await _client!.EvaluateAsync("JSON.stringify({href: location.href, ready: document.readyState, html: document.documentElement ? document.documentElement.outerHTML : ''})", ct);
            if (answer.Length == 0)
            {
                return null;
            }

            try
            {
                using var document = JsonDocument.Parse(answer);
                var root = document.RootElement;
                return new BrowserPage(root.GetProperty("href").GetString() ?? "", root.GetProperty("ready").GetString() ?? "", root.GetProperty("html").GetString() ?? "");
            }
            catch (JsonException)
            {
                return null;
            }
        }

        // The file is complete when the browser drops its partial name.
        private async Task<string> WaitForFileAsync(CancellationToken ct)
        {
            var deadline = DateTime.UtcNow.AddSeconds(_options.DownloadTimeoutSeconds);
            while (DateTime.UtcNow < deadline)
            {
                foreach (var file in Directory.GetFiles(_downloads))
                {
                    if (!file.EndsWith(".crdownload", StringComparison.OrdinalIgnoreCase))
                    {
                        return file;
                    }
                }

                await Task.Delay(500, ct);
            }

            throw new SourceBrowserException("The browser did not fetch the file.");
        }

        private static string Location(string address)
        {
            return Uri.TryCreate(address, UriKind.Absolute, out var uri) ? uri.GetLeftPart(UriPartial.Path) : address;
        }

        private static bool IsCheck(string html)
        {
            // The check page of the source carries its own title; a captcha widget means the browser did not get through.
            if (html.Contains("ddg-captcha", StringComparison.OrdinalIgnoreCase) || html.Contains("Just a moment", StringComparison.OrdinalIgnoreCase)
                || html.Contains("cf-challenge", StringComparison.OrdinalIgnoreCase) || html.Contains("challenge-platform", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return Title().Match(html) is { Success: true } title && title.Groups[1].Value.Trim().Equals("ddos-guard", StringComparison.OrdinalIgnoreCase);
        }

        private static string? ResolvePath(string? configured)
        {
            if (configured is not null)
            {
                return configured.Length == 0 ? null : configured;
            }

            var found = Executables.Resolve(null, "chromium", "chromium-browser", "google-chrome", "chrome", "msedge");
            if (found is not null)
            {
                return found;
            }

            // Windows installs its browsers outside the PATH.
            var standard = OperatingSystem.IsWindows()
                ? new[] { @"C:\Program Files\Google\Chrome\Application\chrome.exe", @"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe", @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe" }
                : [];
            return standard.FirstOrDefault(File.Exists);
        }

        [GeneratedRegex("<title>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
        private static partial Regex Title();

        // What one page says about itself.
        private sealed record BrowserPage(string Href, string Ready, string Html);
    }
}
