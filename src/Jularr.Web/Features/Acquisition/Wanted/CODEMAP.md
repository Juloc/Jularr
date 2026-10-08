# Wanted

Purpose: the one queue of concrete targets Jularr still needs, whatever the media type.

Canonical owners
- `WantedItem` (table `WantedItems`): membership only (Work, target kind, target id, created at), unique per target.
- `WantedReconciler`: the only writer. Set-based and idempotent: intent minus installed coverage is upserted, anything else for that scope is deleted.
- `IWantedSource` / `WantedAcquisitionService`: the shared 2-minute pass that prepares sources, follows downloads and imports.
- `WantedRequestSource` + `IWantedRequestDrafter`: opens the request that carries each wanted Work (`VideoWantedSource` does it for Movie and Tv through `VideoMonitoringService`).

Intent sources (read, never copied)
- Monitoring: effective state from `Features/Monitoring` (see its CODEMAP).
- Requests: not yet an intent source here; an open request only carries the search of a Work.
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

Persistence note: `WorkService.MergeWorksAsync` drops the absorbed Work's items.

Tests: `WantedReconcilerTests`, `WantedCoverageTests`, `VideoAdminSurfaceTests`, `MusicAcquisitionTests`.
