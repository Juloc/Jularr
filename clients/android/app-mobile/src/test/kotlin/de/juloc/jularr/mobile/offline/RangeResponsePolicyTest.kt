package de.juloc.jularr.mobile.offline

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

class RangeResponsePolicyTest {
    private val eTag = "\"7d0-abc\""

    private fun decide(offset: Long, status: Int, responseTag: String?, rangeStart: Long? = null) =
        RangeResponsePolicy.decide(offset, 2_000, eTag, status, responseTag, rangeStart)

    @Test
    fun partialContentMustMatchVersionAndOffset() {
        assertEquals(RangeDecision.APPEND, decide(1_000, 206, eTag, 1_000))
        assertEquals(RangeDecision.SOURCE_CHANGED, decide(1_000, 206, "\"new\"", 1_000))
        assertEquals(RangeDecision.FAILED, decide(1_000, 206, eTag, 0))
    }

    @Test
    fun fullContentRestartsOnlyForTheSameVersion() {
        assertEquals(RangeDecision.RESTART, decide(1_000, 200, eTag))
        assertEquals(RangeDecision.RESTART, decide(0, 200, "W/$eTag"))
        assertEquals(RangeDecision.SOURCE_CHANGED, decide(1_000, 200, "\"new\""))
        assertEquals(RangeDecision.SOURCE_CHANGED, decide(0, 200, null))
    }

    @Test
    fun unsatisfiableRangeAtTheEndMeansComplete() {
        assertEquals(RangeDecision.ALREADY_COMPLETE, decide(2_000, 416, null))
        assertEquals(RangeDecision.SOURCE_CHANGED, decide(1_500, 416, null))
    }

    @Test
    fun errorStatusesAreClassified() {
        assertEquals(RangeDecision.SIGNED_OUT, decide(0, 401, null))
        assertEquals(RangeDecision.SIGNED_OUT, decide(0, 403, null))
        assertEquals(RangeDecision.MISSING, decide(0, 404, null))
        assertEquals(RangeDecision.RETRY_LATER, decide(0, 503, null))
        assertEquals(RangeDecision.RETRY_LATER, decide(0, 429, null))
        assertEquals(RangeDecision.FAILED, decide(0, 400, null))
    }

    @Test
    fun parsesContentRangeStart() {
        assertEquals(1_200L, RangeResponsePolicy.contentRangeStart("bytes 1200-1999/2000"))
        assertNull(RangeResponsePolicy.contentRangeStart("bytes */2000"))
        assertNull(RangeResponsePolicy.contentRangeStart(null))
    }
}
