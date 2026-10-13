using System.Net;
using System.Net.Http;
using System.Net.Sockets;

namespace Jularr.Integrations.Logic.Common
{
    // Prevent DNS rebinding when downloading from public sources: validate every resolved address and connect to a pinned IP.
    internal static class PublicNetworkConnection
    {
        public static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken ct)
        {
            var endpoint = context.DnsEndPoint;
            if (!DirectDownload.IsPublicFileUri(new UriBuilder(Uri.UriSchemeHttps, endpoint.Host).Uri))
            {
                throw new HttpRequestException("The remote hostname is not a public download destination.");
            }

            var addresses = await Dns.GetHostAddressesAsync(endpoint.Host, ct);
            if (addresses.Length == 0 || addresses.Any(address => !DirectDownload.IsPublicAddress(address)))
            {
                throw new HttpRequestException("The remote hostname resolved to a non-public address.");
            }

            foreach (var address in addresses)
            {
                var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    await socket.ConnectAsync(new IPEndPoint(address, endpoint.Port), ct);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch (SocketException)
                {
                    socket.Dispose();
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            }

            throw new HttpRequestException("No public address of the download host was reachable.");
        }
    }
}
