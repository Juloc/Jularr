# Torrents: Nyaa, qBittorrent and Jularr's own engine

Jularr owns wanted decisions, profile language ranking, operations and library import. Nyaa is a
search-only source; qBittorrent is an external download client. Jularr's own download-only engine
(module `Torrent`) is described at the end. The Gluetun lab is a deployment check, **not** a runtime
VPN health signal.

## One-time setup

1. Run qBittorrent with its transfer network in the same network namespace as Gluetun.
   Publish its WebUI only as required for Jularr; do not independently expose qBittorrent's
   network namespace to a second route around the VPN.
2. In Jularr Admin > Downloads > External clients, add qBittorrent with its WebUI URL
   and account credentials. Test the connection.
3. Assign the storage mount/folder through which Jularr can read the client's completed
   download directory. The client path and Jularr mount must refer to the same files.
4. Add Nyaa as an indexer. No API key is needed. Test it and try manual Search.
5. Use the existing profile language order and fallback for light novels, then request
   a supported title. Other media types are not searched through Nyaa by this integration.

## Download lifecycle

- Nyaa RSS offers are sent **only** to qBittorrent, never to SABnzbd.
- A unique tag links an add operation to its torrent hash. A matching name or newest
  torrent is insufficient.
- Download status, ETA and size are monitored by hash. Missing or errored torrents
  enter the existing interrupted/retry flow, not an infinite active state.
- On full completion, the shared importer validates and places files in the selected
  library. An absent mount or ambiguous file awaits attention instead of silently
  falling back to a local directory.
- Activity offers pause/resume, removal with payload deletion and per-file progress
  for running qBittorrent jobs. The removal action is destructive; use its confirmation.
- Import currently **does not automatically remove a seeded torrent or its data**.
  This avoids stopping seeding and deleting files implicitly. Explicit removal from
  the client remains an administrative decision.

## Required integration checks (real environment)

1. Create an isolated disposable test deployment and verify that the frozen database
   baseline installs, the numbered migration adds IDs 9/10 and an existing database
   migrates with its required backup.
2. Configure qBittorrent behind Gluetun; verify DNS and external IP from within that
   same namespace. Take the tunnel down: DNS and outbound transfers must fail closed.
   Bring the tunnel up and confirm transfer resumes without recreating the job.
3. Log into Jularr, save both connections, test them and manually search Nyaa.
   Assert the category/language matches, and the full RSS torrent link is used.
4. Grab a freely redistributable test torrent while another torrent is added to
   qBittorrent: verify Jularr tracks only the tagged hash. Test restart/recovery.
5. Check progress, ETA, per-file names/progress, pause, resume and cancellation in
   Activity's queue/files views, including keyboard and mobile navigation.
6. Map qBittorrent's completed directory through the same storage used by the importer.
   Import a supported test document and verify one correct library item with no duplicates.
7. Make the NAS unavailable during import, then restore it. Confirm attention/retry
   rather than a misplaced local copy or false success.
8. Remove a Jularr-owned test torrent: verify that the remote job and its payload
   disappear, with an explicit confirmation, and that other torrents are untouched.
9. Verify wrong qBittorrent credentials, invalid Nyaa XML, RSS with a different Nyaa
   namespace, missing torrent hashes, and disabled search-source fallbacks.
10. Run Frontend, Backend and Browser Smoke CI, plus targeted database migration,
    acquisition, activity and source tests. Do not treat a passing browser smoke test
    as end-to-end validation.

## Not covered

- Automatic cleanup/retention policies for seeded torrents and advanced ratio limits.
- Arbitrary browse/delete of all files in the client's incomplete/complete folders
  that are not attached to an active Jularr operation.
- Real-time proof of a VPN tunnel from Jularr itself; the Gluetun lab can verify it.
- Bulk light-novel search-quality benchmark across 25 real titles.

Do not claim these optional/operational capabilities are implemented.

## Jularr's own download-only engine (module `Torrent`)

Owner decision #1626: the engine never sends file data. Off by default; it needs `Acquisition` and
`Vpn` (migration `0018_native_torrent`: connection type 12, workspace area 3). With the module off,
or with no torrent job, nothing runs: [TorrentWorker](../src/Jularr.Workers/TorrentWorker.cs)
sleeps on `WorkSignals.Torrents` until a grab wakes it; with jobs it looks every 30 s.

- Engine: [TorrentDownloader.cs](../src/Jularr.Integrations/Logic/Torrent/TorrentDownloader.cs)
  (own code, no library: MonoTorrent could not guarantee the lock because its encryption sits above
  the connection, it always announces fast/extension/DHT bits and sends bitfield, have and unchoke).
  It never listens, never unchokes, never answers a request and reports `uploaded=0`. Its only
  writer is [TorrentOutboundWire](../src/Jularr.Integrations/Logic/Torrent/TorrentOutboundWire.cs):
  handshake (plain, or with the BEP 10 bit alone for magnets), interested, not interested,
  request, cancel, keep-alive, the fixed extension handshake `d1:md11:ut_metadatai1eee` and
  `ut_metadata` requests. Everything else (piece, unchoke, have, bitfield, fast extension, metadata
  data, peer exchange) is refused before writing. No encryption (MSE), no DHT, no PEX, no uTP.
- Sources: `.torrent` (fetched by the indexer client at grab) and magnet links (metadata from peers
  through `ut_metadata`, checked against the info hash). v1 torrents only; HTTP(S) trackers only.
- Jobs: staging holds `job.torrent`/`job.magnet`, `job.info`, `job.state` (verified pieces) and
  `data/`. A pass ([TorrentServices.cs](../src/Jularr.Backend/Service/System/Acquisition/V1/TorrentServices.cs),
  two minutes) writes only SHA-1 verified pieces; the next pass and a restart continue from them,
  re-verifying from disk without a state file. A new try at the same torrent (same info hash) takes over the
  folder of an earlier try that no longer runs ([TorrentResume.cs](../src/Jularr.Backend/Service/Admin/Wanted/V1/TorrentResume.cs)),
  so its verified pieces are kept. A pass connects to up to 50 peers at once. A finished job contacts nobody and imports through
  the shared `DownloadImport`. Pause, resume and removal are those of Jularr's own downloaders.
- Network: every pass takes a `VpnLease` through [TorrentJob.cs](../src/Jularr.Backend/Service/Admin/Wanted/V1/TorrentJob.cs);
  `NetworkRouteLogic` accepts only a tunnel that answers pings and whose exit differs from the host.
  Without one the pass sends nothing and the job rests five minutes. Peer connections and tracker
  requests go through the tunnel; there is no direct path.
- Admin: Downloads > External clients offers "Add torrent engine" only with the module; its card shows the
  lock proven at that read (`ReadTorrentEngineV1`), file data sent (0), control bytes, refused frames,
  received bytes, connected peers and whether a verified tunnel is ready. Test checks module, lock and
  tunnel. Downloads appear in Activity like the other own downloaders.
- Tests: `TorrentEngineTests` (a seeder and a counter-peer on loopback ask for blocks and metadata
  during, after and across passes; their logs of the engine's bytes contain no file data, no
  piece advertisement and no other extension), `TorrentOutboundWireTests`, `TorrentClientTests`.

Not covered: UDP trackers, DHT and uTP (the VPN path is TCP only), incoming connections, file
selection inside a torrent, v2-only torrents, a real swarm through a real VPN provider.
