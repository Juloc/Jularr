using System.Security;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Browser.V1;
using Jularr.Backend.Data.Common;
using Jularr.Integrations.Data.Browser;

namespace Jularr.Backend.Service.Core
{
    // The session that opened the shared browser owns its controls until it closes or becomes idle.
    internal sealed class BrowserAssistance(IBrowserInteraction browser, TimeProvider time) : IDisposable
    {
        private readonly SemaphoreSlim _gate = new(1, 1);
        private Lease? _lease;
        private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(2);
        private static readonly TimeSpan OpenTimeout = TimeSpan.FromSeconds(150);
        private static readonly TimeSpan QuickOpen = TimeSpan.FromMilliseconds(100);

        public async Task<BrowserViewV1?> ReadOwnAsync(Guid owner, CancellationToken ct)
        {
            await _gate.WaitAsync(ct);
            try
            {
                return _lease is { } lease && lease.Owner == owner ? View(lease) : null;
            }
            finally
            {
                _gate.Release();
            }
        }

        // The state of an opening or open view: what the browser is doing for it, how it ended, and the picture's size once it is there.
        public async Task<BrowserViewV1> StateAsync(Guid owner, Guid id, CancellationToken ct)
        {
            await _gate.WaitAsync(ct);
            try
            {
                return View(Require(owner, id, active: false));
            }
            finally
            {
                _gate.Release();
            }
        }

        private BrowserViewV1 View(Lease lease)
        {
            var state = lease.State;
            var active = state.Phase == "ready" && lease.ExpiresAt > time.GetUtcNow() && browser.HasManualControl;
            return new BrowserViewV1(lease.Id, state.Viewport.Width, state.Viewport.Height, lease.AcquisitionConnectionId, active, lease.OperationId, state.Phase, state.Error);
        }

        // Opening the view starts the browser, which takes seconds when it is not running yet. The answer therefore comes at once: when the browser
        // is quick (already running) the view is ready in it, otherwise it is "opening" with the step the browser is at, and the page asks for the
        // state until it is ready or has failed. A second request for the view that is opening joins it.
        public async Task<BrowserViewV1> OpenAsync(Guid owner, long acquisitionConnectionId, Uri address, CancellationToken ct, long? operationId = null)
        {
            Lease lease;
            Lease? previous;
            await _gate.WaitAsync(ct);
            try
            {
                var now = time.GetUtcNow();
                Guid? continuing = null;
                previous = _lease;
                if (_lease is { } current && current.ExpiresAt > now && (browser.HasManualControl || current.State.Phase is "checking" or "waiting" or "starting"))
                {
                    if (current.Owner != owner) throw new ServiceUnavailableException("browser_assistance_busy");
                    if (current.AcquisitionConnectionId == acquisitionConnectionId && current.OperationId == operationId)
                    {
                        if (current.State.Phase is "checking" or "waiting" or "starting") return View(current);
                        continuing = current.Id;
                    }
                }

                // The opening may outlast the idle time; it is bounded by OpenTimeout, and the view's own idle time starts when it is ready.
                var expires = now.Add(OpenTimeout + IdleTimeout);
                lease = new Lease(continuing ?? Guid.NewGuid(), owner, acquisitionConnectionId, expires, new BrowserViewport(1280, 900), operationId);
                previous?.Opening?.Cancel();
                _lease = lease;
                lease.Opening = new CancellationTokenSource(OpenTimeout);
                lease.Task = OpenInBackgroundAsync(lease, address, expires, operationId is not null);
            }
            finally
            {
                _gate.Release();
            }

            // A quick open is answered as before; a slow one answers now and is followed by its state.
            await Task.WhenAny(lease.Task, Task.Delay(QuickOpen, ct));
            if (lease.Task.IsCompleted && lease.State.Phase == "failed")
            {
                await _gate.WaitAsync(ct);
                try
                {
                    if (ReferenceEquals(_lease, lease)) _lease = previous is { } earlier && !ReferenceEquals(earlier, lease) ? earlier : null;
                }
                finally
                {
                    _gate.Release();
                }

                throw new ServiceUnavailableException("browser_assistance_unavailable", lease.Failure);
            }

            return View(lease);
        }

        // The browser's own work, with no request behind it: it ends in "ready" or "failed" and is never thrown at anybody.
        private async Task OpenInBackgroundAsync(Lease lease, Uri address, DateTimeOffset expires, bool opensPage)
        {
            try
            {
                var viewport = await browser.BeginManualAsync(address, expires, lease.Opening!.Token, opensPage, phase =>
                    lease.State = lease.State with { Phase = phase.ToString().ToLowerInvariant() });
                lease.ExpiresAt = time.GetUtcNow().Add(IdleTimeout);
                lease.State = new LeaseState("ready", null, viewport);
            }
            catch (Exception exception) when (exception is SourceBrowserException or OperationCanceledException)
            {
                lease.Failure = exception;
                lease.State = lease.State with { Phase = "failed", Error = ReasonOf(exception) };
            }
        }

