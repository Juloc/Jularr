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

        public async Task<BrowserViewV1?> ReadOwnAsync(Guid owner, CancellationToken ct)
        {
            await _gate.WaitAsync(ct);
            try
            {
                return _lease is { } lease && lease.Owner == owner
                    ? new BrowserViewV1(lease.Id, lease.Viewport.Width, lease.Viewport.Height, lease.AcquisitionConnectionId,
                        lease.ExpiresAt > time.GetUtcNow() && browser.HasManualControl, lease.OperationId) : null;
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task<BrowserViewV1> OpenAsync(Guid owner, long acquisitionConnectionId, Uri address, CancellationToken ct, long? operationId = null)
        {
            await _gate.WaitAsync(ct);
            try
            {
                var now = time.GetUtcNow();
                Guid? continuing = null;
                if (_lease is { } current && current.ExpiresAt > now && browser.HasManualControl)
                {
                    if (current.Owner != owner) throw new ServiceUnavailableException("browser_assistance_busy");
                    if (current.AcquisitionConnectionId == acquisitionConnectionId && current.OperationId == operationId) continuing = current.Id;
                }

                var expires = now.Add(IdleTimeout);
                var viewport = await browser.BeginManualAsync(address, expires, ct, opensPage: operationId is not null);
                var lease = new Lease(continuing ?? Guid.NewGuid(), owner, acquisitionConnectionId, expires, viewport, operationId);
                _lease = lease;
                return new BrowserViewV1(lease.Id, viewport.Width, viewport.Height, acquisitionConnectionId, true, operationId);
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
            if (active && (lease.ExpiresAt <= time.GetUtcNow() || !browser.HasManualControl)) throw new ServiceGoneException("browser_assistance_ended");
            return lease;
        }

        public void Dispose() => _gate.Dispose();

        private sealed class Lease(Guid id, Guid owner, long acquisitionConnectionId, DateTimeOffset expiresAt, BrowserViewport viewport, long? operationId)
        {
            public Guid Id { get; } = id;
            public Guid Owner { get; } = owner;
            public long AcquisitionConnectionId { get; } = acquisitionConnectionId;
            public long? OperationId { get; } = operationId;
            public DateTimeOffset ExpiresAt { get; set; } = expiresAt;
            public BrowserViewport Viewport { get; } = viewport;
            public byte[]? Frame { get; set; }
            public DateTimeOffset FrameAt { get; set; }
        }
    }
}
