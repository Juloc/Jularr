# Wanted

Purpose: the one queue of concrete targets Jularr still needs, whatever the media type.

Canonical owners
- `WantedItem` (table `WantedItems`): membership only (Work, target kind, target id, created at), unique per target.
- `WantedReconciler`: the only writer. Set-based and idempotent: intent minus installed coverage is upserted, anything else for that scope is deleted. All statements live in `WantedSql` (named parameters only).
- `WantedReconcileState`: when the whole library was last reconciled (singleton); every source of a pass asks, only the first one in 10 minutes runs. A Work is reconciled where it changes (request approval, executor), the full run only catches unannounced changes.
- `IWantedSource` / `WantedAcquisitionService`: the shared 2-minute pass that prepares sources, follows downloads and imports.
- `RequestIntent` (table `RequestTargets`): what a request somebody made names (the Work or single episodes), recorded by `AcquisitionRequestService` in the same transaction that creates or approves the request (`ChangeAndRecordAsync`). A request the Wanted pass or Monitoring opened records nothing; a video request without a payload is the whole title, one with a payload but no choice asks for nothing. Rows count while the request is open. `VideoMonitoringService.ReconcileAsync` keeps a request alive with Monitoring off when it has rows (`HasAsync`).
- Migration `RequestTargetsRepair` keeps only provable intent for existing requests (a Movie or TV request that still carries its choice or has no payload); a choice applied earlier is not recoverable and stays Monitoring-driven.
- `WantedRequestSource` + `IWantedRequestDrafter`: opens the request that carries each wanted Work (`VideoWantedSource` does it for Movie and Tv through `VideoMonitoringService`).

Intent sources (read, never copied)
- Monitoring: effective state from `Features/Monitoring` (see its CODEMAP).
- Requests: open approved requests (`RequestTargets`) want their target even when Monitoring is off or switched off later; a pending request wants nothing.
- Why a row is wanted and whether it is missing or an upgrade is derived on read, not stored: a row whose target the library holds is an upgrade, one it lacks is missing.

Upgrades
- `IUpgradeAssessor` per acquisition kind, naming the target kind it judges (`VideoUpgradeAssessor` for Movie and each episode, `MusicUpgradeAssessor` for an album, `BookUpgradeAssessor` for the best file format of a Book) tells which held targets the profile (`UpgradePolicy`) still wants better versions of; `UpgradeAssessors` is the registry (Book and Audiobook share a Work type, so the key is kind plus target kind, never the Work type). Other types have none, so their installed targets leave the queue.
- `WantedReconciler.ReconcileAsync(workId)` runs the SQL reconcile (held rows of assessed types are kept, missing ones queued) and then `SyncUpgrades` with the assessor's answer; the full run skips the assessment.
- `UpgradeWantedSource` (Movie, Tv, Music, Book, bounded page, cursor in `UpgradeScanState`): reconciles the held Works of a page and continues the Completed request of a Work that has rows in one conditional write (search state and status together; tried releases stay remembered). The scan runs hourly, at the next pass while the library is unfinished, and starts over at once when the profile file changed. `WorksWithoutOpenRequest` lists Works that miss something and held Works that were never requested (opened by the Video/Request sources), so an upgrade never opens a second request.
- Not defined yet: upgrades of Light Novel and Manga (no installed quality rule), and Audiobook (the edition has no Monitoring decision, so it is wanted only while a request names it).
- Audiobook: an Audiobook request names the audio edition of its Book Work (target kind Edition, the Work's id as target id); it is installed once an audiobook is linked to the Work with files. `WorksWithoutOpenRequest` lists edition rows only for Audiobook and never for Book.

Flow
monitoring decisions + relation sources -> `WantedReconciler.ReconcileAsync(workId?)` -> `WantedItems`
-> source opens or reopens the request that carries each Work with items (a latest request that is open, failed or rejected is respected; a completed one is not)
-> the engine reads `TargetIdsAsync` for its units (`VideoAcquisitionEngine` for episodes, reconciling its Work first).

Installed coverage (SQL in `WantedReconciler`)
- Movie, Episode: a video `MediaAsset` backed by a `StoredFile`. Music: an audio asset; an album is only wanted once released.
- Book: a `BookFile` of a linked edition. Light Novel: a `NovelVolume`. Manga: a `MangaChapter` (through `WorkSourceLink`).
- Not reconciled yet: Audiobook monitoring as its own decision. Reading targets are whole Works until volume and chapter coverage is mapped.

Media adapters: `MusicRequestDrafter` (release group + payload), `IdentityRequestDrafter` (primary provider identity).

Cost: a single-Work reconcile is index-driven (0.7 ms on 40k episodes), the full run is one statement (about 160 ms on 4600 Works and 36k episodes).

Persistence note: `WorkService.MergeWorksAsync` drops the absorbed Work's items.

Tests: `WantedReconcilerTests` (incl. the SQL-ownership guard), `VideoUpgradeTests` (queue and scan), `MusicAcquisitionTests`, `CanonicalMonitoringTests` (atomic commands), `RequestTargetsMigrationTests`, `WantedCoverageTests`, `VideoAdminSurfaceTests`, `MusicAcquisitionTests`.
