# qBittorrent and Nyaa integration

Jularr owns wanted decisions, profile language ranking, operations and library import. Nyaa is a
search-only source; qBittorrent is an external download client. Neither client is an in-process
torrent engine. The Gluetun lab is a deployment check, **not** a runtime VPN health signal.

## One-time setup

1. Run qBittorrent with its transfer network in the same network namespace as Gluetun.
   Publish its WebUI only as required for Jularr; do not independently expose qBittorrent's
   network namespace to a second route around the VPN.
2. In Jularr Admin > Connections > Download clients, add qBittorrent with its WebUI URL
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
