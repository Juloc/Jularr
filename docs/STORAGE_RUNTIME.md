# Storage connections, direct SMB and NAS volumes

Owner settings at `/Admin/Storage` and the authenticated Setup page at
`/Setup/Storage` use the same connection editor and Backend services. First-Owner
creation continues to this Setup page. This is the connection step, not completion
of the nine-step Setup Wizard. Library paths are configured on the same page below
their connection.

## Contracts

`ReadStorageMountsV1` returns a bounded configuration page; `ReadStorageMountV1`
returns one connection and `ReadStorageProtocolsV1` provides supported versions.
`CreateStorageMountV1` and `UpdateStorageMountV1` persist validated configuration
through Storage Logic. `CheckStorageMountV1` probes the connection now, outside
database transactions, and records the result. `ReadStorageMountVolumeV1` returns
the compose lines that mount an SMB or NFS connection as a Docker volume.
Every feature reaches a connection's files through `StorageAccess`
([StorageFiles.cs](../src/Jularr.Integrations/Logic/Storage/StorageFiles.cs)): paths inside the
connection, a folder on this host for local paths and volumes, Jularr's own session for direct SMB.

`ReadStorageLocalPathsV1` lists the `Storage:LocalPaths` deployment configuration;
Local connections may only use one of those paths. They are host or
container paths of the running platform (for example `D:\testlibrary` on a Windows host);
SMB/NFS exports remain absolute POSIX paths.
`WakeStorageMountV1` sends an Owner Wake-on-LAN packet to the configured MAC and
optional IPv4 broadcast address. `WakeOnDemand` records the Owner's policy: false
means only the Owner wakes the device, true allows automatic wake when a profile
opens media; the playback/reader package consumes that policy. `RemoveStorageMountV1`
deletes the version-checked connection unless library paths still use it. `ReadStorageDirectoriesV1` lists at most 500
subfolders below the connection, rejecting traversal, links and missing folders.

The fresh `Storage.sql` baseline adds physical StorageMounts and protocol/version
catalogs. Resource IDs remain long. These tables supplement the original media
snapshot; LibraryRoots remains a separate logical owner. Connection settings are
available independently of Library/Requests module switches.

SMB supports 3.1.1 and 3.0, authenticated username/domain/password connections, reached by Jularr
itself (the default) or mounted by Docker as a volume (`IsVolume`).
NFS supports 4.1 and 4.2 with server-export/UID/GID permissions. Server input is
a hostname or IPv4 address, not a URL. Share/export paths are absolute paths;
for example SMB `/bay4plex/media` or NFS `/volume1/media`. IPv6 and Kerberos/AD
ticket authentication require separate protocol support.

Passwords are encrypted with ASP.NET Data Protection before SQL binding. Persist
the web host's Data Protection key ring with its other protected authentication
state. Read results expose HasPassword only. An omitted password preserves the
stored value; switching to NFS clears SMB credentials. Input-only service data
accepts the password, while neutral persistence data hides protected bytes from JSON.

Saving changes only the stored connection. A changed SMB or NFS connection takes
effect once its new compose lines are in place and the container restarted.
Normal catalog/settings rendering performs no NAS I/O.

## Library paths and scanning

LibraryRoots (`Library.sql`) place one media type below a connection; paths on one
connection never overlap. Their location and media type are fixed, the name is
version-checked. Removing a path forgets its inventory and empty assets, never
Works or files. `QueueLibraryScanV1` queues one active scan per path in Operations.
The storage worker also calls `ScheduleLibraryScansV1`: every path without a scan in the
last `LibraryScanIntervalHours` of the instance settings (default 24, 0 turns it off) is
rescanned, except on connections last probed offline, so a sleeping NAS is not woken.
Finished operations leave the Activity history after the Admin's retention days (default 30) or, oldest first, once the history exceeds its size in decimal megabytes (default 10); unacknowledged failures stay (`PruneOperationsV1`, hourly).

Hosts opt in to `AddJularrWorkers` (`Jularr.Workers`); its worker requeues interrupted scans on
start, claims queued operations with `SKIP LOCKED` and calls System services, which
run only outside HTTP requests. A scan resolves the path below
`Storage:MountRoot/<StorageMountId>` (or the Local path), skips hidden files, links,
samples and unknown extensions, and upserts files in batches of 500. Only a complete
scan deletes vanished unassigned files and marks assigned ones missing; an
unreachable path fails the operation with `storage_unavailable` and keeps all
inventory. With ffprobe (in the image), new or changed video/audio files are
probed with a bounded `ffprobe` run (no shell). Files in a top folder already
identified as exactly one Work are assigned automatically when the path parser finds
their unit. In a music library, files tagged with a MusicBrainz release group go to that
album; with Chromaprint's `fpcalc` (in the image) and an AcoustID key
stored and switched on under `/Admin/Providers`, an untagged folder whose files'
fingerprints all match one album goes to that album. Everything else waits for manual
identification under `/Admin/Library`.
Profiles read the stored availability only.

