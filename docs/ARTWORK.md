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
- The WebP encoder uses SkiaSharp. The next stacked slice adds a *real* JPEG XL rendition using the bundled libjxl `cjxl` executable, with `--progressive` when enabled. Original JXL sidecars are decoded with `djxl` into the same bounded WebP fallback pipeline. JXL cache generation is best-effort; missing native tools do not interrupt the WebP path. Automatic browser format selection, cache garbage collection and ETags are separate follow-ups.
- Existing databases are not migrated. The `Artwork.sql` schema remains a fresh-install baseline only.

## Remaining slices

1. **P2 discovery (this branch):** after media inventory/identification, a bounded second filesystem walk recognizes conventional image names and matches each folder to exactly one existing Work via its assigned StoredFiles. Season-specific posters resolve through a canonical season WorkUnit. Accepted artwork is upserted by Work/WorkUnit/slot/source and reconciled only after a complete image scan. No image is stored as a playable StoredFile and no local file is modified. Ambiguous folders, invalid extensions, oversized paths and images over 20 MiB are not indexed. Images may need a rescan after manual media identification.
2. **P2 compatible export (this stacked branch):** `POST /api/v1/admin/works/{workId}/artwork/export` checks canonical file identity, a unique Work per target folder, NAS write access, configured export policy and all existing local sidecars before downloading provider Poster/Backdrop over public HTTPS. It uses a bounded SkiaSharp JPEG transcode and the existing atomic, no-overwrite NAS writer, then records the candidate immediately. It cannot replace custom artwork or write to a shared library root. JXL export modes explicitly reject until the encoder is implemented. No automatic background export yet.
3. **P2 automatic export (this branch):** existing `Operations` now includes an `ArtworkExport` Work job (deduplicated while queued/running). When metadata is saved, media is assigned, or a library scan succeeds, an export is enqueued only for Works with real writable files and poster/backdrop URLs. The System operation reads a Work identity, shares the same safe NAS writer as the manual endpoint, records a completed/failed operation, and can be retried via Activity. Interrupted jobs are requeued on restart. Unsupported JXL-only/dual modes never quietly write JPEG. The NAS is never woken for an ordinary cover view; no artwork is written during a read request.
4. **P2 JXL NAS export (this stacked PR):** the Owner endpoint and background worker honor all three NAS modes. Compatible emits JPEG, JXL-only emits a real `.jxl`, and Dual emits both JPEG and JXL. The libjxl encoder consumes the already-validated provider JPEG, decodes and re-encodes pixels with `--lossless_jpeg=0` so progressive mode and visually-lossless quality are effective. The `ProgressiveJxl` setting is honored. Source conflict checks still protect **all** manually placed artworks and writes remain atomic/create-only per image file. If native JXL encoding is unavailable the operation fails visibly rather than silently substituting JPEG. Dual-export file writes are not an atomic multi-file transaction and require operator follow-up on partial write failures.
5. **P2 follow-up:** improve export de-duplication, recovery after transient NAS failures and optional provenance for safely filling missing parts of interrupted dual exports.
6. **P3:** vetted libjxl encoder/decoder dependency, WebP/JXL renditions, bounded workers, cache limits and invalidation.
7. **P4:** authenticated image delivery, content negotiation, conditional caching, stable sizing, progressive JXL with browser fallback.
8. **P5:** version-checked Admin settings and manual image selection/protection.
9. **P6:** one Artwork resolver consumed by Home, Library, Discover, Detail, Reader, Player and all media types.
10. **P7:** end-to-end NAS/Plex/Jellyfin coexistence, browser and failure tests, performance benchmarks.

`database/Artwork.sql` is installed **only on a fresh database**, after the Library baseline. Existing installations are never altered automatically. An explicit, non-destructive database upgrade path must be approved and implemented before enabling this feature on existing instances.

## P3b – Progressive JPEG XL codec and endpoint

- The Docker runtime installs Debian's `libjxl-tools` (`cjxl`, `djxl`). Input is a bounded, resized PNG generated through SkiaSharp; `cjxl --progressive` encodes a progressively decodable JXL bitstream. Encoder effort and threads are bounded for low-power CPUs.
- If `ArtworkSettings.CacheFormatId=2`, cache warming writes *both* WebP and real JXL under content-derived keys, storing separate `ArtworkRenditions` rows (formats 3 and 4). The stored progressive flag and encoder version prevent stale variant reuse after a configuration change.
- Authorized `GET /api/v1/works/{workId}/artwork/{slotId}.jxl` serves `image/jxl`, independently of the WebP `GET /api/v1/works/{workId}/artwork/{slotId}`. Both endpoints read persistent local storage only. The frontend must opt into a JXL source only after an actual rendition exists; this PR does not yet activate unconditional `<picture>` sources.
- Native JXL progressive *codestream* support does not by itself guarantee that a browser progressively renders partial downloads. Browser support and first-frame behavior require live E2E verification. On unsupported browsers Jularr's WebP remains the fallback.
- The separate JXL file is not a renamed WebP file. Native codec failures leave the WebP rendition usable. NAS JXL support is covered by the next stacked export PR; runtime settings UI remains outstanding.

## P4a – Browser JXL selection (stacked PR)

- Library and Continue return an optional `JxlPosterUrl` only when the local artwork index has **both** a native JPEG XL rendition and a corresponding WebP rendition for the same Work/slot. Other pages and unknown/provider-only artwork continue using their existing URL.
- The `MediaImage` primitive writes `<picture><source type="image/jxl" srcset="…"><img src="…"></picture>` only when Jularr has a verified indexed JXL variant. The `img` always keeps explicit width and height to prevent layout shifts; it remains the WebP fallback.
- When the cache is set to WebP or a native codec is unavailable, no JXL source is advertised. Image source URL validation applies to both variants, and all image endpoints remain profile-authorized.
- This does not assert browser incremental-render support; the progressive JXL bitstream must be validated in browser E2E. It does not create a JXL rendition for remote provider-only artwork.

## P4b – NAS JPEG XL export (stacked PR)

- Manual and queued NAS exports now respect `NasModeId=1` (compatible JPG), `2` (JXL + JPG), and `3` (JXL-only). They use the same canonical Work/folder ownership checks and never replace an existing sidecar.
- A bounded native `cjxl` encoder writes real JPEG XL data using an explicit pixel re-encode, with `ProgressiveJxl` from settings, visually-lossless distance, capped worker threads and native failure reporting.
- In dual mode, JXL is written before the compatible JPEG. A partial file write remains visible as an incomplete export; the current no-overwrite rule deliberately does not guess whether an existing file was generated by Jularr or manually supplied.
- This slice does not yet provide the Admin UI for changing settings, background retry policies for mixed partial dual export, or upgrades for an existing PostgreSQL database.
