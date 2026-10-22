using System.Diagnostics;
using System.Globalization;
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

        // Local HTTP test fixtures need access that production source navigation must not have.
        internal bool AllowPrivateAddressesForTests { get; set; }
    }

    // A real browser reads the pages and files a plain request cannot: the source answers with a browser check.
    // The browser is started without automation flags, so the source sees an ordinary browser and no captcha is solved.
    internal sealed partial class SourceBrowser : ISourceBrowser, IDisposable
    {
        // The source's own check needs an undisturbed page; reading it too often makes it fail.
        private const int PollMilliseconds = 3000;
        private const int MaximumPageChars = 4 * 1024 * 1024;

        // A fresh browser needs a moment before its first page counts.
        private const int StartMilliseconds = 4000;

        // The file in the profile that holds the port of the browser.
        private const string PortFile = "browser.port";

        private readonly BrowserOptions _options;
        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly string? _path;
        private readonly string _profile;
        private readonly string _downloads;
        private readonly List<SourceBrowserEvent> _events = [];
        private readonly Lock _eventsLock = new();
        private Process? _process;
        private DevToolsClient? _client;
        private bool _refused;
        private string? _version;
        private string? _lastUrl;
        private string? _lastOutcome;
        private string? _lastError;
        private DateTime? _lastAttemptAt;

        public SourceBrowser(BrowserOptions options)
        {
            _options = options;
            _path = ResolvePath(options.Path);
            _profile = Path.Combine(options.ProfilePath, "profile");
            _downloads = Path.Combine(options.ProfilePath, "downloads");
        }

        public bool IsAvailable => _path is not null || !string.IsNullOrWhiteSpace(_options.RemoteAddress);

        public SourceBrowserStatus Status => new(_path, _options.RemoteAddress, _profile, _client is not null, _version, _lastUrl, _lastOutcome, _lastError, _lastAttemptAt, Events.Count);

        public IReadOnlyList<SourceBrowserEvent> Events
        {
            get
            {
                lock (_eventsLock)
                {
                    return [.. _events];
                }
            }
        }

        public async Task<SourceBrowserStatus> StartAsync(CancellationToken ct)
        {
            await _gate.WaitAsync(ct);
            try
            {
                await StartBrowserAsync(ct);
                return Status;
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task StopAsync()
        {
            await CloseAsync();
            Note("stopped", "the browser is stopped");
        }

        // A new client: the profile and the state the source knows of the old one go.
        public async Task ResetAsync()
        {
            await _gate.WaitAsync(CancellationToken.None);
            try
            {
                var running = _client;
                if (running is not null)
                {
                    try
                    {
                        await running.SendAsync("Network.clearBrowserCookies", null, CancellationToken.None);
                    }
                    catch (Exception exception) when (exception is SourceBrowserException or OperationCanceledException or System.Text.Json.JsonException)
                    {
                        // The browser is gone; the profile goes with it.
                    }
                }

                // The browser that holds the profile goes with it.
                await CloseBrowserAsync(CancellationToken.None);
                await CloseRunningBrowserAsync(CancellationToken.None);
                await CloseAsync();
                await DeleteProfileAsync(CancellationToken.None);

                Note("reset", "the browser begins as a new client");
            }
            finally
            {
                _gate.Release();
            }
        }

        // The profile goes with the browser; a browser that is still leaving holds it for a moment.
        private async Task DeleteProfileAsync(CancellationToken ct)
        {
            for (var attempt = 0; attempt < 10 && Directory.Exists(_profile); attempt++)
            {
                try
                {
                    Directory.Delete(_profile, true);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    await Task.Delay(500, ct);
                }
            }
        }

        // The browser that holds the profile goes with it; a browser that runs elsewhere is told to quit.
        private async Task CloseBrowserAsync(CancellationToken ct)
        {
            var client = _client;
            if (client is null)
            {
                return;
            }

            try
            {
                await client.SendAsync("Browser.close", null, ct);
            }
            catch (Exception exception) when (exception is SourceBrowserException or OperationCanceledException or System.Text.Json.JsonException)
            {
                // The browser is already gone.
            }
        }

        // The browser that runs with this profile goes with it, even when an earlier run started it.
        private async Task CloseRunningBrowserAsync(CancellationToken ct)
        {
            try
            {
                var file = Path.Combine(_profile, PortFile);
                if (!File.Exists(file))
                {
                    return;
                }

                var text = (await File.ReadAllTextAsync(file, ct)).Trim();
                if (!int.TryParse(text, out var port) || port <= 0)
                {
                    return;
                }

                var socket = await ReadPageSocketAsync(new Uri($"http://127.0.0.1:{port}"), ct);
                await using var client = new DevToolsClient();
                await client.ConnectAsync(socket, ct);
                await client.SendAsync("Browser.close", null, ct);
            }
            catch (Exception exception) when (exception is SourceBrowserException or IOException or UnauthorizedAccessException or System.Text.Json.JsonException or OperationCanceledException)
            {
                // The browser is already gone.
            }
        }

        public async Task<string?> ReadPageAsync(Uri uri, CancellationToken ct)
        {
            if (!IsAvailable)
            {
                return null;
            }

            if (!await IsSafeSourceAsync(uri, ct))
            {
                return null;
            }

            await _gate.WaitAsync(ct);
            try
            {
                await StartBrowserAsync(ct);
                _lastUrl = uri.AbsoluteUri;
                _lastAttemptAt = DateTime.UtcNow;
                Note("navigate", uri.AbsoluteUri);
                await _client!.SendAsync("Page.navigate", new { url = uri.AbsoluteUri }, ct);
                var page = await WaitForPageAsync(uri, ct);
                if (page is null)
                {
                    _lastOutcome = _refused ? "refused" : "no answer";
                    _lastError = _refused ? "the source refused this browser" : null;
                    Note(_lastOutcome, _lastError ?? $"no answer for {uri.AbsoluteUri}");
                    // A source that refuses this client gets a new one next time.
                    if (_refused)
                    {
                        await ForgetAsync(uri, ct);
                        await CloseAsync();
                    }

                    return null;
                }

                _lastOutcome = "read";
                _lastError = null;
                Note("read", $"{uri.AbsoluteUri} ({page.Length} bytes)");
                return page;
            }
            catch (Exception exception) when (exception is SourceBrowserException or OperationCanceledException or System.Text.Json.JsonException)
            {
                _lastOutcome = "error";
                _lastError = exception.Message;
                Note("error", exception.Message);
                // A browser that cannot answer leaves the source as it was; the next attempt starts a new one.
                await CloseAsync();
                return null;
            }
            finally
            {
                _gate.Release();
            }
        }

        // Several addresses at the same time; the first that answers wins.
        public async Task<BrowserPage?> ReadPagesAsync(IReadOnlyList<Uri> addresses, CancellationToken ct)
        {
            if (!IsAvailable || addresses.Count == 0)
            {
                return null;
            }

            var safe = new List<Uri>();
            foreach (var address in addresses)
            {
                if (await IsSafeSourceAsync(address, ct))
                {
                    safe.Add(address);
                }
            }

            if (safe.Count == 0)
            {
                return null;
            }

            await _gate.WaitAsync(ct);
            var opened = new List<OpenPage>();
            try
            {
                await StartBrowserAsync(ct);
                foreach (var address in safe)
                {
                    var page = await OpenPageAsync(address, ct);
                    if (page is not null)
                    {
                        opened.Add(page);
                    }
                }

                if (opened.Count == 0)
                {
                    return null;
                }

                var deadline = DateTime.UtcNow.AddSeconds(_options.PageTimeoutSeconds);
                while (DateTime.UtcNow < deadline)
                {
                    foreach (var page in opened)
                    {
                        var state = await ReadStateAsync(page.Session, ct);
                        if (state is null || !string.Equals(Location(state.Href), Location(page.Address.AbsoluteUri), StringComparison.Ordinal))
                        {
                            continue;
                        }

                        if (state.Ready != "complete" || state.Html.Length == 0 || IsCheck(state.Html))
                        {
                            continue;
                        }

                        Note("read", $"{page.Address.AbsoluteUri} ({state.Html.Length} bytes)");
                        return new BrowserPage(page.Address, state.Html);
                    }

                    await Task.Delay(PollMilliseconds, ct);
                }

                return null;
            }
            finally
            {
                foreach (var page in opened)
                {
                    await ClosePageAsync(page.Target, ct);
                }

                _gate.Release();
            }
        }

        // A page of its own, so several addresses can be read at the same time.
        private async Task<OpenPage?> OpenPageAsync(Uri address, CancellationToken ct)
        {
            try
            {
                var (target, session) = await _client!.OpenPageAsync(ct);
                await _client.SendAsync("Page.enable", null, ct, session);
                await _client.SendAsync("Page.navigate", new { url = address.AbsoluteUri }, ct, session);
                return new OpenPage(target, session, address);
            }
            catch (Exception exception) when (exception is SourceBrowserException or OperationCanceledException or System.Text.Json.JsonException)
            {
                return null;
            }
        }

        private async Task ClosePageAsync(string target, CancellationToken ct)
        {
            try
            {
                await _client!.ClosePageAsync(target, ct);
            }
            catch (Exception exception) when (exception is SourceBrowserException or OperationCanceledException)
            {
                // The page is gone anyway.
            }
        }

        public async Task SaveFileAsync(Uri file, string destination, long? maximumBytes, Func<long, IDisposable>? reserveWrite, CancellationToken ct)
        {
            if (!IsAvailable)
            {
                throw new SourceBrowserException("No browser is configured.");
            }

            if (!await IsSafeSourceAsync(file, ct))
            {
                throw new SourceBrowserException("The browser cannot visit a private or insecure source.");
            }

            await _gate.WaitAsync(ct);
            try
            {
                await StartBrowserAsync(ct);
                Directory.CreateDirectory(_downloads);
                foreach (var stale in Directory.GetFiles(_downloads))
                {
                    File.Delete(stale);
                }

                await _client!.SendAsync("Browser.setDownloadBehavior", new { behavior = "allowAndName", downloadPath = _downloads }, ct);
                await _client.SendAsync("Page.navigate", new { url = file.AbsoluteUri }, ct);
                var downloaded = await WaitForFileAsync(ct);
                try
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

        private async Task<bool> IsSafeSourceAsync(Uri uri, CancellationToken ct)
        {
            if (_options.AllowPrivateAddressesForTests)
            {
                return true;
            }

            try
            {
                await PublicNetwork.EnsurePublicHttpsAsync(uri, ct);
                return true;
            }
            catch (HttpRequestException)
            {
                Note("refused", "the source is not a public HTTPS address");
                return false;
            }
            catch (SocketException)
            {
                Note("refused", "the source address could not be resolved");
                return false;
            }
        }

        public void Dispose()
        {
            var client = _client;
            _client = null;
            if (client is not null)
            {
                // The browser is gone; its answer is not waited for.
                _ = client.DisposeAsync();
            }

            if (_process is { } process)
            {
                try
                {
                    process.Kill(true);
                    process.WaitForExit(5000);
                }
                catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException or SystemException)
                {
                    // The browser is already gone.
                }
            }
        }

        private async Task CloseAsync()
        {
            var client = _client;
            _client = null;
            _refused = false;
            if (client is not null)
            {
                await client.DisposeAsync();
            }

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

        // A refused client is worth nothing: the source's own state of it goes, so the next attempt is a new one.
        private async Task ForgetAsync(Uri uri, CancellationToken ct)
        {
            try
            {
                await _client!.SendAsync("Network.clearBrowserCookies", null, ct);
                await _client.SendAsync("Storage.clearDataForOrigin", new { origin = uri.GetLeftPart(UriPartial.Authority), storageTypes = "all" }, ct);
            }
            catch (Exception exception) when (exception is SourceBrowserException or OperationCanceledException or System.Text.Json.JsonException)
            {
                // The browser is gone; the next attempt starts a new one.
            }
        }

        // The browser starts on the first use and keeps its profile, so the source's check is passed once.
        private async Task StartBrowserAsync(CancellationToken ct)
        {
            if (_client is not null)
            {
                return;
            }

            // A browser that already runs elsewhere is used as it is; its profile is its own.
            if (!string.IsNullOrWhiteSpace(_options.RemoteAddress))
            {
                await UseRemoteBrowserAsync(ct);
                return;
            }

            Directory.CreateDirectory(_profile);

            // A browser that still runs with this profile is used as it is; it has passed the source's check already.
            if (await UseRunningBrowserAsync(ct))
            {
                return;
            }

            // A lock of an earlier run on this machine would keep the browser from starting.
            foreach (var stale in Directory.GetFiles(_profile, "Singleton*"))
            {
                try
                {
                    File.Delete(stale);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // The lock stays; the browser tells us that it cannot start.
                }
            }

            // A browser with a chosen port answers the source's check; one that picks its own does not.
            var port = _options.Port > 0 ? _options.Port : FreePort();
            var arguments = $"--remote-debugging-port={port} --user-data-dir=\"{_profile}\" --no-first-run --no-default-browser-check --window-size=1280,900 about:blank";
            if (_options.NoSandbox)
            {
                arguments += " --no-sandbox";
            }

            // A browser that does not open its port may have met the remains of an earlier one; it is tried again.
            for (var attempt = 1; attempt <= 3; attempt++)
            {
                _process = Process.Start(new ProcessStartInfo(_path!) { UseShellExecute = false, Arguments = arguments });

                // The browser needs a moment before its first page counts.
                await Task.Delay(StartMilliseconds, ct);
                try
                {
                    var socket = await ReadPageSocketAsync(new Uri($"http://127.0.0.1:{port}"), ct);
                    _client = new DevToolsClient();
                    await _client.ConnectAsync(socket, ct);
                    await _client.SendAsync("Page.enable", null, ct);
                    await RememberVersionAsync(ct);
                    await RememberPortAsync(port);
                    Note("started", $"the browser {_path} answers on port {port}");
                    return;
                }
                catch (SourceBrowserException)
                {
                    await CloseProcessAsync();
                    if (attempt < 3)
                    {
                        Note("retry", $"the browser did not answer on port {port}; it is started again");
                    }
                }
            }

            throw new SourceBrowserException("The browser did not open its debugging port.");
        }

        // The browser that still runs with this profile answers on the port it was started with.
        private async Task<bool> UseRunningBrowserAsync(CancellationToken ct)
        {
            try
            {
                var file = Path.Combine(_profile, PortFile);
                if (!File.Exists(file))
                {
                    return false;
                }

                var text = (await File.ReadAllTextAsync(file, ct)).Trim();
                if (!int.TryParse(text, out var port) || port <= 0)
                {
                    return false;
                }

                var socket = await ReadPageSocketAsync(new Uri($"http://127.0.0.1:{port}"), ct);
                _client = new DevToolsClient();
                await _client.ConnectAsync(socket, ct);
                await _client.SendAsync("Page.enable", null, ct);
                await RememberVersionAsync(ct);
                Note("started", $"the browser that still runs with this profile answers on port {port}");
                return true;
            }
            catch (Exception exception) when (exception is SourceBrowserException or IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            {
                // Nothing runs with this profile; a new browser is started.
                return false;
            }
        }

        // The port is written down; a browser that still runs is found after a crash.
        private async Task RememberPortAsync(int port)
        {
            try
            {
                await File.WriteAllTextAsync(Path.Combine(_profile, PortFile), port.ToString(CultureInfo.InvariantCulture));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // The port stays unknown; the next start tries a new browser.
            }
        }

        // The browser that runs elsewhere is used as it is; its profile is its own.
        private async Task UseRemoteBrowserAsync(CancellationToken ct)
        {
            var remote = await ReadPageSocketAsync(new Uri(_options.RemoteAddress!), ct);
            _client = new DevToolsClient();
            await _client.ConnectAsync(remote, ct);
            await _client.SendAsync("Page.enable", null, ct);
            await RememberVersionAsync(ct);
            Note("started", $"the browser at {_options.RemoteAddress} answers");
        }

        private async Task CloseProcessAsync()
        {
            if (_process is not { } process)
            {
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
        }

        // The browser tells which one it is.
        private async Task RememberVersionAsync(CancellationToken ct)
        {
            try
            {
                var answer = await _client!.SendAsync("Browser.getVersion", null, ct);
                using var document = System.Text.Json.JsonDocument.Parse(answer);
                if (document.RootElement.TryGetProperty("result", out var result) && result.TryGetProperty("product", out var product))
                {
                    _version = product.GetString();
                }
            }
            catch (Exception exception) when (exception is SourceBrowserException or System.Text.Json.JsonException or OperationCanceledException)
            {
                // The version stays unknown.
            }
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
        private async Task<BrowserState?> ReadStateAsync(CancellationToken ct) => await ReadStateAsync(null, ct);

        private async Task<BrowserState?> ReadStateAsync(string? session, CancellationToken ct)
        {
            string answer;
            try
            {
                answer = await _client!.EvaluateAsync("JSON.stringify({href: location.href, ready: document.readyState, html: document.documentElement ? document.documentElement.outerHTML.slice(0, 4194305) : ''})", ct, session);
            }
            catch (Exception exception) when (exception is SourceBrowserException or OperationCanceledException)
            {
                return null;
            }

            if (answer.Length == 0)
            {
                return null;
            }

            try
            {
                using var document = JsonDocument.Parse(answer);
                var root = document.RootElement;
                var html = root.GetProperty("html").GetString() ?? "";
                return html.Length > MaximumPageChars ? null : new BrowserState(root.GetProperty("href").GetString() ?? "", root.GetProperty("ready").GetString() ?? "", html);
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

        // What the browser did, for the administrator to see.
        private void Note(string kind, string message)
        {
            lock (_eventsLock)
            {
                _events.Add(new SourceBrowserEvent(DateTime.UtcNow, kind, message));
                if (_events.Count > 50)
                {
                    _events.RemoveRange(0, _events.Count - 50);
                }
            }
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
        private sealed record BrowserState(string Href, string Ready, string Html);

        // A page of its own, for reading several addresses at the same time.
        private sealed record OpenPage(string Target, string Session, Uri Address);
    }
}
