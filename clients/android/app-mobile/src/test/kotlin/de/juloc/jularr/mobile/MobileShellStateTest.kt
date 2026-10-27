package de.juloc.jularr.mobile

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

class MobileShellStateTest {
    @Test
    fun closingNativePlayerReturnsToExactWebUrl() {
        val before = MobileShellState(
            webUrl = "https://jularr.example/Library/Anime/123?tab=episodes",
        )

        val playing = before.openNativeEpisode(
            "7a3c4f54-66a8-4acd-98aa-2fcd597f9466",
        )
        val after = playing.closeNativePlayer()

        assertEquals(before.webUrl, after.webUrl)
        assertNull(after.activeEpisodeId)
    }

    @Test
    fun recoveryAndFallbackKeepAbsolutePositionAndIntent() {
        val preserved = PlaybackPositionPolicy.preserve(
            playerPositionMs = 8_000,
            sourceOffsetMs = 42_000,
            shouldPlay = true,
        )

        assertEquals(50_000L, preserved.positionMs)
        assertEquals(true, preserved.shouldPlay)
    }
}
