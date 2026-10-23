package de.juloc.jularr.tv

import de.juloc.jularr.core.api.catalog.TvWorkEpisodeV1
import org.junit.Assert.*
import org.junit.Test
import java.math.BigDecimal

class TvV1EpisodeDetailScreenTest {
    @Test fun progressDoesNotGuessDurationOrOverrun() {
        assertNull(tvV1EpisodeProgressFraction(100L, null))
        assertNull(tvV1EpisodeProgressFraction(100L, 0L))
        assertEquals(0.5f, tvV1EpisodeProgressFraction(5_000, 10_000)!!, 0.0001f)
        assertEquals(1f, tvV1EpisodeProgressFraction(20_000, 10_000)!!, 0.0001f)
    }

    @Test fun realFractionalEpisodeNumberIsNeverRounded() {
        val episode = TvWorkEpisodeV1(
            id = 711, seasonNumber = 1, number = BigDecimal("12.50"),
            title = "Special", overview = null, imageUrl = null,
            runtimeMinutes = null, releasedOn = null, mediaAssetId = 987,
            resumePositionMs = null, durationMs = null, isCompleted = false,
            mediaStateTypeId = 1, isUpcoming = false, groupName = null,
        )
        assertEquals("S1 E12.5", tvV1EpisodeLabel(episode))
        assertEquals(987L, episode.playableMediaAssetId())
    }
}
