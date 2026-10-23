package de.juloc.jularr.mobile.offline

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class OfflineProgressQueueTest {
    @Test
    fun keepsOneEntryPerEpisodeWithTheLatestPosition() {
        var queue = emptyList<PendingProgress>()
        queue = OfflineProgressQueue.record(queue, PendingProgress("a", 60_000, 1_400_000, false))
        queue = OfflineProgressQueue.record(queue, PendingProgress("b", 90_000, null, false))
        queue = OfflineProgressQueue.record(queue, PendingProgress("a", 120_000, null, false))

        assertEquals(2, queue.size)
        val a = queue.single { it.episodeId == "a" }
        assertEquals(120_000, a.positionMs)
        assertEquals(1_400_000L, a.durationMs)
    }

    @Test
    fun completionStaysStickyInTheQueue() {
        var queue = OfflineProgressQueue.record(emptyList(), PendingProgress("a", 1_400_000, 1_400_000, true))
        queue = OfflineProgressQueue.record(queue, PendingProgress("a", 30_000, 1_400_000, false))

        assertTrue(queue.single().completed)
    }

    @Test
    fun acknowledgeKeepsEntriesThatChangedWhileTheRequestWasInFlight() {
        val sentA = PendingProgress("a", 60_000, null, false)
        val sentB = PendingProgress("b", 90_000, null, false)
        val newerA = PendingProgress("a", 200_000, null, false)
        val queue = listOf(newerA, sentB)

        val remaining = OfflineProgressQueue.acknowledge(queue, listOf(sentA, sentB), setOf("a", "b"))

        assertEquals(listOf(newerA), remaining)
    }

    @Test
    fun acknowledgeKeepsUnansweredEntries() {
        val sent = PendingProgress("a", 60_000, null, false)

        assertEquals(listOf(sent), OfflineProgressQueue.acknowledge(listOf(sent), listOf(sent), emptySet()))
    }

    @Test
    fun liveCheckpointSupersedesPartialButNotCompletedEntries() {
        val partial = PendingProgress("a", 60_000, null, false)
        val completed = PendingProgress("b", 0, null, true)

        val remaining = OfflineProgressQueue.supersededByLive(listOf(partial, completed), "a")
        assertEquals(listOf(completed), remaining)
        assertEquals(listOf(completed), OfflineProgressQueue.supersededByLive(remaining, "b"))
    }

    @Test
    fun offlineResumePrefersTheQueuedCheckpoint() {
        val download = sampleDownload(resumePositionMs = 300_000)

        assertEquals(300_000, OfflineProgressQueue.offlineResumePosition(download, null))
        assertEquals(
            450_000,
            OfflineProgressQueue.offlineResumePosition(download, PendingProgress("episode-1", 450_000, null, false)),
        )
        assertEquals(
            0,
            OfflineProgressQueue.offlineResumePosition(download, PendingProgress("episode-1", 450_000, null, true)),
        )
        assertEquals(0, OfflineProgressQueue.offlineResumePosition(download.copy(watched = true), null))
    }

    @Test
    fun wireItemsNeverCarryNegativeOrZeroDurations() {
        val item = OfflineProgressQueue.toItem(PendingProgress("a", -5, 0, false))

        assertEquals(0, item.positionMs)
        assertNull(item.durationMs)
    }

    @Test
    fun batchesAreBounded() {
        val queue = List(250) { PendingProgress("e$it", 60_000, null, false) }

        assertEquals(100, OfflineProgressQueue.batch(queue, 100).size)
    }
}
