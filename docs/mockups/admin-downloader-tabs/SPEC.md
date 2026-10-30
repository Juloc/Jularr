# Downloads page: one page, a tab per client

Status: owner-chosen direction 2026-10-29 (variant B, SABnzbd style). Mockup: [index.html](index.html). Extends #720; the earlier Usenet-only screen set `admin-downloader*` stays history, this page replaces its Overview and Queue for all downloaders.

## Purpose

One place to see and steer every download, from all clients or from one. A SABnzbd user feels at home: big speed, limit, pause, queue and history on one page.

## Navigation

- One pill-tab row, no second tab row: **All** and one tab per configured client (Jularr Usenet, SABnzbd, qBittorrent, Jularr Torrent, Jularr Direct, ...). The tab is the client, not the client type. Tabs follow the Connections list; a disabled module has no tab.
- Each tab shows its job count. **All** adds a Downloader column.
- Queue and History are two panels on the same page, not tabs.

## Header

- Title, then icon buttons (tooltip, no text): warnings (count), pause this client, settings of this client, and the primary add button (Add NZB, Add torrent or magnet, Add link; on All the neutral Add).
- Left KPI: current speed (large) with a short history line and the speed limit selector of that client. Further KPIs: remaining size and ETA, active and waiting jobs, free space and reserve.
- Speed is the client's own transfer rate; verify, repair, extract and import throughput are shown per row, never added to the download speed.

## Queue

- Columns: select box, drag handle, name, (Downloader on All), category, status pill, progress bar with percent, size, speed, ETA, row icons.
- Row icons: pause or resume, move to top, delete. Destructive actions confirm.
- **Multi-select** with the select box and shift-click ranges. Selected rows are highlighted; a floating dock appears with the count and icons: pause, resume, move to top, set category, retry, delete, clear selection. Actions apply to the clients that own the selected jobs.
- Drag reorder and priority only where the client supports them; unsupported actions are absent, not disabled.
- Filters: category, priority; search by name.

## Phase colours

One colour per phase, the same for status pill, progress bar and the icon tile; never a coloured edge stripe.

| Phase | Colour |
|---|---|
| Downloading | blue |
| Verifying | yellow |
| Repairing | orange |
| Extracting | teal |
| Importing | violet |
| Queued, waiting | muted violet-grey |
| Paused | light violet |
| Completed | green |
| Failed | red |

Phases come from the client's real state; a client without a phase (for example direct downloads without repair) never shows it.

## History

Completed and failed jobs of the selected client(s): name, status, reason (missing articles, needs your link, ...), size, finished. Icons: retry for failed, details, delete; "Failed only" filter; clear completed. History stays a view of the shared operation events, not a second store (see `admin-downloader/SPEC.md`).

## Per-client panel

Below History, specific to the selected client:

- **Jularr Usenet:** news servers with state, connections, speed, missing articles.
- **SABnzbd, qBittorrent:** connection state and version, category mapping, route and priority, test button.
- **Jularr Torrent:** VPN profile, tunnel state, exit address, proven upload lock with bytes sent, workspace folders and limits.
- **Jularr Direct:** sources with state and what they wait for; "Needs your link" with paste link and open page.

## Not in this page

Storage mounts and paths (Storage), release selection (Acquisition), post-download assignment problems (Activity), logs (System).

## Decided (owner, 2026-10-29)

1. The speed limit is per client.
2. Priority levels: Low, Normal, High, Force.
3. **All** offers a global pause.
