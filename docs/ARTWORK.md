# Canonical Artwork (clean-start)

**P1 is the persistence/naming foundation. P2 adds NAS sidecar discovery on canonical library scans** (stacked branch). NAS export is available through an explicitly invoked Owner endpoint for provider poster/backdrop images in **compatible JPEG mode**. A queued **compatible JPEG** export now runs in the existing Operation worker following canonical file assignment, provider metadata refresh, and successful complete library scans. JXL encoding, a browser image endpoint, and the Admin UI remain separate packages.

- Every artwork candidate belongs to an existing canonical `Work`, optionally a `WorkUnit`. Variants for multiple locales never create additional Works.
- `WorkArtworks` stores manually pinned, NAS-sidecar, provider and embedded candidates with a stable source key. One pinned candidate is allowed per Work/unit/slot/locale.
- `ArtworkRenditions` is metadata for rebuildable, content-addressed disk variants under `DataPath/cache/artwork`. `ArtworkFallbacks` is capped at 64 KiB per Work/unit/slot/locale to allow a last-known-good small WebP image while the NAS is offline.
- `ArtworkSettings` separates NAS export (default compatible JPG/PNG; optional JXL plus compatible copies or JXL only) from the Jularr cache (default WebP, optional JXL). JXL progressive support must be checked in the actual browser/encoder. Never assume that transferring JXL bytes implies progressive rendering on every client.
- Source order without an explicit pin: manually supplied > valid NAS sidecar > provider > embedded. Pinning a provider candidate overrides that order. A missing/offline NAS is not evidence the file was deleted.
- No overwrite of unknown/custom sidecars, no write to read-only storage, no NAS wake for ordinary page reads. Existing provider `WorkMetadataFacts` remain readable until the canonical resolver is connected and all reads are migrated.
- `ArtworkFileNames` recognizes conservative Kodi/Plex-style basename conventions, including `season01-poster.jpg`. Other patterns need explicit identity checks before import.


## P3 – first cache/delivery slice (this stacked branch)

- The canonical scan now records source size and modification time for NAS image sidecars. When an original changes, the index records a changed `UpdatedAt` rather than re-encoding every cover on every pass.
- A bounded, single-encoder `ArtworkCache` generates a 384px WebP derivative and an optional tiny, at-most-64KiB WebP fallback. Files are written under persistent `DataPath/cache/artwork` using opaque content-derived keys and a create-only rename. The database stores the matching `ArtworkRenditions` metadata and fallback, not full-resolution images. Encoding never overwrites the NAS.
- Cache population runs in the system library scan, and immediately after a successful explicit/automatic NAS export. It has a per-scan cap of 100 candidates, a 12MiB source cap, a 32-megapixel decoded-image cap, and a configured byte budget. A corrupt or unsupported sidecar cannot delete last-known-good previews.
- `GET /api/v1/works/{workId}/artwork/{slotId}` authorizes a profile's visible Work and serves an already-cached WebP rendition (or database fallback) without a NAS request. Library and Continue prefer the local endpoint once cached. The existing provider poster remains in the response until a local image is actually available.
- The only encoder in this slice is **SkiaSharp WebP**. If the preferred cache format is JPEG XL, WebP is still generated as its necessary browser-compatibility fallback; **a native JXL cache file is not generated yet**. Progressive JPEG XL, JXL NAS export, dynamic format negotiation, cache garbage collection and ETags are separate follow-ups.
- Existing databases are not migrated. The `Artwork.sql` schema remains a fresh-install baseline only.

## Remaining slices

1. **P2 discovery (this branch):** after media inventory/identification, a bounded second filesystem walk recognizes conventional image names and matches each folder to exactly one existing Work via its assigned StoredFiles. Season-specific posters resolve through a canonical season WorkUnit. Accepted artwork is upserted by Work/WorkUnit/slot/source and reconciled only after a complete image scan. No image is stored as a playable StoredFile and no local file is modified. Ambiguous folders, invalid extensions, oversized paths and images over 20 MiB are not indexed. Images may need a rescan after manual media identification.
2. **P2 compatible export (this stacked branch):** `POST /api/v1/admin/works/{workId}/artwork/export` checks canonical file identity, a unique Work per target folder, NAS write access, configured export policy and all existing local sidecars before downloading provider Poster/Backdrop over public HTTPS. It uses a bounded SkiaSharp JPEG transcode and the existing atomic, no-overwrite NAS writer, then records the candidate immediately. It cannot replace custom artwork or write to a shared library root. JXL export modes explicitly reject until the encoder is implemented. No automatic background export yet.
3. **P2 automatic export (this branch):** existing `Operations` now includes an `ArtworkExport` Work job (deduplicated while queued/running). When metadata is saved, media is assigned, or a library scan succeeds, an export is enqueued only for Works with real writable files and poster/backdrop URLs. The System operation reads a Work identity, shares the same safe NAS writer as the manual endpoint, records a completed/failed operation, and can be retried via Activity. Interrupted jobs are requeued on restart. Unsupported JXL-only/dual modes never quietly write JPEG. The NAS is never woken for an ordinary cover view; no artwork is written during a read request.
4. **P2 remote delivery (next):** add safe NAS JXL with compatible fallback, more efficient export de-duplication and offline recovery, profile-scoped image reads.
5. **P3:** vetted libjxl encoder/decoder dependency, WebP/JXL renditions, bounded workers, cache limits and invalidation.
6. **P4:** authenticated image delivery, content negotiation, conditional caching, stable sizing, progressive JXL with browser fallback.
7. **P5:** version-checked Admin settings and manual image selection/protection.
8. **P6:** one Artwork resolver consumed by Home, Library, Discover, Detail, Reader, Player and all media types.
9. **P7:** end-to-end NAS/Plex/Jellyfin coexistence, browser and failure tests, performance benchmarks.

`database/Artwork.sql` is installed **only on a fresh database**, after the Library baseline. Existing installations are never altered automatically. An explicit, non-destructive database upgrade path must be approved and implemented before enabling this feature on existing instances.
