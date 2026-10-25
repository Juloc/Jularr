using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Field;

namespace Jularr.Backend.Data.Admin.Network.V1
{
    public static class WireGuardPreviewFields
    {
        public static readonly StringField Configuration = new() { MaximumLength = 16384, AllowBlank = false };
    }

    public sealed class WireGuardPreviewParametersV1 : BaseParameters
    {
    }

    public sealed class PreviewWireGuardDataV1(string configuration) : BaseData
    {
        [DataField(typeof(WireGuardPreviewFields), nameof(WireGuardPreviewFields.Configuration))]
        public string Configuration { get; } = configuration;
    }

    // Neither key material nor a "VPN connected" claim may be returned by the preview API.
    public sealed class WireGuardPreviewV1(string endpointHost, int endpointPort, string[] tunnelAddresses, string[] dnsServers,
        bool hasIpv6DefaultRoute, bool hasPresharedKey) : BaseResponse
    {
        public string EndpointHost { get; } = endpointHost;
        public int EndpointPort { get; } = endpointPort;
        public string[] TunnelAddresses { get; } = tunnelAddresses;
        public string[] DnsServers { get; } = dnsServers;
        public bool HasIpv6DefaultRoute { get; } = hasIpv6DefaultRoute;
        public bool HasPresharedKey { get; } = hasPresharedKey;
        public bool IsConnected => false;
    }
}