The Rename tab there previews moving identified files into the import layout
(subtitles follow their video, a trailing `[quality]` tag stays, taken names get a
counter, games and libraries being scanned are left alone). Applying needs the
fingerprint of exactly that plan; each file is moved on the share first and then
recorded under its new path, keeping its identity, analysis and progress, or moved
back. Offline libraries and names taken meanwhile are skipped, a video together with
its subtitles, and folders a rename emptied are removed.

## Direct SMB

Without a volume Jularr speaks SMB itself through [SMBLibrary](https://github.com/TalAloni/SMBLibrary)
(LGPL-3.0, owner-approved): nothing goes into `compose.yaml`, and the container starts while the NAS
sleeps. [SmbShares.cs](../src/Jularr.Integrations/Logic/Storage/SmbShares.cs) keeps the signed-in sessions
per connection and saved configuration ([SmbShare.cs](../src/Jularr.Integrations/Logic/Storage/SmbShare.cs)):
listings, renames and deletions share one session, every open file reads or writes on a session of its own
(at most eight per share, then the least busy is shared), so a playback never waits behind a scan or an
import. Requests on one session take turns, a lost session opens again on the next request, and a session
without open files closes after 15 idle minutes so the NAS may sleep. A file reads in pieces of up to a
megabyte and fetches the next piece while the current one is used.
The share path names the share and then a folder inside it (`/media/library` is the folder `library`
of the share `media`). Hidden and system entries and links are left out like on a local folder.

Playback, games and the PDF reader stream the file with ranges straight from the share. FFmpeg and
ffprobe read it from an address on the loopback interface with a random token that lives as long as
the tool runs ([StorageStreamServer.cs](../src/Jularr.Integrations/Logic/Storage/StorageStreamServer.cs));
fpcalc, whose static build has no network input, reads it on its standard input, and 7-Zip gets a
temporary copy of a CBR or CB7. Imports from the download folder are copied beside the target and
published when complete; a hardlink needs a folder on the same host.

## Docker volumes

Jularr mounts nothing itself and needs no helper container or extra capabilities.
Docker mounts an SMB or NFS share as a volume of the Jularr container at
`Storage:MountRoot/<StorageMountId>` (default `/mnt/jularr`), where Jularr looks for
the connection. The Storage page shows the lines to add to `compose.yaml`
(`ReadStorageMountVolumeV1`, built in [StorageVolumeLogic.cs](../src/Jularr.Backend/Logic/Storage/StorageVolumeLogic.cs)):

```yaml
services:
  jularr:
    volumes:
      - nas-1:/mnt/jularr/1
volumes:
  nas-1:
    driver_opts:
      type: cifs
      device: "//nas.local/media"
      o: "username=jularr,password=${JULARR_NAS_1_PASSWORD},vers=3.1.1,uid=1654,gid=1654,ro"
```

The SMB password stays out of `compose.yaml`: it goes into `.env` as
`JULARR_NAS_<StorageMountId>_PASSWORD`, each comma written twice as the kernel's
option parser expects. Files of an SMB share belong to the image's app user (1654).
NFS uses `addr`, `nfsvers` and `soft`, so an unreachable server returns errors instead
of hanging readers; its export must grant UID 1654 the intended permissions.
Docker resolves the SMB server name and the NFS `addr` itself.

Docker mounts volumes when the container starts: a NAS that is off at that moment
keeps the container from starting, and a share that disappears later shows as
offline until it is back. A NAS that sleeps is better reached directly.

## Health checks

`StorageMountStates` keeps what Jularr last saw of a connection: available, offline
or no access, the reason, free and total space and when it was checked. The Check
action and every library scan probe the connection (one directory entry and the
volume's space, at most five seconds, so a hanging network mount never blocks);
reads use the stored state and the `StorageMountHealth` view, which shows an offline
connection with Wake-on-LAN as asleep. A failed probe counts in `FailedChecks`;
`CheckStorageMountsV1`, run every minute by the storage worker, probes such a
connection again after 1, 2, 5, 10 and then every 15 minutes, until it is available.
The Owner is notified once, at the third failure in a row. Connections that are
available are not polled, so a sleeping NAS is not woken; the worker never sends
Wake-on-LAN. A probe that hangs in the kernel keeps its thread until the kernel gives
up; callers continue after five seconds.

Playback wakes sleeping storage on demand (per the connection's policy) and Arr+
imports completed downloads into the first writable library of the media type.
Connection paths of SMB/NFS mounts must be actual mount points (`Storage:RequireMountPoint`,
default on), so a stale empty mount point is never read as an empty library or
written to; a scan that skipped unreadable folders fails as `scan_incomplete`
without marking files missing. A Synology NAS can also be managed through DSM: with an administrator account
without two-step verification (address, account and an encrypted password per
connection, never returned), the Owner reads model, DSM version, temperature,
volumes and disks on request, and shuts the NAS down or restarts it after a
confirmation. Every action opens one DSM session; nothing polls the NAS. A stored
password is used only for the address and account it was saved with. Use HTTPS with a valid
certificate, or HTTP only inside the home network. One media type per library path is
the owner decision.

References: [Docker volume options](https://docs.docker.com/reference/compose-file/volumes/#driver_opts),
[kernel CIFS usage](https://www.kernel.org/doc/html/latest/admin-guide/cifs/usage.html).


## Native/direct download staging safety (pre-release R03 slice)

Jularr's native Usenet downloader and direct-source book downloader accept
independent `Storage:UsenetDownloadPath` and `Storage:DirectSourceDownloadPath`
workspaces. For existing explicitly configured installations, each falls back
to the shared `Storage:DirectDownloadPath` setting when its own setting is
absent. None has a built-in default: unconfigured,
missing, relative or symlinked paths are rejected before native/direct grabs.
Jobs already active are put into attention rather than silently writing into
`/tmp` or the container's overlay filesystem. The SABnzbd download directory
is a **separate service**; its completed files use `RemotePath` / `LocalPath`
mapping in the SABnzbd acquisition connection.

For example, mount a persistent storage volume at `/downloads/staging` in
the Jularr container, then set `Storage__UsenetDownloadPath=/downloads/staging`. Direct-source book
downloads can separately use `Storage__DirectSourceDownloadPath=/downloads/books`,
with that path also mounted before Jularr starts.
The directory must already exist. On Linux, Jularr requires this path to be
inside a non-root mounted filesystem (a nested folder is accepted) unless the
owner deliberately sets `Storage__AllowDirectDownloadOnHostFilesystem=true`
for an explicitly configured local/VM workspace. This is **not** a quota
override: disk reservations, capacity checks and UI confirmation are still
required before release in #1209. If a NAS mount disappears, Jularr must not
fall back to VM-local storage; check the actual mounts and free space before
enabling acquisition.


## Native NNTP job size ceiling

`Storage__UsenetMaxJobBytes` controls the maximum allocated article-file bytes
for any single NNTP job (default: **20 GiB**, independently configurable per
installation). The worker validates the parsed NZB's total declared size
before opening article files; the downloader additionally reserves actual
file-growth extents across concurrent NNTP sessions before each write. An
invalid, unknown or over-budget NZB waits with `usenet_disk_budget`, preserving
its staging data instead of exceeding the configured job limit. Lowering the
limit below an existing partial job pauses it on the next pass.

**This is a job-size limit, not the total workspace size**, and it does not
reserve disk across downloads, cap PAR2/7-Zip extraction, protect other
processes sharing the filesystem, or cover direct-source books. Those remain
separate prerequisites of release issue #1209; do not expose this as a
complete hard workspace quota.
NZB segment sizes are checked before summing to reject numeric overflow. Existing
article files count against the job limit even when every article is finished,
so a restart or lower configured limit cannot bypass the extraction preflight.

**Pseudo mounts:** Native download staging rejects Linux `tmpfs`, `overlay`,
`proc` and other operating-system pseudo filesystems even if Docker reports
a mount point. A separate bind mount can still target VM-local storage:
verify its physical source explicitly before enabling downloads.
The nearest mount is authoritative: an unsafe nested mount cannot inherit its
parent's approval, and host-filesystem opt-in does not permit RAM/overlay storage.

## Space checks for native Usenet unpacking

Before extracting archives, the native worker checks free space on the **actual
filesystem containing the staging files** and reserves a rough 10% overhead
above the compressed input size for the next extraction. Linux mount identity
is read from `/proc/self/mountinfo`; if the filesystem cannot be resolved
to a ready mount, extraction enters the existing `usenet_disk_full` wait state
rather than using a misleading VM-root free-space number. This is a preflight
safeguard, **not a hard quota**; capacity reservation across jobs and checks
during extraction are still part of #1209.
