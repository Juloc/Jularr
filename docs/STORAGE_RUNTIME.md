# Storage connections and mount runtime

Owner settings at `/Admin/Storage` and the authenticated Setup page at
`/Setup/Storage` use the same connection editor and Backend services. First-Owner
creation continues to this Setup page. This is the connection step, not completion
of the nine-step Setup Wizard. Library paths are configured on the same page below
their connection.

## Contracts

`ReadStorageMountsV1` returns a bounded configuration page; `ReadStorageMountV1`
returns one connection and `ReadStorageProtocolsV1` provides supported versions.
`CreateStorageMountV1` and `UpdateStorageMountV1` persist validated configuration
through Storage Logic. `ExecuteStorageMountV1` performs an explicit Connect,
Disconnect or ReadStatus operation outside database transactions, using short
authorized batches before and after external work.

`ReadStorageLocalPathsV1` lists the `Storage:LocalPaths` deployment configuration;
Local connections may only use one of those paths and need no helper. They are host or
container paths of the running platform (for example `D:	estlibrary` on a Windows host);
SMB/NFS exports remain absolute POSIX paths.
`WakeStorageMountV1` sends an Owner Wake-on-LAN packet to the configured MAC and
optional IPv4 broadcast address. `WakeOnDemand` records the Owner's policy: false
means only the Owner wakes the device, true allows automatic wake when a profile
opens media; the playback/reader package consumes that policy. `RemoveStorageMountV1`
asks a configured helper to disconnect, then deletes the version-checked connection
unless library paths still use it. `ReadStorageDirectoriesV1` lists at most 500
subfolders below the connection, rejecting traversal, links and missing folders.

The fresh `Storage.sql` baseline adds physical StorageMounts and protocol/version
catalogs. Resource IDs remain long. These tables supplement the original media
snapshot; LibraryRoots remains a separate logical owner. Connection settings are
available independently of Library/Requests module switches.

SMB supports 3.1.1 and 3.0, authenticated username/domain/password connections.
NFS supports 4.1 and 4.2 with server-export/UID/GID permissions. Server input is
a hostname or IPv4 address, not a URL. Share/export paths are absolute paths;
for example SMB `/bay4plex/media` or NFS `/volume1/media`. IPv6 and Kerberos/AD
ticket authentication require separate protocol support.

Passwords are encrypted with ASP.NET Data Protection before SQL binding. Persist
the web host's Data Protection key ring with its other protected authentication
state. Read results expose HasPassword only. An omitted password preserves the
stored value; switching to NFS clears SMB credentials. Input-only service data
accepts the password, while neutral persistence data hides protected bytes from JSON.

Saving never remounts automatically. A mounted connection keeps its applied
revision; explicit Disconnect/Connect applies new settings. Busy unmounts fail
instead of detaching active consumers. Status reports kernel mount state and
whether the applied revision matches current settings, not NAS health or capacity.
Normal catalog/settings rendering performs no NAS I/O or mount-status polling.

## Library paths and scanning

LibraryRoots (`Library.sql`) place one media type below a connection; paths on one
connection never overlap. Their location and media type are fixed, the name is
version-checked. Removing a path forgets its inventory and empty assets, never
Works or files. `QueueLibraryScanV1` queues one active scan per path in Operations.

Hosts opt in to `AddJularrBackgroundWork`; its worker requeues interrupted scans on
start, claims queued operations with `SKIP LOCKED` and calls System services, which
run only outside HTTP requests. A scan resolves the path below
`Storage:MountRoot/<StorageMountId>` (or the Local path), skips hidden files, links,
samples and unknown extensions, and upserts files in batches of 500. Only a complete
scan deletes vanished unassigned files and marks assigned ones missing; an
unreachable path fails the operation with `storage_unavailable` and keeps all
inventory. With `Media:FfprobePath` configured, new or changed video/audio files are
probed with a bounded `ffprobe` run (no shell). Files in a top folder already
identified as exactly one Work are assigned automatically when the path parser finds
their unit; everything else waits for manual identification under `/Admin/Library`.
Profiles read the stored availability only.

## Linux deployment

The console `Jularr.MountHelper` uses a private Unix-domain socket and the Linux
`mount.cifs`/`mount.nfs` utilities. Its API accepts only the three bounded actions
and fixed SMB/NFS options. Destinations are generated as `/mnt/jularr/<StorageMountId>`.
It has neither database credentials nor HTTP listeners. Credential files have
mode 0600, are deleted after the mount attempt and never appear as password arguments.
Persistent helper state records applied revision and non-secret configuration identity.

The [Compose overlay](../deploy/mount-helper/compose.yaml) extends an existing
`jularr` web service. Build context and helper [Dockerfile](../deploy/mount-helper/Dockerfile)
are included. The web service runs as UID/GID 1000 and receives the mount tree
with `rslave` propagation. Only the helper receives SYS_ADMIN, DAC_OVERRIDE and
DAC_READ_SEARCH (exactly the set `mount.cifs` keeps through libcap-ng; with SYS_ADMIN
alone it fails with "Unable to apply new capability set") and AppArmor mount
permission; it has a read-only image, explicit writable directories, no Docker
socket, no host PID namespace and no published ports. These capabilities are
substantial: install this component only on a trusted Linux host.
AppArmor is unconfined for this container, not disabled for the host or web service.

Create a dedicated, root-owned shared bind mount on the Linux Docker host before
starting the overlay, once per host boot unless persisted by its mount configuration:

```sh
sudo install -d -o root -g root -m 0755 /mnt/jularr
sudo mount --bind /mnt/jularr /mnt/jularr
sudo mount --make-rshared /mnt/jularr
```

Inspect existing mounts before applying these commands; do not stack the bind
mount repeatedly. Compose is layered with the deployment's actual web service
definition. Additional libraries do not need separate Compose bind mounts when
their NAS connection is managed beneath this shared tree.

Keep the socket and helper-state volumes dedicated to one Jularr instance. Share
the socket volume only with the web service, read-only; socket access is restricted
to root and group 1000. SMB uses that same UID/GID with cache=strict and explicit
read-only/read-write mode. NFS exports must grant the application identity the
intended permissions. Both protocols use nosuid/nodev/noexec. Mount commands are
bounded and run without a shell. Failure responses and logs contain no credentials.

Mount propagation requires a Linux Docker host and suitable kernel CIFS/NFS support;
Docker Desktop bind propagation is not supported by Docker's contract. Windows
development can edit configuration and test the service boundary, but cannot use
the Linux sidecar through a host-native Windows web process. Helpers are activated
explicitly; application startup does not install schemas or mount the NAS.

## Verification and remaining integration

Focused tests cover isolated PostgreSQL configuration/credentials/Owner access,
real Razor settings and Setup forms, antiforgery, bounded paging, private socket
communication, concurrent edits and native command arguments. Runtime smoke tests
must distinguish control-socket checks from successful SMB/NFS mounts.

Playback wakes sleeping storage on demand (per the connection's policy) and Arr+
imports completed downloads into the first writable library of the media type.
Connection paths of SMB/NFS mounts must be actual mount points (`Storage:RequireMountPoint`,
default on), so a stale empty mount point is never read as an empty library or
written to; a scan that skipped unreadable folders fails as `scan_incomplete`
without marking files missing. Read/write/capacity probes, automatic reconnection
and Synology management are later packages. One media type per library path is
the owner decision.

References: [Docker bind propagation](https://docs.docker.com/engine/storage/bind-mounts/#configure-bind-propagation),
[kernel CIFS usage](https://www.kernel.org/doc/html/latest/admin-guide/cifs/usage.html).
