using Jularr.Backend.Logic.Network;

namespace Jularr.Backend.Tests
{
    [TestClass]
    public sealed class VpnNetworkPolicyTests
    {
        private static readonly DateTimeOffset Now = new(2026, 10, 25, 10, 0, 0, TimeSpan.Zero);
        private static readonly string PrivateKey = Convert.ToBase64String(Enumerable.Repeat((byte)17, 32).ToArray());
        private static readonly string PublicKey = Convert.ToBase64String(Enumerable.Repeat((byte)34, 32).ToArray());

        private static string Configuration(string allowed = "0.0.0.0/0, ::/0", string extra = "") => $"""
            [Interface]
            PrivateKey = {PrivateKey}
            Address = 10.10.0.3/32, fd00::3/128
            DNS = 10.10.0.1
            MTU = 1420
            [Peer]
            PublicKey = {PublicKey}
            Endpoint = vpn.example.test:51820
            AllowedIPs = {allowed}
            PersistentKeepalive = 25
            {extra}
            """;

        [TestMethod]
        public void WireGuardImport_ValidatesFullTunnelWithoutReturningPrivateMaterial()
        {
            var result = WireGuardProfileLogic.Preview(Configuration());
            Assert.AreEqual("vpn.example.test", result.EndpointHost);
            Assert.AreEqual(51820, result.EndpointPort);
            Assert.IsTrue(result.HasIpv6DefaultRoute);
            CollectionAssert.AreEqual(new[] { "10.10.0.3/32", "fd00::3/128" }, result.TunnelAddresses);
            CollectionAssert.AreEqual(new[] { "10.10.0.1" }, result.DnsServers);
            Assert.IsFalse(result.HasPresharedKey);
            Assert.IsFalse(result.ToString()!.Contains(PrivateKey, StringComparison.Ordinal));
        }

        [TestMethod]
        [DataRow("PostUp = curl https://evil.example")]
        [DataRow("PreDown = touch /tmp/unsafe")]
        [DataRow("Table = off")]
        [DataRow("[Peer]")]
        [DataRow("AllowedIPs = 0.0.0.0/0")]
        public void WireGuardImport_RejectsUnexpectedOptionsOrDuplicatePeers(string extra)
        {
            // A second AllowedIPs occurrence is rejected as well: overrides cannot silently weaken routing.
            Assert.ThrowsExactly<ArgumentException>(() => WireGuardProfileLogic.Preview(Configuration(extra: extra)));
        }

        [TestMethod]
        [DataRow("10.0.0.0/8")]
        [DataRow("::/0")]
        [DataRow("0.0.0.0/0, 10.0.0.0/8")]
        public void WireGuardImport_RejectsSplitTunnelConfigurations(string routes)
        {
            Assert.ThrowsExactly<ArgumentException>(() => WireGuardProfileLogic.Preview(Configuration(allowed: routes)));
        }

        [TestMethod]
        public void WireGuardImport_RejectsMalformedAndExcessivelyLongInputs()
        {
            Assert.ThrowsExactly<ArgumentException>(() => WireGuardProfileLogic.Preview(Configuration().Replace(PrivateKey, "not-a-key", StringComparison.Ordinal)));
            Assert.ThrowsExactly<ArgumentException>(() => WireGuardProfileLogic.Preview(new string('x', 20_000)));
            Assert.ThrowsExactly<ArgumentException>(() => WireGuardProfileLogic.Preview(Configuration().Replace("51820", "0", StringComparison.Ordinal)));
        }

        [TestMethod]
        public void Torrent_IsFailClosedWithoutVerifiedVpn()
        {
            var choice = NetworkRouteLogic.Resolve(NetworkActivityKind.TorrentDownload, "nyaa", [], [], Now);
            Assert.AreEqual(NetworkRouteMode.VpnRequired, choice.Mode);
            Assert.IsNull(choice.ProfileId);
            Assert.AreEqual("vpn_unavailable", choice.Reason);
        }

        [TestMethod]
        public void Torrent_DirectOverrideMustBeReauthenticatedAndIsNotImplementedYet()
        {
            NetworkRouteRule[] rules = [new(NetworkActivityKind.TorrentDownload, null, NetworkRouteMode.Direct, null, [])];
            var choice = NetworkRouteLogic.Resolve(NetworkActivityKind.TorrentDownload, null, rules, [], Now);
            Assert.AreEqual(NetworkRouteMode.VpnRequired, choice.Mode);
            Assert.IsNull(choice.ProfileId);
            Assert.AreEqual("torrent_direct_not_authorized", choice.Reason);
        }

