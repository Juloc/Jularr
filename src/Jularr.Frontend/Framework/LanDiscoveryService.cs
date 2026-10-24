using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Jularr.Frontend.Framework
{
    /// <summary>Optional LAN-only UDP responder for the native TV's first-run server picker.</summary>
    public sealed class LanDiscoveryService(IConfiguration configuration, ILogger<LanDiscoveryService> logger) : BackgroundService
    {
        private const int DiscoveryPort = 37812;
        private static readonly byte[] Probe = "JULARR_DISCOVER_V1"u8.ToArray();

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (!configuration.GetValue("DeviceDiscovery:Enabled", false))
            {
                return;
            }

            var webPort = configuration.GetValue("DeviceDiscovery:WebPort", 0);
            if (webPort is < 1 or > 65535)
            {
                logger.LogWarning("LAN discovery disabled: configure DeviceDiscovery:WebPort with the HTTP(S) port reachable from the TV");
                return;
            }

            var name = configuration["DeviceDiscovery:Name"]?.Trim() ?? "Jularr";
            if (name.Length is < 1 or > 64)
            {
                name = "Jularr";
            }

            var reply = JsonSerializer.SerializeToUtf8Bytes(new
            {
                service = "jularr",
                port = webPort,
                https = configuration.GetValue("DeviceDiscovery:Https", false),
                name,
                version = "1"
            });

            try
            {
                using var server = new UdpClient(new IPEndPoint(IPAddress.Any, DiscoveryPort));
                logger.LogInformation("LAN discovery listening on UDP {Port}", DiscoveryPort);
                var lastReply = new Dictionary<IPAddress, DateTimeOffset>();

                while (!stoppingToken.IsCancellationRequested)
                {
                    UdpReceiveResult received;
                    try
                    {
                        received = await server.ReceiveAsync(stoppingToken);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        break;
                    }

                    if (!IsPrivateLanAddress(received.RemoteEndPoint.Address) || !received.Buffer.AsSpan().SequenceEqual(Probe))
                    {
                        continue;
                    }

                    var now = DateTimeOffset.UtcNow;
                    if (lastReply.TryGetValue(received.RemoteEndPoint.Address, out var prior) && now - prior < TimeSpan.FromSeconds(1))
                    {
                        continue;
                    }

                    if (lastReply.Count > 256)
                    {
                        lastReply.Clear();
                    }

                    lastReply[received.RemoteEndPoint.Address] = now;
                    await server.SendAsync(reply.AsMemory(), received.RemoteEndPoint, stoppingToken);
                }
            }
            catch (SocketException exception)
            {
                logger.LogWarning(exception, "LAN discovery is unavailable; the TV can still connect by manual URL");
            }
        }

        public static bool IsPrivateLanAddress(IPAddress address)
        {
            if (address.AddressFamily != AddressFamily.InterNetwork)
            {
                return false;
            }

            var octets = address.GetAddressBytes();
            return octets[0] == 10
                || octets[0] == 172 && octets[1] is >= 16 and <= 31
                || octets[0] == 192 && octets[1] == 168
                || octets[0] == 169 && octets[1] == 254
                || octets[0] == 127;
        }
    }
}
