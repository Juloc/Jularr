using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Jularr.Backend.Data.Admin.Network.V1;
using Jularr.Backend.Logic.Network;
using Jularr.Integrations.Data.Network;
using Jularr.Integrations.Logic.Network;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Jularr.Backend.Service.Core
{
    public sealed class VpnOptions
    {
        // The userspace WireGuard helper of the image.
        public string WireproxyPath { get; set; } = "wireproxy";

        // An outside service that answers {"ip": ..., "country": ...}; asked through the tunnel and once directly to compare.
        public string ExitCheckAddress { get; set; } = "https://ipinfo.io/json";

        // Pinged through the tunnel while it runs; empty turns the ping off and the tunnel never counts as alive.
        public string? CheckAliveAddress { get; set; } = "1.1.1.1";

        // Where a starting helper reads its configuration; the file is removed once it runs.
        public string WorkPath { get; set; } = "";
    }

    // The tunnels of the VPN profiles. Nothing runs until a client or an administrator asks for a profile's tunnel; a tunnel nobody uses stops after a while,
    // and switching the module off stops them all. A client gets a lease: its connections go through that tunnel or fail, there is no direct path behind it.
    public sealed class VpnTunnels : IAsyncDisposable
    {
        public const string ProtectorPurpose = "Jularr.Network.VpnProfile.V1";
        private static readonly TimeSpan HealthInterval = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan ExitInterval = TimeSpan.FromMinutes(10);
        private static readonly TimeSpan IdleStop = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan HostExitAge = TimeSpan.FromMinutes(10);
        private const int CheckAliveSeconds = 20;
        private readonly ConcurrentDictionary<long, Tunnel> _tunnels = new();
        private readonly SemaphoreSlim _starting = new(1, 1);
        private readonly VpnOptions _options;
        private readonly string _workDirectory;
        private readonly TimeProvider _time;
        private readonly ILogger<VpnTunnels> _logger;
        private readonly IDataProtector _protector;
        private (PublicExit Exit, DateTimeOffset At)? _hostExit;

        public VpnTunnels(IOptions<VpnOptions> options, IDataProtectionProvider protection, TimeProvider time, ILogger<VpnTunnels> logger)
        {
            _options = options.Value;
            _protector = protection.CreateProtector(ProtectorPurpose);
            _workDirectory = _options.WorkPath;
            _time = time;
            _logger = logger;
        }

        internal byte[] Protect(string configuration) => _protector.Protect(Encoding.UTF8.GetBytes(configuration));

        // What the profile's tunnel shows now; a profile without a running tunnel is Stopped.
        internal VpnTunnelV1 Read(long profileId) => _tunnels.TryGetValue(profileId, out var tunnel) ? tunnel.Snapshot() : VpnTunnelV1.Stopped;

        // The verified tunnels as the route policy reads them: alive by its pings, its exit measured and not the host's own address.
        internal IReadOnlyList<VpnProfileHealth> Health() => [.. _tunnels.Values.Select(tunnel => tunnel.Health()).OfType<VpnProfileHealth>()];

        // A lease on the profile's tunnel, started and checked first when it does not run.
        internal async Task<VpnLease> AcquireAsync(long profileId, byte[] protectedConfiguration, CancellationToken ct)
        {
            var tunnel = await EnsureAsync(profileId, protectedConfiguration, ct);
            if (tunnel.Phase != VpnTunnelPhase.Connected) throw new TunnelUnavailableException(tunnel.ErrorKey ?? "vpn_unavailable");
            return tunnel.Lease();
        }

        // Starts the tunnel when needed and measures it again now.
        internal async Task<VpnTunnelV1> CheckAsync(long profileId, byte[] protectedConfiguration, CancellationToken ct)
        {
            var tunnel = await EnsureAsync(profileId, protectedConfiguration, ct);
            if (tunnel.Phase == VpnTunnelPhase.Connected) await tunnel.VerifyAsync(true, ct);
            return tunnel.Snapshot();
        }

        internal async Task StopAsync(long profileId)
        {
            if (_tunnels.TryRemove(profileId, out var tunnel)) await tunnel.DisposeAsync();
        }

        internal async Task StopAllAsync()
        {
            foreach (var id in _tunnels.Keys) await StopAsync(id);
        }

        internal bool IsRunning => !_tunnels.IsEmpty;

        public ValueTask DisposeAsync() => new(StopAllAsync());

        private async Task<Tunnel> EnsureAsync(long profileId, byte[] protectedConfiguration, CancellationToken ct)
        {
            await _starting.WaitAsync(ct);
            try
            {
                if (_tunnels.TryGetValue(profileId, out var running) && running.Phase != VpnTunnelPhase.Failed) return running;
                if (running is not null) await StopAsync(profileId);

                var configuration = Unprotect(protectedConfiguration);
                var tunnel = new Tunnel(this, profileId);
                _tunnels[profileId] = tunnel;
                await tunnel.StartAsync(configuration, ct);
                return tunnel;
            }
            finally
            {
                _starting.Release();
            }
        }

        private string Unprotect(byte[] protectedConfiguration)
        {
            byte[] clear;
            try
            {
                clear = _protector.Unprotect(protectedConfiguration);
            }
            catch (CryptographicException)
            {
                throw new TunnelUnavailableException("vpn_unavailable");
            }

            try
            {
                return Encoding.UTF8.GetString(clear);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(clear);
            }
        }

        // The host's own public address, asked directly and kept for a few minutes.
        private async Task<PublicExit?> ReadHostExitAsync(CancellationToken ct)
        {
            var now = _time.GetUtcNow();
            if (_hostExit is { } known && now - known.At < HostExitAge) return known.Exit;
            try
            {
                using var direct = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
                var exit = await WireproxyTunnel.ReadExitAsync(direct, new Uri(_options.ExitCheckAddress), ct);
                _hostExit = (exit, now);
                return exit;
            }
            catch (Exception exception) when (exception is HttpRequestException or JsonException || exception is TaskCanceledException && !ct.IsCancellationRequested)
            {
                return null;
            }
        }

        // One profile's helper process and what was measured on it.
        internal sealed class Tunnel(VpnTunnels owner, long profileId) : IAsyncDisposable
        {
            private readonly Lock _state = new();
            private WireproxyTunnel? _process;
            private ITimer? _timer;
            private int _leases;
            private DateTimeOffset _lastUsed;
            private DateTimeOffset? _startedAt;
            private DateTimeOffset? _aliveAt;
            private TunnelCounters? _counters;
            private PublicExit? _exit;
            private DateTimeOffset? _exitCheckedAt;
            private bool _exitDiffers;
            private int _checking;

            public VpnTunnelPhase Phase { get; private set; } = VpnTunnelPhase.Connecting;
            public string? ErrorKey { get; private set; }

            public async Task StartAsync(string configuration, CancellationToken ct)
            {
                _startedAt = owner._time.GetUtcNow();
                _lastUsed = _startedAt.Value;
                try
                {
                    _process = await WireproxyTunnel.StartAsync(owner._options.WireproxyPath, owner._workDirectory, configuration,
                        string.IsNullOrWhiteSpace(owner._options.CheckAliveAddress) ? null : owner._options.CheckAliveAddress, CheckAliveSeconds, ct);
                }
                catch (TunnelUnavailableException exception)
                {
                    Fail(exception.ReasonKey);
                    return;
                }

                // Connected only once a request through it reached the outside under another address than the host's.
                await VerifyAsync(true, ct);
                _timer = owner._time.CreateTimer(_ => _ = TickAsync(), null, HealthInterval, HealthInterval);
            }

            public VpnLease Lease()
            {
                lock (_state)
                {
                    _leases++;
                    _lastUsed = owner._time.GetUtcNow();
                }

                return new VpnLease(profileId, this);
            }

            public void Release()
            {
                lock (_state)
                {
                    _leases--;
                    _lastUsed = owner._time.GetUtcNow();
                }
            }

            public async Task<Socket> ConnectAsync(string host, int port, CancellationToken ct)
            {
                var process = _process;
                if (Phase != VpnTunnelPhase.Connected || process is null) throw new TunnelUnavailableException(ErrorKey ?? "vpn_unavailable");
                _lastUsed = owner._time.GetUtcNow();
                return await process.ConnectAsync(host, port, ct);
            }

            public SocketsHttpHandler CreateHandler() => new()
            {
                UseProxy = false,
                ConnectTimeout = TimeSpan.FromSeconds(20),
                PooledConnectionLifetime = TimeSpan.FromMinutes(2),
                ConnectCallback = async (context, ct) => new NetworkStream(await ConnectAsync(context.DnsEndPoint.Host, context.DnsEndPoint.Port, ct), ownsSocket: true)
            };

            public async Task VerifyAsync(bool exitNow, CancellationToken ct)
            {
                if (Interlocked.Exchange(ref _checking, 1) == 1) return;
                try
                {
                    var process = _process;
                    if (process is null || !process.IsRunning)
                    {
                        Fail("vpn_tunnel_stopped");
                        return;
                    }

                    var now = owner._time.GetUtcNow();
                    if (await process.IsAliveAsync(ct)) _aliveAt = now;
                    _counters = await process.ReadCountersAsync(ct) ?? _counters;
                    if (!exitNow && _exitCheckedAt is { } checkedAt && now - checkedAt < ExitInterval) return;

                    PublicExit tunnelExit;
                    try
                    {
                        using var client = new HttpClient(process.CreateHandler()) { Timeout = TimeSpan.FromSeconds(20) };
                        tunnelExit = await WireproxyTunnel.ReadExitAsync(client, new Uri(owner._options.ExitCheckAddress), ct);
                    }
                    catch (Exception exception) when (exception is HttpRequestException or JsonException or IOException || exception is TaskCanceledException && !ct.IsCancellationRequested)
                    {
                        Fail("vpn_tunnel_unreachable");
                        return;
                    }

                    var host = await owner.ReadHostExitAsync(ct);
                    _exit = tunnelExit;
                    _exitCheckedAt = owner._time.GetUtcNow();
                    _exitDiffers = host is not null && host.Address != tunnelExit.Address;
                    _counters = await process.ReadCountersAsync(ct) ?? _counters;
                    if (host is not null && !_exitDiffers)
                    {
                        Fail("vpn_exit_not_hidden");
                        return;
                    }

                    ErrorKey = null;
                    Phase = VpnTunnelPhase.Connected;
                }
                finally
                {
                    Volatile.Write(ref _checking, 0);
                }
            }

            public VpnTunnelV1 Snapshot() => new(Phase, _startedAt?.UtcDateTime, _aliveAt?.UtcDateTime, _counters?.HandshakeAt?.UtcDateTime, _counters?.ReceivedBytes ?? 0,
                _counters?.SentBytes ?? 0, _exit?.Address, _exit?.CountryCode, _exitCheckedAt?.UtcDateTime, _exitDiffers, Volatile.Read(ref _leases), ErrorKey);

            public VpnProfileHealth? Health()
            {
                if (Phase != VpnTunnelPhase.Connected || _aliveAt is not { } alive || _exitCheckedAt is null) return null;

                // The kill switch is the binding itself: a lease has no path other than the tunnel's loopback door.
                return new VpnProfileHealth(profileId, 0, _exit?.CountryCode, true, true, _exitDiffers, alive);
            }

            public async ValueTask DisposeAsync()
            {
                Phase = VpnTunnelPhase.Stopped;
                if (_timer is not null) await _timer.DisposeAsync();
                if (_process is not null) await _process.DisposeAsync();
                _process = null;
            }

            private void Fail(string key)
            {
                ErrorKey = key;
                Phase = VpnTunnelPhase.Failed;
            }

            private async Task TickAsync()
            {
                try
                {
                    // Nobody used it for a while: it stops and costs nothing until it is asked for again.
                    if (Volatile.Read(ref _leases) == 0 && owner._time.GetUtcNow() - _lastUsed > IdleStop)
                    {
                        if (owner._tunnels.TryRemove(new KeyValuePair<long, Tunnel>(profileId, this))) await DisposeAsync();
                        return;
                    }

                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
                    await VerifyAsync(false, timeout.Token);
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    owner._logger.LogWarning(exception, "The VPN tunnel check of profile {ProfileId} failed.", profileId);
                }
            }
        }
    }

    // A client's hold on one tunnel. Its connections and HTTP handlers reach the network only through that tunnel.
    internal sealed class VpnLease : IDisposable
    {
        private readonly VpnTunnels.Tunnel _tunnel;
        private int _released;

        internal VpnLease(long profileId, VpnTunnels.Tunnel tunnel)
        {
            ProfileId = profileId;
            _tunnel = tunnel;
        }

        public long ProfileId { get; }

        public async Task<Stream> ConnectAsync(string host, int port, CancellationToken ct) => new NetworkStream(await _tunnel.ConnectAsync(host, port, ct), ownsSocket: true);

        public SocketsHttpHandler CreateHandler() => _tunnel.CreateHandler();

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) _tunnel.Release();
        }
    }
}
