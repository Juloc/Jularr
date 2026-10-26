using System.Text.Json;
using Jularr.Integrations.Data.Browser;

namespace Jularr.Integrations.Logic.Browser
{
    internal sealed partial class SourceBrowser : IBrowserInteraction
    {
        private long _manualUntil;
        private bool _manualMouseDown;
        private BrowserViewport _viewport = new(1280, 900);
        public bool HasManualControl => DateTime.UtcNow.Ticks < Interlocked.Read(ref _manualUntil);

        public async Task<BrowserViewport> BeginManualAsync(Uri address, DateTimeOffset expiresAt, CancellationToken ct)
        {
            if (!IsAvailable || !string.IsNullOrWhiteSpace(_options.RemoteAddress) || !await IsSafeSourceAsync(address, ct))
            {
                throw new SourceBrowserException("Manual assistance requires a local browser and a public HTTPS source.");
            }

            await _gate.WaitAsync(ct);
            try
            {
                await StartBrowserAsync(ct);
                _manualMouseDown = false;
                Interlocked.Exchange(ref _manualUntil, expiresAt.UtcTicks);
                await _client!.SendAsync("Browser.setDownloadBehavior", new { behavior = "deny" }, ct);
                await _client.SendAsync("Page.bringToFront", null, ct);
                var current = await _client!.EvaluateAsync("location.href", ct);
                if (!Uri.TryCreate(current, UriKind.Absolute, out var currentAddress) || !SameOrigin(currentAddress, address))
                {
                    await _client.SendAsync("Page.navigate", new { url = address.AbsoluteUri }, ct);
                }
                using var metrics = JsonDocument.Parse(await _client.SendAsync("Page.getLayoutMetrics", null, ct));
                var viewport = metrics.RootElement.GetProperty("result").GetProperty("cssVisualViewport");
                _viewport = new BrowserViewport((int)viewport.GetProperty("clientWidth").GetDouble(), (int)viewport.GetProperty("clientHeight").GetDouble());
                if (_viewport.Width is < 1 or > 4096 || _viewport.Height is < 1 or > 4096)
                {
                    throw new SourceBrowserException("The browser viewport is outside the supported size.");
                }

                Note("manual", "a permitted administrator controls the source browser");
                return _viewport;
            }
            catch
            {
                Interlocked.Exchange(ref _manualUntil, 0);
                throw;
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task<byte[]> CaptureManualAsync(DateTimeOffset expiresAt, CancellationToken ct)
        {
            await _gate.WaitAsync(ct);
            try
            {
                RequireManual();
                using var answer = JsonDocument.Parse(await _client!.SendAsync("Page.captureScreenshot", new { format = "jpeg", quality = 70, captureBeyondViewport = false }, ct));
                var encoded = answer.RootElement.GetProperty("result").GetProperty("data").GetString();
                if (encoded is null || encoded.Length > 8 * 1024 * 1024) throw new SourceBrowserException("The browser frame is unavailable or too large.");
                Interlocked.Exchange(ref _manualUntil, expiresAt.UtcTicks);
                return Convert.FromBase64String(encoded);
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task InputManualAsync(string action, int x, int y, int delta, string? key, string? text, DateTimeOffset expiresAt, CancellationToken ct)
        {
            await _gate.WaitAsync(ct);
            try
            {
                RequireManual();
                if (x < 0 || x >= _viewport.Width || y < 0 || y >= _viewport.Height || Math.Abs((long)delta) > 2000 || text?.Length > 1000)
                {
                    throw new SourceBrowserException("The browser input is outside its supported bounds.");
                }

                switch (action)
                {
                    case "down":
                    case "up":
                    case "move":
                        if (action != "move") _manualMouseDown = action == "down";
                        await _client!.SendAsync("Input.dispatchMouseEvent", new
                        {
                            type = action == "down" ? "mousePressed" : action == "up" ? "mouseReleased" : "mouseMoved",
                            x, y, button = action == "move" ? "none" : "left", buttons = _manualMouseDown ? 1 : 0, clickCount = action == "move" ? 0 : 1
                        }, ct);
                        break;
                    case "wheel":
                        await _client!.SendAsync("Input.dispatchMouseEvent", new { type = "mouseWheel", x, y, deltaX = 0, deltaY = delta }, ct);
                        break;
                    case "text" when !string.IsNullOrEmpty(text):
                        await _client!.SendAsync("Input.insertText", new { text }, ct);
                        break;
                    case "key" when key is "Enter" or "Tab" or "Backspace" or "Delete" or "ArrowLeft" or "ArrowRight" or "ArrowUp" or "ArrowDown" or "Escape" or "Home" or "End":
                        var windowsVirtualKeyCode = key switch { "Enter" => 13, "Tab" => 9, "Backspace" => 8, "Delete" => 46, "ArrowLeft" => 37, "ArrowUp" => 38,
                            "ArrowRight" => 39, "ArrowDown" => 40, "Escape" => 27, "Home" => 36, _ => 35 };
                        await _client!.SendAsync("Input.dispatchKeyEvent", new { type = "keyDown", key, code = key, windowsVirtualKeyCode, text = key == "Enter" ? "\r" : "" }, ct);
                        await _client.SendAsync("Input.dispatchKeyEvent", new { type = "keyUp", key, code = key, windowsVirtualKeyCode }, ct);
                        break;
                    default:
                        throw new SourceBrowserException("This browser input is not supported.");
                }

                Interlocked.Exchange(ref _manualUntil, expiresAt.UtcTicks);
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task EndManualAsync(CancellationToken ct)
        {
            await _gate.WaitAsync(ct);
            try
            {
                Interlocked.Exchange(ref _manualUntil, 0);
                Note("manual-ended", "the source browser is available to automatic work again");
            }
            finally
            {
                _gate.Release();
            }
        }

        private void RequireManual()
        {
            if (!HasManualControl || _client is null) throw new SourceBrowserException("The manual browser session has ended.");
        }
    }
}
