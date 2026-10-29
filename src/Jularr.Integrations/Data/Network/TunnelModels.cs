namespace Jularr.Integrations.Data.Network
{
    // What wireproxy's device reports about its one peer.
    internal sealed class TunnelCounters(DateTimeOffset? handshakeAt, long receivedBytes, long sentBytes)
    {
        public DateTimeOffset? HandshakeAt { get; } = handshakeAt;
        public long ReceivedBytes { get; } = receivedBytes;
        public long SentBytes { get; } = sentBytes;
    }

    // The public address and country an outside service saw.
    internal sealed class PublicExit(string address, string? countryCode)
    {
        public string Address { get; } = address;
        public string? CountryCode { get; } = countryCode;
    }

    // The tunnel is down or never came up: a bound client gets no connection rather than a direct one.
    internal sealed class TunnelUnavailableException(string reasonKey) : IOException("The VPN tunnel is not available: " + reasonKey)
    {
        public string ReasonKey { get; } = reasonKey;
    }
}
