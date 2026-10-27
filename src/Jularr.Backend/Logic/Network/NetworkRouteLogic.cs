namespace Jularr.Backend.Logic.Network
{
    internal enum NetworkActivityKind
    {
        TorrentDownload,
        DirectDownload,
        Search,
        Browser,
        Metadata
    }

    internal enum NetworkRouteMode
    {
        Direct,
        VpnRequired
    }

    internal enum NetworkFailureKind
    {
        TunnelUnavailable,
        TransportFailure,
        HttpForbidden,
        RateLimited,
        LoginRequired,
        ChallengeRequired
    }

    internal sealed record NetworkRouteRule(NetworkActivityKind Activity, string? SourceKey, NetworkRouteMode Mode, string? RequiredCountryCode,
        long[] AllowedProfileIds);

    // The country is measured at the VPN exit, never guessed from the profile label or WireGuard endpoint.
    internal sealed record VpnProfileHealth(long ProfileId, int Priority, string? VerifiedCountryCode, bool TunnelVerified, bool KillSwitchVerified,
        bool ExitVerified, DateTimeOffset VerifiedAt);

    internal sealed record NetworkRouteDecision(NetworkRouteMode Mode, long? ProfileId, string Reason);

    // Policy only: it must not be treated as enforcing Docker routing until a verified gateway applies it.
    internal static class NetworkRouteLogic
    {
        private static readonly TimeSpan MaximumHealthAge = TimeSpan.FromSeconds(90);

        public static NetworkRouteDecision Resolve(NetworkActivityKind activity, string? sourceKey, IReadOnlyList<NetworkRouteRule> rules,
            IReadOnlyList<VpnProfileHealth> gateways, DateTimeOffset now, IReadOnlySet<long>? excludedProfiles = null)
        {
            ArgumentNullException.ThrowIfNull(rules);
            ArgumentNullException.ThrowIfNull(gateways);

            var sourceRule = rules.FirstOrDefault(rule => rule.Activity == activity && rule.SourceKey is not null
                && string.Equals(rule.SourceKey, sourceKey, StringComparison.OrdinalIgnoreCase));
            var rule = sourceRule ?? rules.FirstOrDefault(candidate => candidate.Activity == activity && candidate.SourceKey is null);
            var mode = rule?.Mode ?? (activity == NetworkActivityKind.TorrentDownload ? NetworkRouteMode.VpnRequired : NetworkRouteMode.Direct);

            if (activity == NetworkActivityKind.TorrentDownload && mode == NetworkRouteMode.Direct)
            {
                // A separate authenticated administrative override is required before unprotected torrents can ever be supported.
                return new NetworkRouteDecision(NetworkRouteMode.VpnRequired, null, "torrent_direct_not_authorized");
            }

            if (mode == NetworkRouteMode.Direct)
            {
                return new NetworkRouteDecision(NetworkRouteMode.Direct, null, "direct");
            }

            var candidates = gateways.Where(gateway => gateway.ProfileId > 0
                    && gateway.TunnelVerified && gateway.KillSwitchVerified && gateway.ExitVerified
                    && gateway.VerifiedAt <= now && now - gateway.VerifiedAt <= MaximumHealthAge
                    && (rule is null || rule.AllowedProfileIds.Length == 0 || rule.AllowedProfileIds.Contains(gateway.ProfileId))
                    && (rule?.RequiredCountryCode is null
                        || string.Equals(rule.RequiredCountryCode, gateway.VerifiedCountryCode, StringComparison.OrdinalIgnoreCase))
                    && (excludedProfiles is null || !excludedProfiles.Contains(gateway.ProfileId)))
                .OrderBy(gateway => gateway.Priority)
                .ThenBy(gateway => gateway.ProfileId)
                .FirstOrDefault();

            return candidates is null
                ? new NetworkRouteDecision(NetworkRouteMode.VpnRequired, null, "vpn_unavailable")
                : new NetworkRouteDecision(NetworkRouteMode.VpnRequired, candidates.ProfileId, "vpn_ready");
        }

        // HTTP restrictions or rate limits are source conditions, not VPN failures. Never rotate IPs to bypass them.
        public static bool MayFailOver(NetworkFailureKind failure) =>
            failure is NetworkFailureKind.TunnelUnavailable or NetworkFailureKind.TransportFailure;
    }
}
