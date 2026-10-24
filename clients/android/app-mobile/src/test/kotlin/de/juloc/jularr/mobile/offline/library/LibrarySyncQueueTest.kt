package de.juloc.jularr.mobile.offline.library

import de.juloc.jularr.core.model.OfflineLibraryBookmarkResult
import de.juloc.jularr.core.model.OfflineLibraryProgressResult
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

class LibraryProgressQueueTest {
    private fun entry(
        workId: String = "work-1",
        chapterId: String = "chapter-1",
        positionPermille: Int = 100,
        clientEventId: String = "event-1",
    ) = LibraryPendingProgress(
        workId = workId,
        chapterId = chapterId,
        positionPermille = positionPermille,
        anchorLanguage = "ja",
        anchorParagraphIndex = 0,
        anchorOffset = 0,
        clientEventId = clientEventId,
        clientTimestampUtc = "2026-09-26T00:00:00Z",
    )

    @Test
    fun recordKeepsAtMostOneEntryPerWork() {
        val queue = LibraryProgressQueue.record(emptyList(), entry(positionPermille = 100))
        val updated = LibraryProgressQueue.record(queue, entry(positionPermille = 250, clientEventId = "event-2"))

        assertEquals(1, updated.size)
        assertEquals(250, updated.single().positionPermille)
    }

    @Test
    fun recordingTheSameEventTwiceIsIdempotent() {
        val checkpoint = entry()
        val once = LibraryProgressQueue.record(emptyList(), checkpoint)
        val twice = LibraryProgressQueue.record(once, checkpoint)

        assertEquals(once, twice)
    }

    @Test
    fun differentWorksQueueIndependently() {
        val queue = LibraryProgressQueue.record(emptyList(), entry(workId = "work-1"))
        val updated = LibraryProgressQueue.record(queue, entry(workId = "work-2"))

        assertEquals(2, updated.size)
    }

    @Test
    fun acknowledgeRemovesOnlyEntriesThatWereSentAndAnswered() {
        val sent = entry(clientEventId = "sent")
        val queue = listOf(sent)

        val cleared = LibraryProgressQueue.acknowledge(queue, listOf(sent), setOf("work-1"))
        assertTrue(cleared.isEmpty())
    }

    @Test
    fun acknowledgeKeepsAnEntryThatChangedAfterTheBatchWasTaken() {
        val sent = entry(positionPermille = 100, clientEventId = "sent")
        val changed = entry(positionPermille = 500, clientEventId = "changed")
        val queue = listOf(changed)

        val remaining = LibraryProgressQueue.acknowledge(queue, listOf(sent), setOf("work-1"))
        assertEquals(listOf(changed), remaining)
    }

    @Test
    fun batchIsBoundedByLimit() {
        val queue = (1..5).map { entry(workId = "work-$it") }
        assertEquals(3, LibraryProgressQueue.batch(queue, 3).size)
    }

    @Test
    fun toEventClampsPositionAndOffset() {
        val event = LibraryProgressQueue.toEvent(entry(positionPermille = 5_000).copy(anchorOffset = -4))
        assertEquals(1000, event.positionPermille)
        assertEquals(0, event.anchorOffset)
    }
}

class LibraryBookmarkQueueTest {
    private fun entry(
        bookmarkId: String = "bookmark-1",
        type: String = "upsert",
        clientEventId: String = "event-1",
    ) = LibraryPendingBookmark(
        bookmarkId = bookmarkId,
        type = type,
        workId = "work-1",
        chapterId = "chapter-1",
        language = "ja",
        positionPermille = 100,
        paragraphIndex = 0,
        characterOffset = 0,
        anchorText = null,
        label = null,
        style = null,
        color = null,
        clientEventId = clientEventId,
        clientTimestampUtc = "2026-09-26T00:00:00Z",
    )

    @Test
    fun recordKeepsAtMostOneEntryPerBookmark() {
        val queue = LibraryBookmarkQueue.record(emptyList(), entry(type = "upsert"))
        val updated = LibraryBookmarkQueue.record(queue, entry(type = "remove", clientEventId = "event-2"))

        assertEquals(1, updated.size)
        assertEquals("remove", updated.single().type)
    }

    @Test
    fun recordingTheSameEventTwiceIsIdempotent() {
        val event = entry()
        val once = LibraryBookmarkQueue.record(emptyList(), event)
        val twice = LibraryBookmarkQueue.record(once, event)

        assertEquals(once, twice)
    }

    @Test
    fun acknowledgeRemovesOnlyEntriesThatWereSentAndAnswered() {
        val sent = entry(clientEventId = "sent")
        val cleared = LibraryBookmarkQueue.acknowledge(listOf(sent), listOf(sent), setOf("bookmark-1"))
        assertTrue(cleared.isEmpty())
    }

    @Test
    fun acknowledgeKeepsAnEntryEditedAfterTheBatchWasTaken() {
        val sent = entry(clientEventId = "sent")
        val edited = entry(type = "remove", clientEventId = "edited")
        val remaining = LibraryBookmarkQueue.acknowledge(listOf(edited), listOf(sent), setOf("bookmark-1"))

        assertEquals(listOf(edited), remaining)
    }
}

class LibrarySyncResultSetsTest {
    @Test
    fun workIdsAndBookmarkIdsExtractIdSets() {
        val progress = listOf(
            OfflineLibraryProgressResult("e1", "work-1", "applied", 100),
            OfflineLibraryProgressResult("e2", "work-2", "unchanged", null),
        )
        val bookmarks = listOf(OfflineLibraryBookmarkResult("e3", "bookmark-1", "applied"))

        assertEquals(setOf("work-1", "work-2"), progress.workIds())
        assertEquals(setOf("bookmark-1"), bookmarks.bookmarkIds())
    }
}