        // Why the browser did not open, as a key the page has a text for; the exception's own words are for the log.
        internal static string ReasonOf(Exception exception) => exception switch
        {
            OperationCanceledException => "timeout",
            _ when exception.Message.Contains("local browser", StringComparison.Ordinal) => "unsupported",
            _ when exception.Message.Contains("not installed", StringComparison.Ordinal) => "not_installed",
            _ when exception.Message.Contains("debugging port", StringComparison.Ordinal) || exception.Message.Contains("did not offer a page", StringComparison.Ordinal) => "no_start",
            _ => "failed"
        };

        public async Task<byte[]> FrameAsync(Guid owner, Guid id, CancellationToken ct)
        {
            await _gate.WaitAsync(ct);
            try
            {
                var lease = Require(owner, id);
                var now = time.GetUtcNow();
                lease.ExpiresAt = now.Add(IdleTimeout);
                if (lease.Frame is not null && now - lease.FrameAt < TimeSpan.FromMilliseconds(500)) return lease.Frame;
                lease.Frame = await browser.CaptureManualAsync(lease.ExpiresAt, ct);
                lease.FrameAt = now;
                return lease.Frame;
            }
            catch (SourceBrowserException exception)
            {
                throw new ServiceUnavailableException("browser_assistance_unavailable", exception);
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task InputAsync(Guid owner, Guid id, BrowserInputDataV1 input, CancellationToken ct)
        {
            await _gate.WaitAsync(ct);
            try
            {
                var lease = Require(owner, id);
                if (input.Action is not ("down" or "up" or "move" or "wheel" or "key" or "text"))
                {
                    throw new DataValidationException(nameof(input.Action), "unknown_catalog_value");
                }

                if (input.X < 0 || input.Y < 0 || input.X >= lease.Viewport.Width || input.Y >= lease.Viewport.Height)
                {
                    throw new DataValidationException(nameof(input.X), "out_of_range");
                }

                if (Math.Abs((long)input.Delta) > 2000 || input.Text?.Length > 1000 ||
                    input.Action == "text" && string.IsNullOrEmpty(input.Text) ||
                    input.Action == "key" && input.Key is not ("Enter" or "Tab" or "Backspace" or "Delete" or "ArrowLeft" or "ArrowRight" or "ArrowUp" or "ArrowDown" or "Escape" or "Home" or "End"))
                {
                    throw new DataValidationException(nameof(input.Action), "out_of_range");
                }

                lease.ExpiresAt = time.GetUtcNow().Add(IdleTimeout);
                await browser.InputManualAsync(input.Action, input.X, input.Y, input.Delta, input.Key, input.Text, lease.ExpiresAt, ct);
                lease.Frame = null;
            }
            catch (SourceBrowserException exception)
            {
                throw new ServiceUnavailableException("browser_assistance_unavailable", exception);
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task<long> CloseAsync(Guid owner, Guid id, CancellationToken ct)
        {
            await _gate.WaitAsync(ct);
            try
            {
                var lease = Require(owner, id, active: false);
                lease.Opening?.Cancel();
                await browser.EndManualAsync(ct);
                _lease = null;
                return lease.AcquisitionConnectionId;
            }
            finally
            {
                _gate.Release();
            }
        }

        private Lease Require(Guid owner, Guid id, bool active = true)
        {
            if (_lease is not { } lease || lease.Id != id)
            {
                throw new ServiceGoneException("browser_assistance_ended");
            }

            if (lease.Owner != owner) throw new SecurityException();
            if (active && lease.State.Phase != "ready") throw new ServiceUnavailableException(lease.State.Phase == "failed" ? "browser_assistance_unavailable" : "browser_assistance_opening");
            if (active && (lease.ExpiresAt <= time.GetUtcNow() || !browser.HasManualControl)) throw new ServiceGoneException("browser_assistance_ended");
            return lease;
        }

        public void Dispose() => _gate.Dispose();

        private sealed record LeaseState(string Phase, string? Error, BrowserViewport Viewport);

        private sealed class Lease(Guid id, Guid owner, long acquisitionConnectionId, DateTimeOffset expiresAt, BrowserViewport viewport, long? operationId)
        {
            public Guid Id { get; } = id;
            public Guid Owner { get; } = owner;
            public long AcquisitionConnectionId { get; } = acquisitionConnectionId;
            public long? OperationId { get; } = operationId;
            public DateTimeOffset ExpiresAt { get; set; } = expiresAt;
            public byte[]? Frame { get; set; }
            public DateTimeOffset FrameAt { get; set; }

            // Written by the opening in the background, read by every request: the whole state is replaced at once.
            private volatile LeaseState _state = new("checking", null, viewport);
            public LeaseState State { get => _state; set => _state = value; }
            public BrowserViewport Viewport => _state.Viewport;
            public CancellationTokenSource? Opening { get; set; }
            public Task Task { get; set; } = Task.CompletedTask;
            public Exception? Failure { get; set; }
        }
    }
}
