package de.juloc.jularr.core.model

/**
 * Client mirror of the server's offline Book/Novel library contract
 * (`/api/client/v1/offline-library` endpoints, capability `offlineLibrary`,
 * see `docs/OFFLINE_LIBRARY.md`). Books and Novels share one server model; these
 * types are generic over "work -> volume -> chapter" and carry no anime/media
 * fields, mirroring `Features/OfflineLibrary` on the server exactly.
 */
data class ClientOfflineLibraryVolume(
    val volumeId: String,
    val number: Int,
    val title: String?,
    val kind: String,
    val coverAssetUrl: String?,
)

/** One chapter reference inside a manifest; [hash] is the differential-download key. */
data class ClientOfflineLibraryChapterRef(
    val chapterId: String,
    val volumeId: String,
    val number: Int,
    val title: String,
    val hash: String,
    val hasContent: Boolean,
    val hasTranslation: Boolean,
)

data class ClientOfflineLibraryManifest(
    val workId: String,
    val schemaVersion: Int,
    val contentVersion: String,
    val title: String,
    val author: String?,
    val description: String?,
    val coverAssetUrl: String?,
    val issuedAtUtc: String,
    val volumes: List<ClientOfflineLibraryVolume>,
    val chapters: List<ClientOfflineLibraryChapterRef>,
)

/** Manifest plus the exact JSON it was parsed from, so it can be stored and re-read offline. */
data class OfflineLibraryManifestPackage(
    val manifest: ClientOfflineLibraryManifest,
    val json: String,
)

data class ClientOfflineLibraryInlineRun(
    val text: String,
    val ruby: String?,
    val emphasis: Boolean,
    val strong: Boolean,
)

data class ClientOfflineLibraryContentBlock(
    val kind: String,
    val level: Int,
    val runs: List<ClientOfflineLibraryInlineRun>,
    val imageAssetUrl: String?,
    val imageAlt: String?,
)

data class ClientOfflineLibraryTranslation(
    val targetLanguage: String,
    val text: String,
)

/**
 * [hash] is repeated here (equal to the manifest's ref for this chapter) so a
 * client can detect a version race: the chapter changed again after the
 * manifest was fetched but before this payload landed.
 */
data class ClientOfflineLibraryChapterPayload(
    val chapterId: String,
    val workId: String,
    val volumeId: String,
    val number: Int,
    val title: String,
    val hash: String,
    val originalText: String,
    val blocks: List<ClientOfflineLibraryContentBlock>,
    val translations: List<ClientOfflineLibraryTranslation>,
)

/** Payload plus the exact JSON it was parsed from, so it can be stored and re-read offline. */
data class OfflineLibraryChapterPackage(
    val payload: ClientOfflineLibraryChapterPayload,
    val json: String,
)

/** One offline reading-position checkpoint for `POST /offline-library/sync`. */
data class OfflineLibraryProgressEvent(
    val clientEventId: String,
    val workId: String,
    val chapterId: String,
    val positionPermille: Int,
    val anchorLanguage: String?,
    val anchorParagraphIndex: Int?,
    val anchorOffset: Int,
    val clientTimestampUtc: String,
)

/** [type] is `"upsert"` (add/edit) or `"remove"`; the bookmark id is client-supplied. */
data class OfflineLibraryBookmarkEvent(
    val clientEventId: String,
    val bookmarkId: String,
    val type: String,
    val workId: String,
    val chapterId: String,
    val language: String?,
    val positionPermille: Int,
    val paragraphIndex: Int?,
    val characterOffset: Int,
    val anchorText: String?,
    val label: String?,
    val style: String?,
    val color: String?,
    val clientTimestampUtc: String,
)

data class OfflineLibraryProgressResult(
    val clientEventId: String,
    val workId: String,
    val outcome: String,
    val positionPermille: Int?,
)

data class OfflineLibraryBookmarkResult(
    val clientEventId: String,
    val bookmarkId: String,
    val outcome: String,
)

data class OfflineLibrarySyncResult(
    val progress: List<OfflineLibraryProgressResult>,
    val bookmarks: List<OfflineLibraryBookmarkResult>,
)
