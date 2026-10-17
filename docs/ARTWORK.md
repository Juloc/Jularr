# Canonical Artwork (clean-start)

**P1 is the persistence/naming foundation. P2 adds NAS sidecar discovery on canonical library scans** (stacked branch). Actual NAS export, JXL encoding, a browser image endpoint, and Admin editing remain separate packages.

- Every artwork candidate belongs to an existing canonical `Work`, optionally a `WorkUnit`. Variants for multiple locales never create additional Works.
- `WorkArtworks` stores manually pinned, NAS-sidecar, provider and embedded candidates with a stable source key. One pinned candidate is allowed per Work/unit/slot/locale.
- `ArtworkRenditions` is metadata for rebuildable, content-addressed disk variants under `DataPath/cache/artwork`. `ArtworkFallbacks` is capped at 64 KiB per Work/unit/slot/locale to allow a last-known-good small WebP image while the NAS is offline.
- `ArtworkSettings` separates NAS export (default compatible JPG/PNG; optional JXL plus compatible copies or JXL only) from the Jularr cache (default WebP, optional JXL). JXL progressive support must be checked in the actual browser/encoder. Never assume that transferring JXL bytes implies progressive rendering on every client.
- Source order without an explicit pin: manually supplied > valid NAS sidecar > provider > embedded. Pinning a provider candidate overrides that order. A missing/offline NAS is not evidence the file was deleted.
- No overwrite of unknown/custom sidecars, no write to read-only storage, no NAS wake for ordinary page reads. Existing provider `WorkMetadataFacts` remain readable until the canonical resolver is connected and all reads are migrated.
- `ArtworkFileNames` recognizes conservative Kodi/Plex-style basename conventions, including `season01-poster.jpg`. Other patterns need explicit identity checks before import.

## Remaining slices

1. **P2 discovery (this branch):** after media inventory/identification, a bounded second filesystem walk recognizes conventional image names and matches each folder to exactly one existing Work via its assigned StoredFiles. Season-specific posters resolve through a canonical season WorkUnit. Accepted artwork is upserted by Work/WorkUnit/slot/source and reconciled only after a complete image scan. No image is stored as a playable StoredFile and no local file is modified. Ambiguous folders, invalid extensions, oversized paths and images over 20 MiB are not indexed. Images may need a rescan after manual media identification.
2. **P2 export and image reads (next):** safe, atomic JPEG/PNG/JXL NAS export and profile-scoped original/cache reads; queued recovery after outages.
3. **P3:** vetted libjxl encoder/decoder dependency, WebP/JXL renditions, bounded workers, cache limits and invalidation.
4. **P4:** authenticated image delivery, content negotiation, conditional caching, stable sizing, progressive JXL with browser fallback.
5. **P5:** version-checked Admin settings and manual image selection/protection.
6. **P6:** one Artwork resolver consumed by Home, Library, Discover, Detail, Reader, Player and all media types.
7. **P7:** end-to-end NAS/Plex/Jellyfin coexistence, browser and failure tests, performance benchmarks.

`database/Artwork.sql` is installed **only on a fresh database**, after the Library baseline. Existing installations are never altered automatically. An explicit, non-destructive database upgrade path must be approved and implemented before enabling this feature on existing instances.
