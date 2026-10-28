using Jularr.Backend.Data.Admin.Acquisition.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Service.Core;
using Jularr.Integrations.Logic.Torrent;

namespace Jularr.Backend.Service.Admin.Acquisition.V1
{
    [ServiceEndpoint("GET", "/api/v1/admin/acquisition/torrent/engine")]
    public sealed class ReadTorrentEngineV1 : AdminService<ReadTorrentEngineParametersV1>
    {
        private readonly VpnTunnels _tunnels;
        private readonly TimeProvider _time;

        internal ReadTorrentEngineV1(VpnTunnels tunnels, TimeProvider time)
        {
            _tunnels = tunnels;
            _time = time;
        }

        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<TorrentEngineV1>()];
        protected override ModuleRequirement GetInstanceModules(ReadTorrentEngineParametersV1 parameters) => ModuleRequirement.All("Acquisition", "Torrent");
        protected override ServicePermission GetPermission(ReadTorrentEngineParametersV1 parameters, Type resultType) => new("acquisition.manage");

        protected override async Task<IServiceOutput> OnRead()
        {
            // The lock is proven again on every read: forbidden frames go to the writer and must leave nothing.
            var verified = await TorrentOutboundWire.VerifyLockAsync(CancellationToken);
            return new TorrentEngineV1(verified, _time.GetUtcNow().UtcDateTime, TorrentDownloader.ReceivedBytes, TorrentOutboundWire.SentBytes, TorrentOutboundWire.SentPieceBytes,
                TorrentOutboundWire.CheckedFrames, TorrentOutboundWire.BlockedFrames, TorrentDownloader.ConnectedPeers, _tunnels.Health().Count > 0);
        }
    }
}
