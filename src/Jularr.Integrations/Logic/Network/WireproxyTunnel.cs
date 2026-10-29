using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Jularr.Integrations.Data.Network;

namespace Jularr.Integrations.Logic.Network
{
    // One userspace WireGuard tunnel (wireproxy) inside the Jularr process's container: no network interface, no capability, no route of the host changes.
    // Its only door is a SOCKS5 listener on loopback with a one-time sign-in; a client bound to it reaches the network through the tunnel or not at all.
    internal sealed class WireproxyTunnel : IAsyncDisposable
    {
        private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(10);
        private readonly Process _process;
        private readonly IPEndPoint _socks;
        private readonly string _userName;
        private readonly string _password;
        private readonly HttpClient _local;

        private WireproxyTunnel(Process process, IPEndPoint socks, IPEndPoint info, string userName, string password)
        {
            _process = process;
            _socks = socks;
            _userName = userName;
            _password = password;
            _local = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { BaseAddress = new Uri("http://" + info), Timeout = TimeSpan.FromSeconds(5) };
        }

        public bool IsRunning => !_process.HasExited;

        // The validated client configuration plus the tunnel's own pings and its loopback door; nothing that dials around the tunnel.
        public static string BuildConfiguration(string wireGuard, IPEndPoint socks, string userName, string password, string? checkAlive, int checkAliveSeconds)
        {
            var text = new StringBuilder();
            foreach (var raw in wireGuard.Split('\n'))
            {
                var line = raw.TrimEnd('\r');
                text.Append(line).Append('\n');
                if (checkAlive is not null && line.Trim().Equals("[Interface]", StringComparison.OrdinalIgnoreCase))
                {
                    text.Append("CheckAlive = ").Append(checkAlive).Append('\n');
                    text.Append("CheckAliveInterval = ").Append(checkAliveSeconds.ToString(CultureInfo.InvariantCulture)).Append('\n');
                }
            }

            text.Append("\n[Socks5]\nBindAddress = ").Append(socks).Append('\n');
            text.Append("Username = ").Append(userName).Append('\n');
            text.Append("Password = ").Append(password).Append('\n');
            return text.ToString();
        }

        public static async Task<WireproxyTunnel> StartAsync(string executable, string workDirectory, string wireGuard, string? checkAlive, int checkAliveSeconds, CancellationToken ct)
        {
            var socks = new IPEndPoint(IPAddress.Loopback, FreePort());
            var info = new IPEndPoint(IPAddress.Loopback, FreePort());
            var userName = Convert.ToHexString(RandomNumberGenerator.GetBytes(12));
            var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
            Directory.CreateDirectory(workDirectory);
            var path = Path.Combine(workDirectory, Convert.ToHexString(RandomNumberGenerator.GetBytes(8)) + ".conf");

            // The key material lies on disk only until the helper has read it, readable by the app user alone.
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using (var file = new FileStream(path, options))
            {
                await file.WriteAsync(Encoding.UTF8.GetBytes(BuildConfiguration(wireGuard, socks, userName, password, checkAlive, checkAliveSeconds)), ct);
            }

            Process process;
            try
            {
                var start = new ProcessStartInfo(executable)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                start.ArgumentList.Add("-s");
                start.ArgumentList.Add("-c");
                start.ArgumentList.Add(path);
                start.ArgumentList.Add("-i");
                start.ArgumentList.Add(info.ToString());

                // Its messages may quote the configuration; they are read and dropped.
                process = Process.Start(start) ?? throw new TunnelUnavailableException("vpn_helper_missing");
                process.OutputDataReceived += (_, _) => { };
                process.ErrorDataReceived += (_, _) => { };
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
            }
            catch (Exception exception) when (exception is Win32Exception or FileNotFoundException)
            {
                File.Delete(path);
                throw new TunnelUnavailableException("vpn_helper_missing");
            }

            var tunnel = new WireproxyTunnel(process, socks, info, userName, password);
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(StartTimeout);
                while (!await tunnel.ListensAsync(timeout.Token))
                {
                    if (process.HasExited) throw new TunnelUnavailableException("vpn_tunnel_unreachable");
                    await Task.Delay(100, timeout.Token);
                }

                return tunnel;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                await tunnel.DisposeAsync();
                throw new TunnelUnavailableException("vpn_tunnel_unreachable");
            }
            catch
            {
                await tunnel.DisposeAsync();
                throw;
            }
            finally
            {
                File.Delete(path);
            }
        }

