# Jularr VPN: universal network policy (first slice)

Owner decision (25 October 2026): a separate optional VPN network component; torrent
traffic VPN-protected by default. Search, browser, direct HTTP and metadata traffic
may receive independent source/action routing rules. Import multiple user-provided
WireGuard configurations, select priorities and country-constrained fallback
profiles. Normal Jularr connectivity remains direct.

## Implemented by this slice

- `WireGuardProfileLogic.Preview` strictly validates bounded single-peer WireGuard
  client configurations. It checks key encoding, client addresses, DNS addresses,
  endpoint/port and full-tunnel `AllowedIPs`. It rejects unknown directives,
  shell hooks and split-tunnel configurations. Returned previews contain no
  secrets; the submitted configuration is not stored by the preview API.
- `POST /api/v1/admin/network/wireguard/preview` is accessible to authorized
  instance settings administrators and returns **only** a validation preview,
  never a connection/success claim. Input is a JSON object with
  `configuration` containing the `.conf` text. The preview is not an upload
  or a persisted VPN profile yet.
- `NetworkRouteLogic` expresses action/source-specific choices, VPN-required
  torrent default, per-profile priorities, approved profile IDs, exit-country
  constraints, 90-second health freshness and fail-closed selection. Fallback is
  considered only for transport or tunnel problems; HTTP 403/429, logins and
  challenges are not reasons to rotate addresses.
- Logic tests cover key rejection, dangerous directives, split tunnels,
  verified health, source rules, country restrictions and safe fallback.

## Not implemented yet — do not enable or claim VPN protection

- Saving multiple profiles (including protected private keys), file-upload UI,
  profile editing/removal, profile migration and secret rotation.
- A WireGuard/OpenVPN process, an isolated network namespace, firewall/Kill-Switch
  enforcement, DNS/IPv6 leak prevention, observed exit IP/country, and continuous
  health reporting. The health record is a policy input, **not** a live probe.
- Routing real HTTP, browser, search, NZB, native or external qBittorrent
  transfers through the policy. The current external qBittorrent integration
  and Gluetun lab have not been modified.
- Retrying real jobs, UI event history, country verification, container lifecycle,
  administrative password re-authentication and explicit unprotected torrent
  override. Torrent Direct rules remain rejected.
- Parsing of arbitrary wg-quick commands or imported `PostUp`/`PostDown`
  scripts is deliberately unsupported. No uploaded text may be executed.

## Next verified vertical slices

1. Encrypted profile CRUD and upload UI, explicit management permissions and
   migration/backup checks. Never return raw configurations or secrets.
2. Managed WireGuard gateway + per-gateway network namespace, firewall before
   connection, shutdown and restart; real Docker Debian fail-closed testing.
3. Actual route enforcement at native download workers and per-source search;
   external-client isolation must be explicitly configured and verified.
4. Live egress IP/country/DNS probes, provider/server reporting, connection
   metrics, structured event history and fallback tests. Zero claim of
   protection until measurements confirm it.
5. Password-verified, audited and conspicuously warned VPN-off override;
   no silent direct routing when protection is required.

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
