using System.Net;
using System.Net.Sockets;

namespace Jularr.Backend.Logic.Network
{
    internal sealed record WireGuardProfilePreview(string EndpointHost, int EndpointPort, string[] TunnelAddresses, string[] DnsServers,
        bool HasIpv6DefaultRoute, bool HasPresharedKey);

    // Import validation never executes wg-quick configuration directives or returns key material.
    internal static class WireGuardProfileLogic
    {
        private const int MaximumConfigurationLength = 16 * 1024;
        private static readonly HashSet<string> InterfaceKeys = new(StringComparer.OrdinalIgnoreCase) { "PrivateKey", "Address", "DNS", "MTU" };
        private static readonly HashSet<string> PeerKeys = new(StringComparer.OrdinalIgnoreCase) { "PublicKey", "PresharedKey", "Endpoint", "AllowedIPs", "PersistentKeepalive" };

        public static WireGuardProfilePreview Preview(string configuration)
        {
            if (string.IsNullOrWhiteSpace(configuration) || configuration.Length > MaximumConfigurationLength)
            {
                throw Invalid();
            }

            var network = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var peer = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, string>? section = null;
            var interfaceCount = 0;
            var peerCount = 0;

            foreach (var rawLine in configuration.Split('\n'))
            {
                var line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith('#') || line.StartsWith(';'))
                {
                    continue;
                }

                if (line.StartsWith('['))
                {
                    if (line.Equals("[Interface]", StringComparison.OrdinalIgnoreCase) && ++interfaceCount == 1)
                    {
                        section = network;
                    }
                    else if (line.Equals("[Peer]", StringComparison.OrdinalIgnoreCase) && ++peerCount == 1)
                    {
                        section = peer;
                    }
                    else
                    {
                        throw Invalid();
                    }

                    continue;
                }

                var separator = line.IndexOf('=');
                if (section is null || separator <= 0 || separator == line.Length - 1)
                {
                    throw Invalid();
                }

                var key = line[..separator].Trim();
                var value = line[(separator + 1)..].Trim();
                var supported = ReferenceEquals(section, network) ? InterfaceKeys : PeerKeys;
                if (!supported.Contains(key) || value.Length == 0 || !section.TryAdd(key, value))
                {
                    // In particular, PreUp/PostUp/PreDown/PostDown and unknown options are never accepted.
                    throw Invalid();
                }
            }

            if (interfaceCount != 1 || peerCount != 1)
            {
                throw Invalid();
            }

            VerifyKey(Required(network, "PrivateKey"));
            VerifyKey(Required(peer, "PublicKey"));
            if (peer.TryGetValue("PresharedKey", out var sharedKey))
            {
                VerifyKey(sharedKey);
            }

            if (network.TryGetValue("MTU", out var mtu) && (!int.TryParse(mtu, out var size) || size < 576 || size > 9000))
            {
                throw Invalid();
            }

            if (peer.TryGetValue("PersistentKeepalive", out var keepalive)
                && (!int.TryParse(keepalive, out var seconds) || seconds < 0 || seconds > 65535))
            {
                throw Invalid();
            }

            var addresses = Split(Required(network, "Address"));
            if (addresses.Length == 0 || addresses.Length > 8 || addresses.Any(address => !ValidCidr(address)))
            {
                throw Invalid();
            }

            var dns = network.TryGetValue("DNS", out var servers) ? Split(servers) : [];
            if (dns.Length > 8 || dns.Any(server => !IPAddress.TryParse(server, out _)))
            {
                throw Invalid();
            }

            var allowed = Split(Required(peer, "AllowedIPs"));
            if (!allowed.Contains("0.0.0.0/0", StringComparer.Ordinal)
                || allowed.Length > 2
                || allowed.Any(route => route is not ("0.0.0.0/0" or "::/0")))
            {
                // Split-tunnel and non-default routes cannot satisfy the VPN-only guarantee.
                throw Invalid();
            }

            var endpoint = Required(peer, "Endpoint");
            var portSeparator = endpoint.LastIndexOf(':');
            if (portSeparator <= 0 || !int.TryParse(endpoint[(portSeparator + 1)..], out var port) || port is < 1 or > 65535)
            {
                throw Invalid();
            }

            var host = endpoint[..portSeparator];
            if (host.StartsWith('[') && host.EndsWith(']'))
            {
                host = host[1..^1];
                if (!IPAddress.TryParse(host, out var ip) || ip.AddressFamily != AddressFamily.InterNetworkV6)
                {
                    throw Invalid();
                }
            }
            else if (host.Contains(':') || Uri.CheckHostName(host) == UriHostNameType.Unknown)
            {
                throw Invalid();
            }

            return new WireGuardProfilePreview(host, port, addresses, dns, allowed.Contains("::/0", StringComparer.Ordinal), sharedKey is not null);
        }

        private static string Required(IReadOnlyDictionary<string, string> section, string key) =>
            section.TryGetValue(key, out var value) ? value : throw Invalid();

        private static string[] Split(string values) => values.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        private static bool ValidCidr(string value)
        {
            var separator = value.LastIndexOf('/');
            if (separator <= 0 || !IPAddress.TryParse(value[..separator], out var address) || !int.TryParse(value[(separator + 1)..], out var prefix))
            {
                return false;
            }

            return address.AddressFamily switch
            {
                AddressFamily.InterNetwork => prefix is >= 1 and <= 32,
                AddressFamily.InterNetworkV6 => prefix is >= 1 and <= 128,
                _ => false
            };
        }

        private static void VerifyKey(string value)
        {
            Span<byte> decoded = stackalloc byte[64];
            var valid = Convert.TryFromBase64String(value, decoded, out var count) && count == 32;
            decoded.Clear();
            if (!valid)
            {
                throw Invalid();
            }
        }

        private static ArgumentException Invalid() => new("Unsupported or invalid WireGuard client configuration.");
    }
}
