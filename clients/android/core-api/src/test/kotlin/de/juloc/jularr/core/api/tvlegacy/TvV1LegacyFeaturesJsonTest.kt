package de.juloc.jularr.core.api.tvlegacy

import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Test
import java.math.BigDecimal

class TvV1LegacyFeaturesJsonTest {
    @Test fun statsAreAggregatesWithRealWorkIdsRatherThanFabricatedHistoryRows() {
        val stats = JSONObject(
            """{"watchedMs":4312333,"sessions":7,"gamePlayedMs":0,
                "completed":[{"mediaTypeId":1,"count":3},{"mediaTypeId":2,"count":6}],
                "topWorks":[{"workId":42,"title":"A film","durationMs":390001},
                    {"workId":99,"title":"Series","durationMs":10230}]}"""
        ).tvProfileStatistics()
        assertEquals(7L, stats.sessions)
        assertEquals(4312333L, stats.watchedMs)
        assertEquals(listOf(1, 2), stats.completed.map { it.mediaTypeId })
        assertEquals(42L, stats.topWorks.first().workId)
        assertEquals("Series", stats.topWorks.last().title)
        assertTrue(runCatching { JSONObject("""{"watchedMs":12,"sessions":1,"gamePlayedMs":0}""").tvProfileStatistics() }.isFailure)
        assertEquals(0, JSONObject(
            """{"watchedMs":0,"sessions":0,"gamePlayedMs":0,"completed":[],"topWorks":[]}"""
        ).tvProfileStatistics().completed.size)
    }

    @Test fun playbackAssetRetainsTrueAssetWorkAndNullableUnitNeighbours() {
        val movie = JSONObject(
            """{"mediaAssetId":567,"workId":10,"canonicalTitle":"Movie",
                "mediaAssetTypeId":1,"workUnitTypeId":null,"unitNumber":null,
                "parentUnitNumber":null,"unitTitle":null,"resumePositionMs":0,
                "previousMediaAssetId":null,"nextMediaAssetId":null}"""
        ).tvPlaybackAssetInfo()
        assertEquals(567L, movie.mediaAssetId)
        assertEquals(10L, movie.workId)
        assertNull(movie.unitNumber)
        assertNull(movie.previousMediaAssetId)
        val episode = JSONObject(
            """{"mediaAssetId":901,"workId":10,"canonicalTitle":"Series",
                "mediaAssetTypeId":1,"workUnitTypeId":2,"unitNumber":1.5,
                "parentUnitNumber":2,"unitTitle":"Special",
                "resumePositionMs":12345,"previousMediaAssetId":899,
                "nextMediaAssetId":902}"""
        ).tvPlaybackAssetInfo()
        assertEquals(BigDecimal("1.5"), episode.unitNumber)
        assertEquals(899L, episode.previousMediaAssetId)
        assertEquals(902L, episode.nextMediaAssetId)
        assertEquals("Special", episode.unitTitle)
        assertTrue(runCatching {
            JSONObject("""{"mediaAssetId":0,"workId":10,"canonicalTitle":"X",
                "mediaAssetTypeId":1}""").tvPlaybackAssetInfo()
        }.isFailure)
    }

    @Test fun routesUseOnlyV1AndRejectInvalidInput() {
        assertEquals("/api/v1/statistics?days=0", TvV1LegacyFeaturesRoutes.statistics(0))
        assertEquals("/api/v1/statistics?days=3650", TvV1LegacyFeaturesRoutes.statistics(3650))
        assertEquals("/api/v1/playback/assets/567", TvV1LegacyFeaturesRoutes.playbackAsset(567))
        assertThrows(IllegalArgumentException::class.java) { TvV1LegacyFeaturesRoutes.statistics(-1) }
        assertThrows(IllegalArgumentException::class.java) { TvV1LegacyFeaturesRoutes.statistics(3651) }
        assertThrows(IllegalArgumentException::class.java) { TvV1LegacyFeaturesRoutes.playbackAsset(0) }
    }
}
