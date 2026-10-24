package de.juloc.jularr.tv

import de.juloc.jularr.core.api.tvlegacy.TvV1PlaybackAssetInfo
import org.junit.Assert.assertEquals
import org.junit.Assert.assertThrows
import org.junit.Assert.assertTrue
import org.junit.Test
import java.math.BigDecimal

class TvV1AppControllerTest {
    @Test fun workAndEpisodeRequireDistinctVerifiedIds() {
        val work = TvV1AppController.work(TvV1NavigationState(), 81)
        assertEquals(81L, work.current.workId)
        val episode = TvV1AppController.episode(work, 81, 7002, setOf(7002, 7003))
        assertEquals(7002L, episode.current.workUnitId)
        assertEquals(81L, episode.current.workId)
        assertThrows(IllegalArgumentException::class.java) {
            TvV1AppController.episode(work, 81, 8888, setOf(7002, 7003))
        }
        assertThrows(IllegalArgumentException::class.java) {
            TvV1AppController.work(work, -1)
        }
    }

    @Test fun playbackDoesNotConfuseTheRealAssetWithWorkOrUnitId() {
        val work = TvV1AppController.work(TvV1NavigationState(), 31)
        val episode = TvV1AppController.episode(work, 31, 410, setOf(410))
        val playing = TvV1AppController.playback(episode, 9009, 31, 410)
        assertEquals(31L, playing.current.workId)
        assertEquals(410L, playing.current.workUnitId)
        assertEquals(TvV1CatalogPage.Player, playing.page)
        assertEquals(410L, TvV1AppController.back(playing).current.workUnitId)
        assertThrows(IllegalArgumentException::class.java) {
            TvV1AppController.playback(episode, 0, 31, 410)
        }
    }

    @Test fun verifiedNextAssetDoesNotInventAnEpisodeId() {
        val playing = TvV1AppController.playback(TvV1NavigationState(), 9002, 31, 410)
        val next = TvV1PlaybackAssetInfo(
            mediaAssetId = 9003,
            workId = 31,
            canonicalTitle = "Series",
            mediaAssetTypeId = 1,
            workUnitTypeId = 2,
            unitNumber = BigDecimal("4.5"),
            parentUnitNumber = BigDecimal.ONE,
            unitTitle = "Special",
            resumePositionMs = 1200,
            previousMediaAssetId = 9002,
            nextMediaAssetId = null,
        )
        val moved = TvV1AppController.verifiedNeighbour(playing, 9003, next)
        assertEquals(31L, moved.current.workId)
        assertEquals(null, moved.current.workUnitId)
        assertThrows(IllegalArgumentException::class.java) {
            TvV1AppController.verifiedNeighbour(playing, 9004, next)
        }
    }

    @Test fun profileChangeForgetsTheEntirePreviousViewerHistory() {
        val state = TvV1AppController.playback(
            TvV1AppController.work(TvV1NavigationState(), 31), 9009, 31, 410,
        )
        val cleared = TvV1AppController.clearForProfileSwitch()
        assertEquals(TvV1CatalogPage.Home, cleared.page)
        assertTrue(cleared.previous.isEmpty())
        assertEquals(null, cleared.current.workId)
        assertTrue(state.previous.isNotEmpty())
    }
}
