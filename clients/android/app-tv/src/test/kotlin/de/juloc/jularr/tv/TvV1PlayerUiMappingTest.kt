package de.juloc.jularr.tv

import de.juloc.jularr.core.api.playbackextras.TvV1PlaybackMarker
import de.juloc.jularr.core.api.playbackextras.TvV1PlaybackPreferences
import de.juloc.jularr.core.api.playbackextras.TvV1Trickplay
import org.junit.Assert.*
import org.junit.Test
import java.math.BigDecimal

class TvV1PlayerUiMappingTest {
    private val preferences = TvV1PlaybackPreferences(
        audioLanguage = null, subtitleLanguage = null, subtitleModeTypeId = 2,
        speed = BigDecimal("1.00"), autoplayNext = true, introSkipModeTypeId = 3,
        outroSkipModeTypeId = 1, autoplayPreviews = true, profileLanguage = "de", deviceQuality = 1,
    )

    @Test fun onlyAllowedRealServerMarkersBecomeSkipControls() {
        val markers = listOf(TvV1PlaybackMarker(1, 500, 4000), TvV1PlaybackMarker(2, 50000, 58000))
        val segments = TvV1PlayerUiMapping.skipSegments(markers, preferences)
        assertEquals("intro", segments[0].kind)
        assertTrue(segments[0].canSkip)
        assertEquals(4000L, TvV1PlayerUiMapping.automaticSkipTarget(1000, markers, preferences))
        assertFalse(segments[1].canSkip)
        assertNull(TvV1PlayerUiMapping.automaticSkipTarget(55000, markers, preferences))
    }

    @Test fun trickplayUsesActualOneBasedSpriteSheetsAndFrameGeometry() {
        val desc = TvV1PlayerUiMapping.trickplay(9002, TvV1Trickplay(10, 21, 4, 3, 160, 90))
        assertEquals(10000, desc.intervalMs)
        assertEquals(21, desc.thumbnailCount)
        assertEquals(160, desc.tileWidth)
        assertEquals(listOf(
            "/api/v1/playback/assets/9002/trickplay/1",
            "/api/v1/playback/assets/9002/trickplay/2",
        ), desc.spriteUrls)
    }
}
