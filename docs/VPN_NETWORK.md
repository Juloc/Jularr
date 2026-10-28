# Jularr VPN: universal network path

Owner decision (25 October 2026): a separate optional VPN network component; torrent
traffic VPN-protected by default. Search, browser, direct HTTP and metadata traffic
may receive independent source/action routing rules. Import multiple user-provided
WireGuard configurations, select priorities and country-constrained fallback
profiles. Normal Jularr connectivity remains direct.

## Implemented

- `WireGuardProfileLogic.Preview` strictly validates bounded single-peer WireGuard
  client configurations: key encoding, client addresses, DNS addresses, endpoint/port
  and full-tunnel `AllowedIPs`; unknown directives, shell hooks and split tunnels are
  rejected. `POST /api/v1/admin/network/wireguard/preview` returns only that preview.
- Module `Vpn` (off by default, migration `0017_vpn_profiles`): `VpnProfiles` keeps
  imported `wg0.conf` profiles (uploaded or pasted) with the configuration protected by
  Data Protection; a profile needs a DNS server. Reads never return key material.
  Services in [VpnProfileServices.cs](../src/Jularr.Backend/Service/Admin/Network/V1/VpnProfileServices.cs),
  page `/Admin/Network`.
- Runtime inside the one Jularr container, without a sidecar, capability or network
  interface: [VpnTunnels.cs](../src/Jularr.Backend/Service/Core/VpnTunnels.cs) starts one
  userspace WireGuard client per profile on demand ([wireproxy](https://github.com/pufferffish/wireproxy),
  ISC, pinned by hash in the image; [WireproxyTunnel.cs](../src/Jularr.Integrations/Logic/Network/WireproxyTunnel.cs)).
  Its only door is a SOCKS5 listener on loopback with a one-time sign-in; the
  configuration file is removed once the helper runs and its output is discarded.
- Binding: a client takes a `VpnLease` and opens connections and HTTP handlers through
  it ([Socks5Connector.cs](../src/Jularr.Integrations/Logic/Network/Socks5Connector.cs);
  HTTP uses a `ConnectCallback`, so there is no proxy setting to bypass). Host names go
  to the tunnel unresolved, so DNS is answered by the profile's DNS server through the
  tunnel. IPv6 destinations work only with an IPv6 route in the profile; otherwise they
  are unreachable, never sent directly. A stopped or failed tunnel refuses new
  connections (fail-closed); there is no direct path behind a lease.
- Health while a tunnel runs (a timer only then): pings through the tunnel
  (`Vpn:CheckAliveAddress`, default `1.1.1.1`, every 20 s, read from the helper's
  `/readyz`), the WireGuard handshake and byte counters (`/metrics`, keys redacted),
  and the exit address and country from `Vpn:ExitCheckAddress` (default
  `https://ipinfo.io/json`) through the tunnel compared with the host's own address
  (every 10 minutes and on Check). An exit equal to the host's address fails the
  tunnel. `VpnTunnels.Health()` feeds `NetworkRouteLogic`. A tunnel without leases
  stops after five minutes; switching the module off stops all tunnels.
- Tests: `VpnTunnelTests` (SOCKS sign-in and unresolved names, proxy down without
  touching the target, generated configuration, counters, module gate, import/check/
  delete without key exposure, missing helper fails closed), `VpnNetworkPolicyTests`.

## Not implemented yet

- OpenVPN: no userspace OpenVPN client works without a TUN device and `NET_ADMIN`,
  so it would break the minimal compose; not built.
- UDP through the tunnel (wireproxy's SOCKS5 has only CONNECT): bound clients use TCP
  only (no UDP trackers, DHT or uTP for torrents).
- Per-source routing rules, priorities and country fallback as stored settings, and
  routing search, browser, metadata or the external qBittorrent through the tunnel. Bound so far:
  Jularr's own torrent engine (profiles tried by id, see [torrents](TORRENT_ACQUISITION.md)).
- Password-verified, audited VPN-off override; profile editing and secret rotation.

## Acceptance for a future production-ready release

- Unit and contract tests green, fresh and upgraded database tests with
  backup/restore, real WireGuard + firewall + DNS/IPv6 fail-closed tests on
  Debian Docker, fresh module installation and uninstall without unrelated
  resource damage, no socket exposure to the web host, secrets absent from
  HTTP responses/logs/URLs/crash output.
- VPN-only traffic has **no** direct fallback even during VPN loss or start-up
  races. Source-specific Indonesia policy may fall back to another verified
  Indonesian egress but never to another country without an explicit policy
  change. Rate limits and source blocking do not trigger automatic IP rotation.
- Off means no background resources, no visible enabled-only settings, and no
  dependent job leak. Changing a route drains previous connections.

References: [torrent integration](TORRENT_ACQUISITION.md),
[download spaces](DOWNLOAD_WORKSPACES.md), [architecture epic #1448](https://github.com/Juloc/Jularr/issues/1448).
