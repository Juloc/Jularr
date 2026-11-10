# Downloads: clients, native engines and VPN

Download clients, the native Usenet and torrent engines, download storage and the VPN path.
Index: [CODEMAP](../CODEMAP.md).
Specs: [DOWNLOAD_WORKSPACES](../DOWNLOAD_WORKSPACES.md), [TORRENT_ACQUISITION](../TORRENT_ACQUISITION.md), [VPN_NETWORK](../VPN_NETWORK.md).

## Decisions

Owner decisions; change them only with the owner.

- Downloads (`/Admin/Downloads`) follows SABnzbd: the upper queue contains transfers and downloads still waiting to start, already-started rows first
  without changing the downloader's saved priority/order.
  History begins when transfer ends and shows real verification, repair, extraction and moving stages live (an unknown percentage is indeterminate),
  then downloader completion or failure.
  Library import remains the existing Arr+ operation: a delivered download with an unresolved import stays in download History with its reason and a
  link to its Queue details; it is never presented as an unfinished transfer or as available in the library.
  Counts distinguish transfers, waiting downloads and post-processing.
  The existing stream patches values and requests authorized HTML fragments for changing rows, actions, client counts and paging; hidden pages rest,
  active selection/dialogs/edits are preserved, and failed refreshes retain the snapshot with a retry notice.
  No full-document reload is needed.

## Backend

- SABnzbd console: [file contracts](../../src/Jularr.Backend/Data/Admin/Acquisition/V1/DownloadClientFiles.cs), [queue/history
  services](../../src/Jularr.Backend/Service/Admin/Acquisition/V1/DownloadClientDashboardServices.cs), [file
  services](../../src/Jularr.Backend/Service/Admin/Acquisition/V1/DownloadClientFileServices.cs); [folder ownership
  Logic](../../src/Jularr.Backend/Logic/Acquisition/DownloadFileOwnershipLogic.cs) permits manual deletion only for explicit SABnzbd orphan folders
  under Incomplete, while shared file containment and preview live in
  [DownloadClientFileSafety.cs](../../src/Jularr.Backend/Service/Core/DownloadClientFileSafety.cs).
  [DownloadCleanupLogic.cs](../../src/Jularr.Backend/Logic/Acquisition/DownloadCleanupLogic.cs) stores retryable cleanup requests.
  Direct SMB is browse-only; mount-backed local paths allow manual cleanup after revalidation.
  Tests: `DownloadClientFileSafetyTests`.
- Native downloads: [DownloadStorageLogic.cs](../../src/Jularr.Backend/Logic/Storage/DownloadStorageLogic.cs) owns the single persisted-workspace
  readiness rule, returned as `DownloadStorageSettingsV1.IsConfigured` by
  [DownloadStorageSettingsServices.cs](../../src/Jularr.Backend/Service/Admin/Storage/V1/DownloadStorageSettingsServices.cs) for the UI and used by
  the source auto-setup; [DownloadStorage.cs](../../src/Jularr.Backend/Service/Core/DownloadStorage.cs) separately enforces current mount/capacity
  checks and write/job reservations.
  [AcquisitionServices.cs](../../src/Jularr.Backend/Service/System/Acquisition/V1/AcquisitionServices.cs) runs independent job services;
  [UsenetWorker.cs](../../src/Jularr.Workers/UsenetWorker.cs) schedules native passes.
  [UsenetJob.cs](../../src/Jularr.Backend/Service/Admin/Wanted/V1/UsenetJob.cs) uses
  [NzbReader.cs](../../src/Jularr.Integrations/Logic/Usenet/NzbReader.cs) (file names from quoted or plain subjects, the archive password from the NZB
  head), [UsenetDownloader.cs](../../src/Jularr.Integrations/Logic/Usenet/UsenetDownloader.cs), NNTP/yEnc and
  [ArchiveTools.cs](../../src/Jularr.Integrations/Logic/Usenet/ArchiveTools.cs) (7-Zip gets that password, else a placeholder).
  Focused tests: `DownloadStorageTests`, `UsenetTests`, `SourceTests`.
