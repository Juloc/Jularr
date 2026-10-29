using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Fields.Network;

namespace Jularr.Backend.Data.Logic.Network
{
    internal sealed class VpnProfileSaveData(string name, byte[] protectedConfiguration, string endpointHost, int endpointPort, string[] tunnelAddresses,
        string[] dnsServers, bool hasIpv6Route) : BaseData
    {
        [DataField(typeof(VpnProfileFields), nameof(VpnProfileFields.Name))]
        public string Name { get; } = name;

        [DataField(typeof(VpnProfileFields), nameof(VpnProfileFields.ProtectedConfiguration))]
        public byte[] ProtectedConfiguration { get; } = protectedConfiguration;

        [DataField(typeof(VpnProfileFields), nameof(VpnProfileFields.EndpointHost))]
        public string EndpointHost { get; } = endpointHost;

        [DataField(typeof(VpnProfileFields), nameof(VpnProfileFields.EndpointPort))]
        public int EndpointPort { get; } = endpointPort;

        [DataField(typeof(VpnProfileFields), nameof(VpnProfileFields.Addresses))]
        public string[] TunnelAddresses { get; } = tunnelAddresses;

        [DataField(typeof(VpnProfileFields), nameof(VpnProfileFields.Addresses))]
        public string[] DnsServers { get; } = dnsServers;

        [DataField(typeof(VpnProfileFields), nameof(VpnProfileFields.HasIpv6Route))]
        public bool HasIpv6Route { get; } = hasIpv6Route;
    }

    internal sealed class VpnProfileKeyData(long id, long version) : BaseData
    {
        [DataField(typeof(VpnProfileFields), nameof(VpnProfileFields.Id))]
        public long Id { get; } = id;

        [DataField(typeof(VpnProfileFields), nameof(VpnProfileFields.Version))]
        public long Version { get; } = version;
    }

    internal sealed class VpnProfileIdData(long id) : BaseData
    {
        [DataField(typeof(VpnProfileFields), nameof(VpnProfileFields.Id))]
        public long Id { get; } = id;
    }

    // The protected configuration of one profile, read only to start its tunnel.
    internal sealed class VpnProfileSecretData(long id, byte[] protectedConfiguration) : BaseData
    {
        public long Id { get; } = id;
        public byte[] ProtectedConfiguration { get; } = protectedConfiguration;
    }
}
