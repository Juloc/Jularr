package de.juloc.jularr.mobile.offline.library

import de.juloc.jularr.core.model.OfflineLibraryBookmarkEvent
import de.juloc.jularr.core.model.OfflineLibraryBookmarkResult
import de.juloc.jularr.core.model.OfflineLibraryProgressEvent
import de.juloc.jularr.core.model.OfflineLibraryProgressResult

/**
 * Local sync queue for reading-state changes that could not reach the server
 * yet. Mirrors `OfflineProgressQueue` (bounded offline playback, #341): these
 * are pure queue-bookkeeping functions, not a progress/bookmark store. The
 * server applies the actual forward-only/last-writer-wins rules
 * (`OfflineLibrarySyncRules`, see docs/OFFLINE_LIBRARY.md); the client only
 * has to replay events and adopt whatever canonical value the server answers
 * with, which is why `applyReconciliation` below always adopts the server's
 * returned position/outcome rather than computing one locally.
 */
object LibraryProgressQueue {
    /** Keeps at most one pending checkpoint per work: the latest local write wins. */
    fun record(
        queue: List<LibraryPendingProgress>,
        checkpoint: LibraryPendingProgress,
    ): List<LibraryPendingProgress> =
        queue.filterNot { it.workId == checkpoint.workId } + checkpoint

    /**
     * Removes checkpoints the server has answered. An entry that changed
     * (a newer local event id) after the batch was taken stays queued.
     */
    fun acknowledge(
        queue: List<LibraryPendingProgress>,
        sent: List<LibraryPendingProgress>,
        answeredWorkIds: Set<String>,
    ): List<LibraryPendingProgress> =
        queue.filterNot { entry -> entry.workId in answeredWorkIds && entry in sent }

    fun batch(queue: List<LibraryPendingProgress>, limit: Int): List<LibraryPendingProgress> =
        queue.take(limit)

    fun toEvent(entry: LibraryPendingProgress) = OfflineLibraryProgressEvent(
        clientEventId = entry.clientEventId,
        workId = entry.workId,
        chapterId = entry.chapterId,
        positionPermille = entry.positionPermille.coerceIn(0, 1000),
        anchorLanguage = entry.anchorLanguage,
        anchorParagraphIndex = entry.anchorParagraphIndex,
        anchorOffset = entry.anchorOffset.coerceAtLeast(0),
        clientTimestampUtc = entry.clientTimestampUtc,
    )
}

object LibraryBookmarkQueue {
    /** Keeps at most one pending event per bookmark id: the latest local write wins. */
    fun record(
        queue: List<LibraryPendingBookmark>,
        event: LibraryPendingBookmark,
    ): List<LibraryPendingBookmark> =
        queue.filterNot { it.bookmarkId == event.bookmarkId } + event

    fun acknowledge(
        queue: List<LibraryPendingBookmark>,
        sent: List<LibraryPendingBookmark>,
        answeredBookmarkIds: Set<String>,
    ): List<LibraryPendingBookmark> =
        queue.filterNot { entry -> entry.bookmarkId in answeredBookmarkIds && entry in sent }

    fun batch(queue: List<LibraryPendingBookmark>, limit: Int): List<LibraryPendingBookmark> =
        queue.take(limit)

    fun toEvent(entry: LibraryPendingBookmark) = OfflineLibraryBookmarkEvent(
        clientEventId = entry.clientEventId,
        bookmarkId = entry.bookmarkId,
        type = entry.type,
        workId = entry.workId,
        chapterId = entry.chapterId,
        language = entry.language,
        positionPermille = entry.positionPermille.coerceIn(0, 1000),
        paragraphIndex = entry.paragraphIndex,
        characterOffset = entry.characterOffset.coerceAtLeast(0),
        anchorText = entry.anchorText,
        label = entry.label,
        style = entry.style,
        color = entry.color,
        clientTimestampUtc = entry.clientTimestampUtc,
    )
}

/** Applies a batch's outcomes: the server's returned values are always the new local truth. */
fun List<OfflineLibraryProgressResult>.workIds(): Set<String> = mapTo(HashSet()) { it.workId }

fun List<OfflineLibraryBookmarkResult>.bookmarkIds(): Set<String> = mapTo(HashSet()) { it.bookmarkId }
