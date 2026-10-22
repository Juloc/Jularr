package de.juloc.jularr.tv

import de.juloc.jularr.core.model.CueResponse
import de.juloc.jularr.core.model.SubtitleCue
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class TvCueTimelineTest {
    private val window = CueResponse(
        trackId = "track",
        fromMs = 10_000,
        toMs = 70_000,
        cues = listOf(
            SubtitleCue(1, 12_000, 15_000, "A", emptyList()),
            SubtitleCue(2, 20_000, 24_000, "B", emptyList()),
        ),
    )

    @Test
    fun resolvesCueByPlaybackPosition() {
        assertEquals(1L, TvCueTimeline.currentCue(window, 12_500)?.id)
        assertEquals(2L, TvCueTimeline.currentCue(window, 23_999)?.id)
        assertNull(TvCueTimeline.currentCue(window, 24_000))
    }

    @Test
    fun refreshesOutsideWindowOrNearItsEnd() {
        assertTrue(TvCueTimeline.shouldRefresh(window, 5_000))
        assertFalse(TvCueTimeline.shouldRefresh(window, 30_000))
        assertTrue(TvCueTimeline.shouldRefresh(window, 60_000))
    }

    @Test
    fun unboundedResponseRequiresWindowRefresh() {
        assertTrue(
            TvCueTimeline.shouldRefresh(
                CueResponse("track", null, null, emptyList()),
                1_000,
            ),
        )
    }
}