        [TestMethod]
        public void SearchAndDirectDownloads_AreDirectUnlessAssignedToVpn()
        {
            Assert.AreEqual(NetworkRouteMode.Direct, NetworkRouteLogic.Resolve(NetworkActivityKind.Search, "indexer", [], [], Now).Mode);
            Assert.AreEqual(NetworkRouteMode.Direct, NetworkRouteLogic.Resolve(NetworkActivityKind.DirectDownload, null, [], [], Now).Mode);
        }

        [TestMethod]
        public void CountryConstrainedProfiles_DoNotFallbackOutsideRequiredCountry()
        {
            NetworkRouteRule[] rules = [new(NetworkActivityKind.Search, "regional-library", NetworkRouteMode.VpnRequired, "ID", [7, 8, 9])];
            VpnProfileHealth[] profiles =
            [
                Healthy(7, "CH", 1),
                Healthy(8, "ID", 2),
                Healthy(9, "ID", 3)
            ];
            var selected = NetworkRouteLogic.Resolve(NetworkActivityKind.Search, "regional-library", rules, profiles, Now);
            Assert.AreEqual(8L, selected.ProfileId);

            var replacement = NetworkRouteLogic.Resolve(NetworkActivityKind.Search, "regional-library", rules, profiles, Now, new HashSet<long> { 8 });
            Assert.AreEqual(9L, replacement.ProfileId);

            var blocked = NetworkRouteLogic.Resolve(NetworkActivityKind.Search, "regional-library", rules, profiles, Now, new HashSet<long> { 8, 9 });
            Assert.IsNull(blocked.ProfileId);
            Assert.AreEqual(NetworkRouteMode.VpnRequired, blocked.Mode);
        }

        [TestMethod]
        public void VpnHealth_RequiresFreshVerifiedTunnelFirewallAndExit()
        {
            NetworkRouteRule[] rules = [new(NetworkActivityKind.Browser, null, NetworkRouteMode.VpnRequired, null, [])];
            VpnProfileHealth[] bad =
            [
                Healthy(1, "ID", 1) with { TunnelVerified = false },
                Healthy(2, "ID", 2) with { KillSwitchVerified = false },
                Healthy(3, "ID", 3) with { ExitVerified = false },
                Healthy(4, "ID", 4) with { VerifiedAt = Now.AddMinutes(-2) },
                Healthy(5, "ID", 5) with { VerifiedAt = Now.AddMinutes(1) }
            ];
            Assert.IsNull(NetworkRouteLogic.Resolve(NetworkActivityKind.Browser, null, rules, bad, Now).ProfileId);
        }

        [TestMethod]
        public void SourceSpecificRule_TakesPrecedenceOverGeneralPolicy()
        {
            NetworkRouteRule[] rules =
            [
                new(NetworkActivityKind.Search, null, NetworkRouteMode.Direct, null, []),
                new(NetworkActivityKind.Search, "regional-library", NetworkRouteMode.VpnRequired, "ID", [6])
            ];
            var available = new[] { Healthy(6, "ID", 1) };
            Assert.AreEqual(6L, NetworkRouteLogic.Resolve(NetworkActivityKind.Search, "regional-library", rules, available, Now).ProfileId);
            Assert.AreEqual(NetworkRouteMode.Direct, NetworkRouteLogic.Resolve(NetworkActivityKind.Search, "other", rules, available, Now).Mode);
        }

        [TestMethod]
        public void FallOver_OnlyForNetworkFailuresNotRateLimitsOrBlocking()
        {
            Assert.IsTrue(NetworkRouteLogic.MayFailOver(NetworkFailureKind.TunnelUnavailable));
            Assert.IsTrue(NetworkRouteLogic.MayFailOver(NetworkFailureKind.TransportFailure));
            Assert.IsFalse(NetworkRouteLogic.MayFailOver(NetworkFailureKind.HttpForbidden));
            Assert.IsFalse(NetworkRouteLogic.MayFailOver(NetworkFailureKind.RateLimited));
            Assert.IsFalse(NetworkRouteLogic.MayFailOver(NetworkFailureKind.LoginRequired));
            Assert.IsFalse(NetworkRouteLogic.MayFailOver(NetworkFailureKind.ChallengeRequired));
        }

        private static VpnProfileHealth Healthy(long id, string country, int priority) =>
            new(id, priority, country, true, true, true, Now);
    }
}
