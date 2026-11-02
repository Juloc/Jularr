using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Fields.Network;

namespace Jularr.Backend.Data.Admin.Network.V1
{
    public sealed class ReadVpnProfilesParametersV1 : BaseParameters
    {
    }

    public sealed class ImportVpnProfileParametersV1 : BaseParameters
    {
    }

    public sealed class VpnProfileParametersV1(long vpnProfileId) : BaseParameters
    {
        [DataField(typeof(VpnProfileFields), nameof(VpnProfileFields.Id))]
        public long VpnProfileId { get; } = vpnProfileId;
    }

    public sealed class DeleteVpnProfileParametersV1(long vpnProfileId, long version) : BaseParameters
    {
        [DataField(typeof(VpnProfileFields), nameof(VpnProfileFields.Id))]
        public long VpnProfileId { get; } = vpnProfileId;

        [DataField(typeof(VpnProfileFields), nameof(VpnProfileFields.Version))]
        public long Version { get; } = version;
    }

    // A WireGuard client configuration as an uploaded wg0.conf or as pasted text.
    public sealed class ImportVpnProfileDataV1(string name, UploadedFile? file, string? configuration) : BaseData
    {
        [DataField(typeof(VpnProfileFields), nameof(VpnProfileFields.Name))]
        public string Name { get; } = name;

        [DataField(typeof(VpnProfileFields), nameof(VpnProfileFields.File), false)]
        public UploadedFile? File { get; } = file;

        [DataField(typeof(VpnProfileFields), nameof(VpnProfileFields.Configuration))]
        public string? Configuration { get; } = configuration;
    }

    public sealed class CreateVpnProfileResultV1(long id) : BaseResponse
    {
        [DataField(typeof(VpnProfileFields), nameof(VpnProfileFields.Id))]
        public long Id { get; } = id;
    }

    // Stopped, Connecting, Connected or Failed: what the running tunnel of the profile shows now, never a remembered or assumed state.
    public enum VpnTunnelPhase : byte
    {
        Stopped = 0,
        Connecting = 1,
        Connected = 2,
        Failed = 3
    }

    // A profile without its key material, with what was measured on its tunnel while it runs.
    public sealed class VpnProfileV1(long id, string name, string endpointHost, int endpointPort, string[] tunnelAddresses, string[] dnsServers, bool hasIpv6Route,
        long version, DateTime createdAt) : BaseResponse
    {
        public long Id { get; } = id;
        public string Name { get; } = name;
        public string EndpointHost { get; } = endpointHost;
        public int EndpointPort { get; } = endpointPort;
        public string[] TunnelAddresses { get; } = tunnelAddresses;
        public string[] DnsServers { get; } = dnsServers;
        public bool HasIpv6Route { get; } = hasIpv6Route;
        public long Version { get; } = version;
        public DateTime CreatedAt { get; } = createdAt;
        public VpnTunnelV1 Tunnel { get; set; } = VpnTunnelV1.Stopped;
    }

    public sealed class VpnTunnelV1(VpnTunnelPhase phase, DateTime? startedAt, DateTime? aliveAt, DateTime? handshakeAt, long receivedBytes, long sentBytes,
        string? exitAddress, string? exitCountryCode, DateTime? exitCheckedAt, bool exitDiffersFromHost, int leases, string? errorKey) : BaseResponse
    {
        public static VpnTunnelV1 Stopped { get; } = new(VpnTunnelPhase.Stopped, null, null, null, 0, 0, null, null, null, false, 0, null);

        public VpnTunnelPhase Phase { get; } = phase;
        public DateTime? StartedAt { get; } = startedAt;

        // The last answer of a ping through the tunnel.
        public DateTime? AliveAt { get; } = aliveAt;
        public DateTime? HandshakeAt { get; } = handshakeAt;
        public long ReceivedBytes { get; } = receivedBytes;
        public long SentBytes { get; } = sentBytes;

        // The public address and country an outside service saw for a request through the tunnel.
        public string? ExitAddress { get; } = exitAddress;
        public string? ExitCountryCode { get; } = exitCountryCode;
        public DateTime? ExitCheckedAt { get; } = exitCheckedAt;
        public bool ExitDiffersFromHost { get; } = exitDiffersFromHost;
        public int Leases { get; } = leases;
        public string? ErrorKey { get; } = errorKey;
    }
}
