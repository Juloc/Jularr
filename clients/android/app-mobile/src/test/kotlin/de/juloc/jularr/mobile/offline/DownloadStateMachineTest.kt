package de.juloc.jularr.mobile.offline

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

class DownloadStateMachineTest {
    @Test
    fun happyPathEndsReadyOnlyAfterVerification() {
        var state = DownloadState.QUEUED
        state = DownloadStateMachine.next(state, DownloadEvent.Start)!!
        assertEquals(DownloadState.DOWNLOADING, state)
        state = DownloadStateMachine.next(state, DownloadEvent.Verified)!!
        assertEquals(DownloadState.READY, state)
    }

    @Test
    fun pauseAndResumeRequeue() {
        assertEquals(DownloadState.PAUSED, DownloadStateMachine.next(DownloadState.DOWNLOADING, DownloadEvent.Pause))
        assertEquals(DownloadState.PAUSED, DownloadStateMachine.next(DownloadState.QUEUED, DownloadEvent.Pause))
        assertEquals(DownloadState.QUEUED, DownloadStateMachine.next(DownloadState.PAUSED, DownloadEvent.Resume))
    }

    @Test
    fun transientInterruptionRequeuesButNeverOverridesAPause() {
        assertEquals(DownloadState.QUEUED, DownloadStateMachine.next(DownloadState.DOWNLOADING, DownloadEvent.Interrupted))
        assertNull(DownloadStateMachine.next(DownloadState.PAUSED, DownloadEvent.Interrupted))
        assertNull(DownloadStateMachine.next(DownloadState.PAUSED, DownloadEvent.Start))
    }

    @Test
    fun failedDownloadsOnlyLeaveThroughRetry() {
        assertEquals(DownloadState.FAILED, DownloadStateMachine.next(DownloadState.DOWNLOADING, DownloadEvent.Failed("x")))
        assertNull(DownloadStateMachine.next(DownloadState.FAILED, DownloadEvent.Start))
        assertNull(DownloadStateMachine.next(DownloadState.FAILED, DownloadEvent.Verified))
        assertEquals(DownloadState.QUEUED, DownloadStateMachine.next(DownloadState.FAILED, DownloadEvent.Retry))
    }

    @Test
    fun readyIsTerminalAndCannotBeVerifiedFromQueued() {
        listOf(
            DownloadEvent.Start,
            DownloadEvent.Pause,
            DownloadEvent.Resume,
            DownloadEvent.Interrupted,
            DownloadEvent.Verified,
            DownloadEvent.Failed("late"),
            DownloadEvent.Retry,
        ).forEach { event ->
            assertNull(DownloadStateMachine.next(DownloadState.READY, event))
        }
        assertNull(DownloadStateMachine.next(DownloadState.QUEUED, DownloadEvent.Verified))
    }
}
