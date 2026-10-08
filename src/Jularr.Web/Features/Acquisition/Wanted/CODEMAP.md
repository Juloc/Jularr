# Wanted

Purpose: the one queue of concrete targets Jularr still needs, whatever the media type.

Canonical owners
- `WantedItem` (table `WantedItems`): membership only (Work, target kind, target id, created at), unique per target.
- `WantedReconciler`: the only writer. Set-based and idempotent: intent minus installed coverage is upserted, anything else for that scope is deleted. All statements live in `WantedSql` (named parameters only).
- `WantedReconcileState`: when the whole library was last reconciled (singleton); every source of a pass asks, only the first one in 10 minutes runs. A Work is reconciled where it changes (request approval, executor), the full run only catches unannounced changes.
- `IWantedSource` / `WantedAcquisitionService`: the shared 2-minute pass that prepares sources, follows downloads and imports.
- `RequestIntent` (table `RequestTargets`): what an approved request names (the Work or single episodes), recorded when the request is approved and executed; rows end with the request.
- `WantedRequestSource` + `IWantedRequestDrafter`: opens the request that carries each wanted Work (`VideoWantedSource` does it for Movie and Tv through `VideoMonitoringService`).

Intent sources (read, never copied)
- Monitoring: effective state from `Features/Monitoring` (see its CODEMAP).
- Requests: open approved requests (`RequestTargets`) want their target even when Monitoring is off or switched off later; a pending request wants nothing.
- Why a row is wanted and whether it is missing or an upgrade is derived on read, not stored.

Flow
monitoring decisions + relation sources -> `WantedReconciler.ReconcileAsync(workId?)` -> `WantedItems`
-> source opens or reopens the request that carries each Work with items (a latest request that is open, failed or rejected is respected; a completed one is not)
-> the engine reads `TargetIdsAsync` for its units (`VideoAcquisitionEngine` for episodes, reconciling its Work first).

Installed coverage (SQL in `WantedReconciler`)
- Movie, Episode: a video `MediaAsset` backed by a `StoredFile`. Music: an audio asset; an album is only wanted once released.
- Book: a `BookFile` of a linked edition. Light Novel: a `NovelVolume`. Manga: a `MangaChapter` (through `WorkSourceLink`).
- Not reconciled yet: Anime (`AnimeWantedSource`), Audiobook. Reading targets are whole Works until volume and chapter coverage is mapped.

Media adapters: `MusicRequestDrafter` (release group + payload), `IdentityRequestDrafter` (primary provider identity).

Cost: a single-Work reconcile is index-driven (0.7 ms on 40k episodes), the full run is one statement (about 160 ms on 4600 Works and 36k episodes).

Persistence note: `WorkService.MergeWorksAsync` drops the absorbed Work's items.

Tests: `WantedReconcilerTests` (incl. the SQL-ownership guard), `CanonicalMonitoringTests` (atomic commands), `WantedCoverageTests`, `VideoAdminSurfaceTests`, `MusicAcquisitionTests`.
