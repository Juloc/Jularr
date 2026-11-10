# Storage connections and mounts

Storage connections, mounts, NAS wake and file access for every feature.
Index: [CODEMAP](../CODEMAP.md).
Specs: [STORAGE_RUNTIME](../STORAGE_RUNTIME.md).

## Backend

- [Storage services](../../src/Jularr.Backend/Service/Admin/Storage/V1): bounded Owner configuration/catalog reads, protected connection saves, the
  Check action and the compose lines of a Docker volume.
  [Storage data](../../src/Jularr.Backend/Data/Admin/Storage/V1) contains versioned inputs/results.
  [DownloadStorageServices.cs](../../src/Jularr.Backend/Service/Admin/Storage/V1/DownloadStorageSettingsServices.cs) exposes effective native
  workspace configuration through `ReadDownloadStorageV1`; pages never read Backend options directly or infer mount health from a configured path.
- [StorageMountLogic.cs](../../src/Jularr.Backend/Logic/Storage/StorageMountLogic.cs) owns canonical configuration writes;
  [StorageMountStateLogic.cs](../../src/Jularr.Backend/Logic/Storage/StorageMountStateLogic.cs) the observed state, failed checks and their backoff;
  [StorageVolumeLogic.cs](../../src/Jularr.Backend/Logic/Storage/StorageVolumeLogic.cs) the compose lines of a Docker volume; probes in
  [StorageOptions.cs](../../src/Jularr.Backend/Service/Core/StorageOptions.cs), repeated checks in
  [StorageCheckServices.cs](../../src/Jularr.Backend/Service/System/Storage/V1/StorageCheckServices.cs) and the periodic library rescan
  (`ScheduleLibraryScansV1`, `QueueDueLibraryScansAsync`) run by [StorageWorker.cs](../../src/Jularr.Workers/StorageWorker.cs); Synology health and
  power in [StorageSynologyServices.cs](../../src/Jularr.Backend/Service/Admin/Storage/V1/StorageSynologyServices.cs) through
  [SynologyClient.cs](../../src/Jularr.Integrations/Logic/Synology/SynologyClient.cs) (HTTPS accepts a certificate a public authority vouches for or
  the self-signed one whose SHA-256 the Owner trusted on the Storage page; a new address forgets it); [Storage.sql](../../database/Storage.sql) is the
  fresh physical-connection baseline, LibraryRoots are in [Library.sql](../../database/Library.sql).
- [StorageAccess.cs](../../src/Jularr.Backend/Service/Core/StorageAccess.cs) opens a connection's files for every feature:
  [LocalStorageFiles.cs](../../src/Jularr.Integrations/Logic/Storage/LocalStorageFiles.cs) for local paths and Docker volumes,
  [SmbStorageFiles.cs](../../src/Jularr.Integrations/Logic/Storage/SmbStorageFiles.cs) with
  [SmbShares.cs](../../src/Jularr.Integrations/Logic/Storage/SmbShares.cs) for direct SMB, and
  [StorageStreamServer.cs](../../src/Jularr.Integrations/Logic/Storage/StorageStreamServer.cs) for FFmpeg and ffprobe; tests `SmbStorageTests` with
  [FakeSmbStore.cs](../../tests/Jularr.Backend.Tests/FakeSmbStore.cs).
- [WakeOnLanClient.cs](../../src/Jularr.Integrations/Logic/Nas/WakeOnLanClient.cs) sends magic packets;
  [NasMountException.cs](../../src/Jularr.Integrations/Data/Nas/NasMountException.cs) is storage out of reach, never a deleted file.

## Frontend

- [Storage](../../src/Jularr.Frontend/Pages/Admin/Storage/Storage.cshtml.cs): the connections as
  [FeatureCard](../../src/Jularr.Frontend/Framework/Components/FeatureCard.cs)s in a card grid (state chip with icon,
  [SpaceBar](../../src/Jularr.Frontend/Framework/Components/SpaceBar.cs) of the last measured free space or the reason it is not online, last check,
  WOL, Check now, Wake, Edit, Remove in a confirm dialog; an offline connection stays), figures and the add card; the Storage entry's chip row adds
  Download storage.
  `?storageMountId=` (or `?protocol=` for a new one) is one connection's page: WOL, safe browsing, Synology and independently paged library paths.
  Tests: `StorageIntegrationsTests`, `FeatureCardTests`.

## Tests

- Storage configuration, protected credentials, Owner access, Setup/Admin forms, Docker volume lines, connection state and repeated checks:
  `StorageTests.cs`.