        // A connection through the tunnel, or an error: never a direct connection.
        public async Task<Socket> ConnectAsync(string host, int port, CancellationToken ct)
        {
            if (!IsRunning) throw new TunnelUnavailableException("vpn_unavailable");
            try
            {
                return await Socks5Connector.ConnectAsync(_socks, _userName, _password, host, port, ct);
            }
            catch (SocketException) when (!IsRunning)
            {
                throw new TunnelUnavailableException("vpn_unavailable");
            }
        }

        // HTTP through the tunnel: every connection is opened by ConnectAsync, so there is no proxy setting to bypass and no lookup on the host.
        public SocketsHttpHandler CreateHandler() => new()
        {
            UseProxy = false,
            ConnectTimeout = TimeSpan.FromSeconds(20),
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            ConnectCallback = async (context, ct) => new NetworkStream(await ConnectAsync(context.DnsEndPoint.Host, context.DnsEndPoint.Port, ct), ownsSocket: true)
        };

        // The helper pings through the tunnel and answers 200 while the pings come back.
        public async Task<bool> IsAliveAsync(CancellationToken ct)
        {
            if (!IsRunning) return false;
            try
            {
                using var response = await _local.GetAsync("/readyz", ct);
                return response.StatusCode == HttpStatusCode.OK;
            }
            catch (Exception exception) when (exception is HttpRequestException || exception is TaskCanceledException && !ct.IsCancellationRequested)
            {
                return false;
            }
        }

        public async Task<TunnelCounters?> ReadCountersAsync(CancellationToken ct)
        {
            if (!IsRunning) return null;
            try
            {
                return ParseCounters(await _local.GetStringAsync("/metrics", ct));
            }
            catch (Exception exception) when (exception is HttpRequestException || exception is TaskCanceledException && !ct.IsCancellationRequested)
            {
                return null;
            }
        }

        // The UAPI dump of the device: one key=value per line; the helper replaces the keys with REDACTED.
        public static TunnelCounters ParseCounters(string metrics)
        {
            DateTimeOffset? handshake = null;
            long received = 0, sent = 0;
            foreach (var line in metrics.Split('\n'))
            {
                var separator = line.IndexOf('=');
                if (separator <= 0) continue;
                var key = line[..separator];
                var value = line[(separator + 1)..].Trim();
                if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number)) continue;
                switch (key)
                {
                    case "last_handshake_time_sec" when number > 0:
                        handshake = DateTimeOffset.FromUnixTimeSeconds(number);
                        break;
                    case "rx_bytes":
                        received += number;
                        break;
                    case "tx_bytes":
                        sent += number;
                        break;
                }
            }

            return new TunnelCounters(handshake, received, sent);
        }

        // The address an outside service sees for a request: the tunnel's when the client is the tunnel's, the host's for the plain client.
        public static async Task<PublicExit> ReadExitAsync(HttpClient client, Uri address, CancellationToken ct)
        {
            using var response = await client.GetAsync(address, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            await using var body = await response.Content.ReadAsStreamAsync(ct);
            using var json = await JsonDocument.ParseAsync(body, cancellationToken: ct);
            var root = json.RootElement;
            if (!root.TryGetProperty("ip", out var ip) || ip.ValueKind != JsonValueKind.String || !IPAddress.TryParse(ip.GetString(), out var parsed))
            {
                throw new JsonException("The exit check did not name an address.");
            }

            string? country = null;
            foreach (var name in (string[])["country", "country_iso", "countryCode"])
            {
                if (root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: 2 } code)
                {
                    country = code.ToUpperInvariant();
                    break;
                }
            }

            return new PublicExit(parsed.ToString(), country);
        }

        public async ValueTask DisposeAsync()
        {
            _local.Dispose();
            try
            {
                if (!_process.HasExited) _process.Kill(entireProcessTree: true);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await _process.WaitForExitAsync(timeout.Token);
            }
            catch (Exception exception) when (exception is InvalidOperationException or OperationCanceledException or Win32Exception)
            {
                // A helper that is already gone or does not end in time is left to the container.
            }
            finally
            {
                _process.Dispose();
            }
        }

        private async Task<bool> ListensAsync(CancellationToken ct)
        {
            using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                await probe.ConnectAsync(_socks, ct);
                return true;
            }
            catch (SocketException)
            {
                return false;
            }
        }

        private static int FreePort()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
    }
}