- Jularr's own download-only torrent engine (module `Torrent`, off by default;
  [scope](../TORRENT_ACQUISITION.md#jularrs-own-download-only-engine-module-torrent)):
  [TorrentDownloader.cs](../../src/Jularr.Integrations/Logic/Torrent/TorrentDownloader.cs) writes to peers only through
  [TorrentOutboundWire.cs](../../src/Jularr.Integrations/Logic/Torrent/TorrentOutboundWire.cs);
  [TorrentJob.cs](../../src/Jularr.Backend/Service/Admin/Wanted/V1/TorrentJob.cs) binds every pass to a verified VPN tunnel;
  [TorrentServices.cs](../../src/Jularr.Backend/Service/System/Acquisition/V1/TorrentServices.cs) run by
  [TorrentWorker.cs](../../src/Jularr.Workers/TorrentWorker.cs); status
  [TorrentEngineServices.cs](../../src/Jularr.Backend/Service/Admin/Acquisition/V1/TorrentEngineServices.cs).
  Tests `TorrentEngineTests`, `TorrentOutboundWireTests`, `TorrentClientTests`.
- VPN network path (module `Vpn`, off by default; [scope and gaps](../VPN_NETWORK.md)): profiles in `VpnProfiles` (migration `0017_vpn_profiles`)
  through [VpnProfileLogic.cs](../../src/Jularr.Backend/Logic/Network/VpnProfileLogic.cs) and
  [VpnProfileServices.cs](../../src/Jularr.Backend/Service/Admin/Network/V1/VpnProfileServices.cs); tunnels on demand in
  [VpnTunnels.cs](../../src/Jularr.Backend/Service/Core/VpnTunnels.cs) (a client binds with a `VpnLease`, stopped with the module) over
  [WireproxyTunnel.cs](../../src/Jularr.Integrations/Logic/Network/WireproxyTunnel.cs) and
  [Socks5Connector.cs](../../src/Jularr.Integrations/Logic/Network/Socks5Connector.cs); route policy
  [NetworkRouteLogic.cs](../../src/Jularr.Backend/Logic/Network/NetworkRouteLogic.cs).
  Tests `VpnTunnelTests`, `VpnNetworkPolicyTests`.
- Native workspace mount selection: [StorageFileSystem.cs](../../src/Jularr.Integrations/Logic/Storage/StorageFileSystem.cs), visible Linux mounts and
  stable filesystem identities; automount/overmount regression cases in `DownloadStorageTests`.

## Frontend

- Downloads lifecycle: [Downloads.cshtml.cs](../../src/Jularr.Frontend/Pages/Admin/Downloads/Downloads.cshtml.cs) owns downloader queue/history and
  its authorized `?handler=Live` fragments; [Downloads.entry.ts](../../src/Jularr.Frontend/Pages/Admin/Downloads/Downloads.entry.ts) follows the
  existing live stream for row membership/actions/paging and recovers on a quiet tick.
  Backend `ReadActivityV1`/`ReadDownloadClientsV1` distinguish transfers, post-processing and delivered files independently of import.
  Focused checks: `AcquisitionTests.DownloadsLifecycle_*`, `DatabaseMigrationTests.DownloadStages_*`, E2E
  `DownloadPagesTests.Downloads_LiveMovesIntoHistory_*`.
- SABnzbd downloader console: [Acquisition.DownloadConsole.cs](../../src/Jularr.Frontend/Pages/Admin/Acquisition/Acquisition.DownloadConsole.cs) owns
  three server-rendered tabs, the [PageModel](../../src/Jularr.Frontend/Pages/Admin/Acquisition/Acquisition.cshtml.cs) authorizes a scoped
  `?handler=Console` fragment, and [DownloadConsole.entry.ts](../../src/Jularr.Frontend/Pages/Admin/Acquisition/DownloadConsole.entry.ts) refreshes
  only that fragment using the shared `onPage`/insertion lifecycle.
  Destructive actions use shared `Modal` and antiforgery, with localized messages.
  The global operations SSE does not contain SABnzbd's external queue.
- [Download storage](../../src/Jularr.Frontend/Pages/Admin/DownloadStorage/DownloadStorage.cshtml.cs), also `/Setup/DownloadStorage` after library
  storage with Acquisition: per-area Incomplete/Complete folder dialogs, reserve, optional limits and parallel jobs.
  The chip row joins Storage; each folder shows the free space last measured on its connection.
  Uses the canonical Storage services; browser tests: `DownloadStorageTests`.
- The download clients are spread by kind over two addresses of the one
  [Acquisition](../../src/Jularr.Frontend/Pages/Admin/Acquisition/Acquisition.cshtml.cs) page (`Kinds`, `HomeOf`; an older address of a client
  redirects to its page, the former `/Admin/Acquisition/Downloads` to External clients): `/Admin/Downloads/Servers` (Servers) and
  `/Admin/Downloads/Clients` (External clients: SABnzbd and qBittorrent with the SABnzbd console, and with module `Torrent` Jularr's own torrent
  engine; a row of kinds, `?Kind=`); both follow the owner's downloader mockup (panels 3 and 7,
  [Acquisition.Downloaders.cs](../../src/Jularr.Frontend/Pages/Admin/Acquisition/Acquisition.Downloaders.cs)): the heading with the add button (a
  dialog of kinds where there are several), the list, and the opened connection (`?AcquisitionConnectionId=`, else the first) with its settings inline
  on the left, saved by the pill, and on the right what is known (a news server's live connections, speed and missing articles, a client's address,
  last test and the functions the Downloads page offers it); the setup step keeps the cards of every kind.
  [Speed and schedule](../../src/Jularr.Frontend/Pages/Admin/DownloadSpeed/DownloadSpeed.cshtml.cs) (`/Admin/Downloads/Speed`, panel 5): the heading
  and one panel of speed limits, a row per client that can be held back with its live speed and its limit (`Read`/`SetDownloadSpeedLimitV1`), saved by
  the pill; `AcquisitionTests.DownloaderPages_*`.
  The torrent engine's card shows the measured lock and byte counts (`TorrentFacts`), and its workspace appears on Download storage only with the
  module; `TorrentClientTests`.
