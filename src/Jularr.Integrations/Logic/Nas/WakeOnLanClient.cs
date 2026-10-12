using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Jularr.Integrations.Data.Nas;

namespace Jularr.Integrations.Logic.Nas
{
    internal sealed class WakeOnLanClient
    {
        public async Task SendAsync(string macAddress, string? broadcastAddress, CancellationToken ct)
        {
            // Six sync bytes, then sixteen MAC repetitions.
            var mac = PhysicalAddress.Parse(macAddress.Replace(':', '-')).GetAddressBytes();
            var packet = new byte[102];
            Array.Fill(packet, (byte)0xFF, 0, 6);
            for (var index = 0; index < 16; index++)
            {
                mac.CopyTo(packet, 6 + index * mac.Length);
            }

            try
            {
                using var client = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true };
                var target = new IPEndPoint(broadcastAddress is null ? IPAddress.Broadcast : IPAddress.Parse(broadcastAddress), 9);
                await client.SendAsync(packet, target, ct);
            }
            catch (SocketException)
            {
                throw new NasMountException("storage_wake_failed");
            }
        }
    }
}
