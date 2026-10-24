using System.Net;
using System.Net.Sockets;
using System.Text;
using Jularr.Integrations.Logic.Common;

namespace Jularr.Integrations.Logic.Browser
{
    // Browser subprocesses may only open HTTPS tunnels to publicly resolved endpoints.
    // A fresh public-only DNS resolution is used for the actual socket to prevent rebinding
    // between page validation and connection. The proxy binds only to loopback.
    internal sealed class BrowserEgressProxy : IDisposable
    {
        private const int MaximumHeaderBytes = 8192;
        private const int MaximumClients = 64;
        private static readonly byte[] Granted = "HTTP/1.1 200 Connection Established\r\n\r\n"u8.ToArray();
        private static readonly byte[] Denied = "HTTP/1.1 403 Forbidden\r\nConnection: close\r\nContent-Length: 0\r\n\r\n"u8.ToArray();

        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly SemaphoreSlim _slots = new(MaximumClients, MaximumClients);
        private readonly object _gate = new();
        private readonly HashSet<Task> _clients = [];
        private Task? _accept;
        private bool _disposed;

        public int Port { get; private set; }

        public void Start()
        {
            _listener.Start(MaximumClients);
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _accept = AcceptAsync();
        }

        private async Task AcceptAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient incoming;
                try
                {
                    incoming = await _listener.AcceptTcpClientAsync(_stop.Token);
                }
                catch (Exception exception) when (exception is OperationCanceledException or SocketException or ObjectDisposedException)
                {
                    break;
                }

                if (!await _slots.WaitAsync(0, _stop.Token))
                {
                    incoming.Dispose();
                    continue;
                }

                var task = HandleAsync(incoming);
                lock (_gate) _clients.Add(task);
                _ = task.ContinueWith(done =>
                {
                    lock (_gate) _clients.Remove(done);
                    _slots.Release();
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }

        private async Task HandleAsync(TcpClient incoming)
        {
            using (incoming)
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token))
            {
                deadline.CancelAfter(TimeSpan.FromSeconds(30));
                try
                {
                    var stream = incoming.GetStream();
                    var header = await ReadHeaderAsync(stream, deadline.Token);
                    if (!TryConnectAddress(header, out var hostname))
                    {
                        await stream.WriteAsync(Denied, deadline.Token);
                        return;
                    }

                    var addresses = await Dns.GetHostAddressesAsync(hostname, deadline.Token);
                    // Never resolve a public hostname to a private address, even if another
                    // address in the response is public. Do not re-resolve after validation.
                    if (addresses.Length == 0 || addresses.Any(address => !PublicNetwork.IsPublic(address)))
                    {
                        await stream.WriteAsync(Denied, deadline.Token);
                        return;
                    }

                    var address = addresses[0];
                    using var upstream = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                    await upstream.ConnectAsync(new IPEndPoint(address, 443), deadline.Token);
                    await stream.WriteAsync(Granted, deadline.Token);
                    using var upstreamStream = new NetworkStream(upstream, ownsSocket: false);
                    deadline.CancelAfter(TimeSpan.FromMinutes(10));
                    var send = stream.CopyToAsync(upstreamStream, deadline.Token);
                    var receive = upstreamStream.CopyToAsync(stream, deadline.Token);
                    await Task.WhenAny(send, receive);
                    await deadline.CancelAsync();
                    try { await Task.WhenAll(send, receive); }
                    catch (OperationCanceledException) { }
                }
                catch (Exception exception) when (exception is IOException or SocketException or OperationCanceledException
                    or ObjectDisposedException or InvalidDataException)
                {
                    // Failed closed: never fall back to a direct browser socket.
                }
            }
        }

        private static async Task<string> ReadHeaderAsync(NetworkStream stream, CancellationToken ct)
        {
            var bytes = new byte[MaximumHeaderBytes];
            var length = 0;
            while (length < bytes.Length)
            {
                var read = await stream.ReadAsync(bytes.AsMemory(length, 1), ct);
                if (read == 0) break;
                length += read;
                if (length >= 4 && bytes[length - 4] == '\r' && bytes[length - 3] == '\n'
                    && bytes[length - 2] == '\r' && bytes[length - 1] == '\n')
                    return Encoding.ASCII.GetString(bytes, 0, length);
            }

            throw new InvalidDataException("Invalid or oversized browser proxy request.");
        }

        internal static bool TryConnectAddress(string header, out string hostname)
        {
            hostname = "";
            var first = header.Split("\r\n", 2, StringSplitOptions.None)[0];
            var parts = first.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 3 || parts[0] != "CONNECT" || parts[2] != "HTTP/1.1"
                || !parts[1].EndsWith(":443", StringComparison.Ordinal)
                || !Uri.TryCreate("https://" + parts[1], UriKind.Absolute, out var target)
                || !string.IsNullOrEmpty(target.UserInfo) || target.Port != 443)
                return false;

            hostname = target.IdnHost;
            return hostname.Length is > 0 and <= 253;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _stop.Cancel();
            _listener.Stop();
            // The active streams observe cancellation. Their tasks own their sockets.
        }
    }
}