- [VPN](../../src/Jularr.Frontend/Pages/Admin/Network/Network.cshtml.cs) (`/Admin/Network`, module `Vpn`): a card per WireGuard profile with what its
  running tunnel measured (exit address against the host, pings, handshake, traffic, bound clients) and Check, Stop and Delete; a profile is added in
  a dialog from a `wg0.conf` file or pasted text; `VpnTunnelTests`.
- [Downloads](../../src/Jularr.Frontend/Pages/Admin/Downloads/Downloads.cshtml.cs) (`/Admin/Downloads`, first entry of the sidebar's Downloads group,
  [spec and mockup](../mockups/admin-downloader-tabs/SPEC.md)), laid out as its mockup: the heading `Downloads` with the client's icon buttons
  (warnings, pause or resume, settings, Add NZB / Add torrent or magnet / Add: a dialog that finds the wanted title and takes the address), under it
  one pill tab row (All and one tab per switched-on client, with its open count; no chip row), the figures kept current by the live stream
  (`downloads:<client>` entities: speed with its short [Sparkline](../../src/Jularr.Frontend/Framework/Components/Sparkline.cs) line, whose points
  `data-live-points` patches, and the client's speed limit menu; remaining with ETA, active and waiting, free space where the downloads land: a native
  workspace's last measure, SABnzbd's own report, on All the fullest workspace), the queue and history as list-kit panels in the mockup's table look
  (box, handle and bold name; category and priority menus, pause or resume, move to top and delete per row, drag by the handle where the client
  reorders ([Downloads.entry.ts](../../src/Jularr.Frontend/Pages/Admin/Downloads/Downloads.entry.ts),
  [rules](../../src/Jularr.Frontend/Pages/Admin/Downloads/Downloads.ts), `downloads.test.mjs`), Failed only, how long ago, retry or details and delete
  an entry, Clear completed; one colour per phase on pill and bar, tokens `--phase-*` in
  [Theme.scss](../../src/Jularr.Frontend/Modules/Appearance/Theme.scss)), the dock's bulk zone for the chosen rows (shift-click ranges and clear in
  [ActionDock.entry.ts](../../src/Jularr.Frontend/Framework/Components/ActionDock.entry.ts)) and the client's own panels (news servers with their live
  connections, speed and missing articles; SABnzbd/qBittorrent connection with Test beside its route; torrent VPN path beside workspace and limits;
  direct sources with what they wait for and their last result, a download that needs a link with Paste link (a dialog) and Open page);
  `AcquisitionTests.DownloadsPage_*`, `AdminMenuTests`.
