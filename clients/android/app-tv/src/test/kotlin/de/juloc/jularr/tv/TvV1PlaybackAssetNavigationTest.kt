package de.juloc.jularr.tv

import de.juloc.jularr.core.api.tvlegacy.TvV1PlaybackAssetInfo
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test
import java.math.BigDecimal

class TvV1PlaybackAssetNavigationTest {
    private fun media(
        mediaAssetId: Long,
        workId: Long = 701L,
        title: String? = null,
        previous: Long? = null,
        next: Long? = null,
    ) = TvV1PlaybackAssetInfo(
        mediaAssetId = mediaAssetId,
        workId = workId,
        canonicalTitle = "Verified series",
        mediaAssetTypeId = 1,
        workUnitTypeId = 2,
        unitNumber = BigDecimal("12.5"),
        parentUnitNumber = BigDecimal.ONE,
        unitTitle = title,
        resumePositionMs = 23_000,
        previousMediaAssetId = previous,
        nextMediaAssetId = next,
    )

    @Test fun useActualServerNeighbourIdsAndNeverDeriveThemFromUnitNumbers() {
        val asset = media(mediaAssetId = 9002L, previous = 9001L, next = 9823L)
        assertEquals(9001L, asset.previousMediaAssetId)
        assertEquals(9823L, asset.nextMediaAssetId)
        assertEquals(BigDecimal("12.5"), asset.unitNumber)
        val finalEpisode = media(mediaAssetId = 9003L)
        assertNull(finalEpisode.nextMediaAssetId)
        assertNull(finalEpisode.previousMediaAssetId)
    }

    @Test fun playbackButtonsShowOnlyActualBackendMetadata() {
        assertEquals("Special episode", tvV1PlaybackAssetTitle(media(9002L, title = "Special episode")))
        assertEquals("Verified series", tvV1PlaybackAssetTitle(media(9002L, title = "  ")))
        assertEquals("Verified series", tvV1PlaybackAssetTitle(media(9002L)))
    }
}
