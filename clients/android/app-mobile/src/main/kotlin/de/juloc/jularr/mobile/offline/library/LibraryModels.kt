package de.juloc.jularr.mobile.offline.library

import de.juloc.jularr.mobile.offline.DownloadState
import de.juloc.jularr.mobile.offline.OfflineAccount

/**
 * Local record of one chapter selected for offline reading. [hash] is the
 * manifest hash this record targets (what the user asked for);
 * [verifiedHash] is the hash of the payload actually verified and stored on
 * disk, `null` until the chapter reaches [DownloadState.READY]. A manifest
 * hash change after [verifiedHash] was set means an update is available.
 */
data class LibraryChapterRecord(
    val ownerKey: String,
    val workId: String,
    val chapterId: String,
    val volumeId: String,
    val number: Int,
    val title: String,
    val hash: String,
    val verifiedHash: String? = null,
    val state: DownloadState = DownloadState.QUEUED,
    val failure: String? = null,
    val createdAtMs: Long = 0,
) {
    /** Whether the currently stored payload still matches what the manifest asks for. */
    val isUpToDate: Boolean
        get() = state == DownloadState.READY && verifiedHash == hash
}

/**
 * Local record of one book/novel kept offline. `null` [selectedChapterIds]
 * means "the whole book"; new chapters that appear in a later manifest are
 * then queued automatically. A non-null set is exactly the chapters the user
 * chose, so a later manifest addition is not downloaded until re-selected.
 */
data class LibraryBookRecord(
    val ownerKey: String,
    val workId: String,
    val title: String,
    val author: String?,
    val coverAssetUrl: String?,
    val contentVersion: String,
    val selectedChapterIds: Set<String>? = null,
    val wholeBook: Boolean = true,
    /** Content-addressed asset file names (cover/illustrations) this book downloaded, for cleanup on removal. */
    val assetFileNames: Set<String> = emptySet(),
    val createdAtMs: Long = 0,
) {
    fun wants(chapterId: String): Boolean =
        wholeBook || selectedChapterIds?.contains(chapterId) == true
}

/** One queued reading-position checkpoint. The queue holds at most one entry per work. */
data class LibraryPendingProgress(
    val workId: String,
    val chapterId: String,
    val positionPermille: Int,
    val anchorLanguage: String?,
    val anchorParagraphIndex: Int?,
    val anchorOffset: Int,
    val clientEventId: String,
    val clientTimestampUtc: String,
)

/** One queued bookmark add/edit/remove. The queue holds at most one entry per bookmark id. */
data class LibraryPendingBookmark(
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
    val clientEventId: String,
    val clientTimestampUtc: String,
)

data class LibrarySettings(
    val wifiOnly: Boolean = true,
)

data class LibrarySnapshot(
    val account: OfflineAccount? = null,
    val settings: LibrarySettings = LibrarySettings(),
    val books: List<LibraryBookRecord> = emptyList(),
    val chapters: List<LibraryChapterRecord> = emptyList(),
    val pendingProgress: Map<String, List<LibraryPendingProgress>> = emptyMap(),
    val pendingBookmarks: Map<String, List<LibraryPendingBookmark>> = emptyMap(),
) {
    /** Books of the signed-in account for [origin]; empty while signed out or for another server. */
    fun accessibleBooks(origin: String): List<LibraryBookRecord> {
        val current = account?.takeIf { it.signedIn && it.origin == origin } ?: return emptyList()
        return books.filter { it.ownerKey == current.ownerKey }
    }

    fun chaptersOf(ownerKey: String, workId: String): List<LibraryChapterRecord> =
        chapters.filter { it.ownerKey == ownerKey && it.workId == workId }
}

/** Aggregate download state of a book, derived from its chapters (mirrors the PWA finalization rule). */
enum class BookDownloadStatus {
    QUEUED,
    DOWNLOADING,
    PAUSED,
    FAILED,
    AVAILABLE,
    UPDATE_AVAILABLE,
}

object LibraryBookStatus {
    /** A book is "available" only once every wanted chapter is verified up to date; see docs/OFFLINE_LIBRARY.md. */
    fun of(chapters: List<LibraryChapterRecord>): BookDownloadStatus {
        if (chapters.isEmpty()) {
            return BookDownloadStatus.QUEUED
        }
        if (chapters.any { it.state == DownloadState.DOWNLOADING }) {
            return BookDownloadStatus.DOWNLOADING
        }
        if (chapters.all { it.isUpToDate }) {
            return BookDownloadStatus.AVAILABLE
        }
        if (chapters.any { it.state == DownloadState.FAILED }) {
            return BookDownloadStatus.FAILED
        }
        if (chapters.any { it.state == DownloadState.PAUSED }) {
            return BookDownloadStatus.PAUSED
        }
        if (chapters.any { it.state == DownloadState.QUEUED }) {
            return BookDownloadStatus.QUEUED
        }
        // Every chapter is READY but at least one verifiedHash is stale.
        return BookDownloadStatus.UPDATE_AVAILABLE
    }
}
