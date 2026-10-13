using System.Net;
using System.Net.Sockets;

namespace Jularr.Integrations.Logic.Common
{
    // Links pasted by people may only reach public addresses, never the local network.
    internal static class PublicNetwork
    {
        public static SocketsHttpHandler CreateHandler() => new()
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            ConnectCallback = async (context, ct) =>
            {
                // The checked address is the one connected to.
                var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct);
                var address = addresses.FirstOrDefault(IsPublic) ?? throw new HttpRequestException("The source address is not public.");
                var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), ct);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            }
        };

        public static bool IsPublic(IPAddress address)
        {
            if (address.IsIPv4MappedToIPv6)
            {
                address = address.MapToIPv4();
            }

            if (address.AddressFamily == AddressFamily.InterNetwork)
            {
                var bytes = address.GetAddressBytes();
                return bytes[0] switch
                {
                    0 or 10 or 127 or >= 224 => false,
                    100 => bytes[1] is < 64 or > 127,
                    169 => bytes[1] != 254,
                    172 => bytes[1] is < 16 or > 31,
                    192 => !(bytes[1] == 168 || bytes[1] == 0 && bytes[2] is 0 or 2),
                    198 => bytes[1] is not (18 or 19),
                    _ => true
                };
            }

            return address.AddressFamily == AddressFamily.InterNetworkV6 && !IPAddress.IsLoopback(address) && !address.Equals(IPAddress.IPv6None) && !address.IsIPv6LinkLocal
                && !address.IsIPv6SiteLocal && !address.IsIPv6Multicast && !address.IsIPv6UniqueLocal;
        }
    }
}
