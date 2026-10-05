package de.juloc.jularr.tv

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class TvProgressPolicyTest {
    @Test
    fun heartbeatIsThrottledToFiveSeconds() {
        val policy = TvProgressPolicy()

        val first = policy.evaluate(
            TvProgressEvent.HEARTBEAT,
            nowMs = 1_000,
            positionMs = 10_000,
            durationMs = 100_000,
        )
        val tooSoon = policy.evaluate(
            TvProgressEvent.HEARTBEAT,
            nowMs = 5_000,
            positionMs = 14_000,
            durationMs = 100_000,
        )
        val due = policy.evaluate(
            TvProgressEvent.HEARTBEAT,
            nowMs = 6_000,
            positionMs = 15_000,
            durationMs = 100_000,
        )

        assertEquals(10_000L, first?.positionMs)
        assertNull(tooSoon)
        assertEquals(15_000L, due?.positionMs)
    }

    @Test
    fun pauseSeekBackgroundAndClosePersistImmediately() {
        for (event in listOf(
            TvProgressEvent.PAUSE,
            TvProgressEvent.SEEK,
            TvProgressEvent.BACKGROUND,
            TvProgressEvent.CLOSE,
        )) {
            val policy = TvProgressPolicy()
            policy.evaluate(
                TvProgressEvent.HEARTBEAT,
                nowMs = 1_000,
                positionMs = 10_000,
                durationMs = 100_000,
            )

            val write = policy.evaluate(
                event,
                nowMs = 1_100,
                positionMs = 11_000,
                durationMs = 100_000,
            )

            assertEquals(11_000L, write?.positionMs)
        }
    }

    @Test
    fun completedUsesNearEndThreshold() {
        val policy = TvProgressPolicy()

        val incomplete = policy.evaluate(
            TvProgressEvent.HEARTBEAT,
            nowMs = 1_000,
            positionMs = 94_000,
            durationMs = 100_000,
        )
        val crossed = policy.evaluate(
            TvProgressEvent.HEARTBEAT,
            nowMs = 6_100,
            positionMs = 95_000,
            durationMs = 100_000,
        )
        val complete = policy.evaluate(
            TvProgressEvent.CLOSE,
            nowMs = 6_200,
            positionMs = 95_500,
            durationMs = 100_000,
        )

        assertFalse(incomplete!!.completed)
        assertTrue(crossed!!.completed)
        assertTrue(complete!!.completed)
    }

    @Test
    fun seekPastThresholdIsOnlyAResumePointAndOnlyEndedCompletesIt() {
        val policy = TvProgressPolicy()
        policy.evaluate(
            TvProgressEvent.HEARTBEAT,
            nowMs = 1_000,
            positionMs = 10_000,
            durationMs = 100_000,
        )

        val seek = policy.evaluate(
            TvProgressEvent.SEEK,
            nowMs = 1_100,
            positionMs = 96_000,
            durationMs = 100_000,
        )
        val close = policy.evaluate(
            TvProgressEvent.CLOSE,
            nowMs = 1_200,
            positionMs = 96_000,
            durationMs = 100_000,
        )
        val firstHeartbeat = policy.evaluate(
            TvProgressEvent.HEARTBEAT,
            nowMs = 7_000,
            positionMs = 97_000,
            durationMs = 100_000,
        )
        val secondHeartbeat = policy.evaluate(
            TvProgressEvent.HEARTBEAT,
            nowMs = 12_000,
            positionMs = 97_500,
            durationMs = 100_000,
        )
        val ended = policy.evaluate(
            TvProgressEvent.ENDED,
            nowMs = 13_000,
            positionMs = 100_000,
            durationMs = 100_000,
        )

        assertFalse(seek!!.completed)
        assertFalse(close!!.completed)
        assertFalse("Playing on after a seek past the threshold does not complete.", firstHeartbeat!!.completed)
        assertFalse(secondHeartbeat!!.completed)
        assertTrue(ended!!.completed)
    }

    @Test
    fun seekBelowTheThresholdThenPlayingThroughItCompletes() {
        val policy = TvProgressPolicy()
        policy.evaluate(TvProgressEvent.SEEK, nowMs = 1_000, positionMs = 90_000, durationMs = 100_000)
        policy.evaluate(TvProgressEvent.HEARTBEAT, nowMs = 1_500, positionMs = 94_500, durationMs = 100_000)

        val crossed = policy.evaluate(
            TvProgressEvent.HEARTBEAT,
            nowMs = 7_000,
            positionMs = 95_100,
            durationMs = 100_000,
        )

        assertTrue(crossed!!.completed)
    }

    @Test
    fun seekAfterCrossingResetsTheCompletion() {
        val policy = TvProgressPolicy()
        policy.evaluate(TvProgressEvent.HEARTBEAT, nowMs = 1_000, positionMs = 94_900, durationMs = 100_000)
        policy.evaluate(TvProgressEvent.HEARTBEAT, nowMs = 1_500, positionMs = 95_100, durationMs = 100_000)

        val seek = policy.evaluate(TvProgressEvent.SEEK, nowMs = 1_600, positionMs = 50_000, durationMs = 100_000)

        assertFalse(seek!!.completed)
    }

    @Test
    fun startingPastThresholdWithoutPlaybackDoesNotComplete() {
        val policy = TvProgressPolicy()

        val close = policy.evaluate(
            TvProgressEvent.CLOSE,
            nowMs = 1_000,
            positionMs = 96_000,
            durationMs = 100_000,
        )

        assertFalse(close!!.completed)
    }

    @Test
    fun resumingPastTheThresholdAndPlayingOnDoesNotComplete() {
        val policy = TvProgressPolicy()
        policy.evaluate(TvProgressEvent.HEARTBEAT, nowMs = 1_000, positionMs = 96_000, durationMs = 100_000)

        val next = policy.evaluate(TvProgressEvent.HEARTBEAT, nowMs = 7_000, positionMs = 96_500, durationMs = 100_000)

        assertFalse(next!!.completed)
    }
}
