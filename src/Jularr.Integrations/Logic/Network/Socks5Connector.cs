using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Jularr.Integrations.Logic.Network
{
    // A SOCKS5 CONNECT (RFC 1928, RFC 1929 sign-in) through a local proxy. Host names travel to the proxy, so they are resolved behind the tunnel.
    internal static class Socks5Connector
    {
        public static async Task<Socket> ConnectAsync(IPEndPoint proxy, string userName, string password, string host, int port, CancellationToken ct)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(port, 1);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(port, 65535);
            var socket = new Socket(proxy.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(proxy, ct);

                // Only the user name and password method is offered.
                await SendAsync(socket, [5, 1, 2], ct);
                var choice = await ReceiveAsync(socket, 2, ct);
                if (choice[0] != 5 || choice[1] != 2) throw new IOException("The SOCKS proxy refused the sign-in method.");

                var user = Encoding.UTF8.GetBytes(userName);
                var secret = Encoding.UTF8.GetBytes(password);
                if (user.Length is 0 or > 255 || secret.Length is 0 or > 255) throw new ArgumentException("Invalid SOCKS credentials.");
                await SendAsync(socket, [1, (byte)user.Length, .. user, (byte)secret.Length, .. secret], ct);
                var signedIn = await ReceiveAsync(socket, 2, ct);
                if (signedIn[1] != 0) throw new IOException("The SOCKS proxy refused the sign-in.");

                // The target as an address or as a name for the proxy to resolve.
                byte[] target;
                if (IPAddress.TryParse(host, out var address))
                {
                    target = address.AddressFamily == AddressFamily.InterNetworkV6 ? [4, .. address.GetAddressBytes()] : [1, .. address.GetAddressBytes()];
                }
                else
                {
                    var name = Encoding.ASCII.GetBytes(new IdnMapping().GetAscii(host));
                    if (name.Length is 0 or > 255) throw new ArgumentException("Invalid host name.");
                    target = [3, (byte)name.Length, .. name];
                }

                var portBytes = new byte[2];
                BinaryPrimitives.WriteUInt16BigEndian(portBytes, (ushort)port);
                await SendAsync(socket, [5, 1, 0, .. target, .. portBytes], ct);

                var reply = await ReceiveAsync(socket, 4, ct);
                if (reply[0] != 5 || reply[1] != 0) throw new IOException("The SOCKS proxy could not reach the target (reply " + reply[1] + ").");
                var bound = reply[3] switch
                {
                    1 => 4,
                    4 => 16,
                    3 => (await ReceiveAsync(socket, 1, ct))[0],
                    _ => throw new IOException("The SOCKS proxy answered with an unknown address type.")
                };
                await ReceiveAsync(socket, bound + 2, ct);
                return socket;
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        private static async Task SendAsync(Socket socket, byte[] data, CancellationToken ct)
        {
            var sent = 0;
            while (sent < data.Length) sent += await socket.SendAsync(data.AsMemory(sent), SocketFlags.None, ct);
        }

        private static async Task<byte[]> ReceiveAsync(Socket socket, int count, CancellationToken ct)
        {
            var buffer = new byte[count];
            var read = 0;
            while (read < count)
            {
                var received = await socket.ReceiveAsync(buffer.AsMemory(read), SocketFlags.None, ct);
                if (received == 0) throw new IOException("The SOCKS proxy closed the connection.");
                read += received;
            }

            return buffer;
        }
    }
}
