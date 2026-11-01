using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Network;
using Jularr.Backend.Service.Core;
using Jularr.Integrations.Data.Network;
using Jularr.Integrations.Data.Torrent;
using Jularr.Integrations.Logic.Torrent;

namespace Jularr.Backend.Service.Admin.Wanted.V1
{
    // Jularr's own torrent downloads: the .torrent or magnet, the verified pieces and the data live in staging. Every pass runs through a verified
    // VPN tunnel chosen by the route policy; without one nothing is sent and the job waits.
    internal sealed class TorrentJob(VpnTunnels tunnels, TimeProvider time, DownloadSpeeds speeds)
    {
        public const string ExternalId = "torrent";
        private static readonly TimeSpan TrackerTimeout = TimeSpan.FromSeconds(20);

        // The first profile whose tunnel the route policy accepts for torrents (alive, exit measured and not the host's); a tunnel that is not
        // verified is never used.
        public async Task<VpnLease> AcquireRouteAsync(LogicContext context, CancellationToken ct)
        {
            var profiles = await context.ExecuteBatchAsync((ctx, token) => ctx.ReadVpnProfileSecretsAsync(token), ct);
            foreach (var profile in profiles)
            {
                VpnLease lease;
                try
                {
                    // A tunnel that was just started has no measured exit yet, and the route policy refuses an unmeasured tunnel: measure it now, once, or the job
                    // would rest for minutes and the tunnel would stop again without a lease.
                    if (!tunnels.Health().Any(health => health.ProfileId == profile.Id))
                        await tunnels.CheckAsync(profile.Id, profile.ProtectedConfiguration, ct);
                    lease = await tunnels.AcquireAsync(profile.Id, profile.ProtectedConfiguration, ct);
                }
                catch (TunnelUnavailableException)
                {
                    continue;
                }

                var decision = NetworkRouteLogic.Resolve(NetworkActivityKind.TorrentDownload, null, [new NetworkRouteRule(NetworkActivityKind.TorrentDownload, null,
                    NetworkRouteMode.VpnRequired, null, [profile.Id])], tunnels.Health(), time.GetUtcNow());
                if (decision.ProfileId == profile.Id) return lease;
                lease.Dispose();
            }

            throw new TunnelUnavailableException("vpn_unavailable");
        }

        public async Task<TorrentPassResult> RunAsync(LogicContext context, string staging, DateTimeOffset deadline, CancellationToken ct)
        {
            using var lease = await AcquireRouteAsync(context, ct);
            using var trackers = new HttpClient(lease.CreateHandler()) { Timeout = TrackerTimeout };
            return await TorrentDownloader.RunAsync(new TorrentPassRequest(staging, lease.ConnectAsync, trackers, deadline, throttle: speeds.Torrent.WaitAsync), ct);
        }
    }
}
