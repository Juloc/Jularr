# Wanted

Purpose: the one queue of concrete targets Jularr still needs, whatever the media type.

Canonical owners
- `WantedItem` (table `WantedItems`): membership only (Work, target kind, target id, created at), unique per target.
- `WantedReconciler`: the only writer. Set-based and idempotent: intent minus installed coverage is upserted, anything else for that scope is deleted.
- `IWantedSource` / `WantedAcquisitionService`: the shared 2-minute pass that prepares sources, follows downloads and imports.

Intent sources (read, never copied)
- Monitoring: effective state from `Features/Monitoring` (see its CODEMAP).
- Requests: not yet an intent source here; an open request only carries the search of a Work.
- Why a row is wanted and whether it is missing or an upgrade is derived on read, not stored.

Flow
monitoring decisions + relation sources -> `WantedReconciler.ReconcileAsync(workId?)` -> `WantedItems`
-> `VideoWantedSource` opens or reopens the request that carries each Work with items
-> `VideoAcquisitionEngine` reads `TargetIdsAsync` for its episodes (reconciling its Work first).

Media coverage today
- Movie (Work target) and Series (Episode targets, only once aired): reconciled and consumed.
- Anime, Music, Book, Manga, Light Novel, Audiobook: still on their own sources (`AnimeWantedSource`, `MusicWantedSource`, `MonitoringWantedSource`).

Installed coverage: a video `MediaAsset` backed by a `StoredFile` (SQL in `WantedReconciler`).

Persistence changes keep Work deletion safe: `WorkService.MergeWorksAsync` drops the absorbed Work's items.

Tests: `WantedReconcilerTests`, `VideoAdminSurfaceTests`, `RequestToPlayTvTests`.
