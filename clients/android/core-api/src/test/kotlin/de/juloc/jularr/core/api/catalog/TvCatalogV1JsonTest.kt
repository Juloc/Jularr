package de.juloc.jularr.core.api.catalog

import org.json.JSONArray
import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Test
import java.math.BigDecimal

class TvCatalogV1JsonTest {
    @Test fun pagedLibraryUsesWorkIdsAndPreservesRealServerFields() {
        val page = JSONObject(
            """
            {
              "items":[{
                "id":741,"mediaTypeId":2,"mediaSubtypeId":1,"title":"Frieren: Beyond Journey's End",
                "releaseYear":2023,"posterUrl":null,"rating":8.8,"mediaStateTypeId":3,
                "isWatchlisted":true,"availabilityStateId":2,"availableUnits":12,"monitoredUnits":28,
                "audioLanguages":["ja","de"],"subtitleLanguages":["de"],
                "progressStateId":2,"progressPercent":43
              }],
              "page":2,"pageSize":25
            }
            """.trimIndent(),
        ).page { workCard() }
        assertEquals(2, page.page)
        assertEquals(25, page.pageSize)
        assertEquals(741L, page.items.single().id)
        assertNull(page.items.single().posterUrl)
        assertEquals(listOf("ja", "de"), page.items.single().audioLanguages)
        assertTrue(page.items.single().isWatchlisted)
        assertEquals(1, page.items.single().mediaSubtypeId)
    }

    @Test fun directContinueArrayKeepsMediaAssetDistinctFromWork() {
        val item = JSONArray(
            """
            [{"workId":741,"mediaAssetId":99041,"mediaAssetTypeId":1,
              "mediaTypeId":2,"mediaSubtypeId":1,"title":"Frieren: Beyond Journey's End",
              "releaseYear":2023,"posterUrl":null,"workUnitTypeId":2,
              "unitNumber":7.5,"parentUnitNumber":1,"positionMs":70000,
              "durationMs":1400000,"progression":null}]
            """.trimIndent(),
        ).objects { continueItem() }.single()
        assertEquals(741L, item.workId)
        assertEquals(99041L, item.mediaAssetId)
        assertEquals(BigDecimal("7.5"), item.unitNumber)
        assertNull(item.progression)
    }

    @Test fun episodeWithoutMediaIsNotMistakenForPlayable() {
        val episodes = JSONArray(
            """
            [
              {"id":8001,"seasonNumber":1,"number":1,"title":"Episode One","overview":null,
               "imageUrl":null,"runtimeMinutes":24,"releasedOn":"2026-10-01",
               "mediaAssetId":null,"resumePositionMs":null,"durationMs":null,
               "isCompleted":false,"mediaStateTypeId":2,"isUpcoming":false,"groupName":null},
              {"id":8002,"seasonNumber":1,"number":2.5,"title":null,"overview":null,
               "imageUrl":null,"runtimeMinutes":null,"releasedOn":null,
               "mediaAssetId":90002,"resumePositionMs":1999,"durationMs":1400000,
               "isCompleted":false,"mediaStateTypeId":3,"isUpcoming":false,"groupName":"Arc 1"}
            ]
            """.trimIndent(),
        ).objects { workEpisode() }
        assertEquals(8001L, episodes[0].id)
        assertNull(episodes[0].playableMediaAssetId())
        assertEquals(90002L, episodes[1].playableMediaAssetId())
        assertEquals(BigDecimal("2.5"), episodes[1].number)
        assertNull(episodes[1].title)
    }

    @Test fun availabilityOnlyOffersExistingVideoFiles() {
        val item = JSONObject(
            """
            {"id":90002,"workVersionId":701,"workEditionId":702,"workUnitId":8002,
             "mediaAssetTypeId":1,"workUnitTypeId":2,"unitNumber":2,"parentUnitNumber":1,
             "fileCount":1,"sizeBytes":2123456789,"durationMs":1400000,"isAvailable":true,
             "height":1080,"audioLanguages":["ja"],"subtitleLanguages":["de"],
             "resumePositionMs":1999,"isCompleted":false,"isEmulated":false,
             "groupName":null,"unitTitle":null,"hasProgress":true,
             "platformName":null,"unitImageUrl":null,"discNumber":null}
            """.trimIndent(),
        ).workAvailability()
        assertEquals(90002L, item.playableMediaAssetId())
        assertEquals(8002L, item.workUnitId)
        assertEquals(702L, item.workEditionId)
        assertNull(item.copy(isAvailable = false).playableMediaAssetId())
        assertNull(item.copy(mediaAssetTypeId = 3).playableMediaAssetId())
    }

    @Test fun discoverCatalogItemCarriesItsRequiredWorkIdAndSeason() {
        val shelves = JSONArray(
            """
            [{"scopeSlug":"books","discoverFeedTypeId":1,"items":[],
              "catalogItems":[{"namespace":"openlibrary","externalId":"OL12345W",
              "title":"A Book","releaseYear":null,"author":"Author",
              "workId":88,"posterUrl":null,"rating":null,"mediaStateTypeId":1,"seasonNumber":3}]}]
            """.trimIndent(),
        ).objects { discoverShelf() }
        assertEquals(88L, shelves.single().catalogItems.single().workId)
        assertEquals(3, shelves.single().catalogItems.single().seasonNumber)
        assertEquals("OL12345W", shelves.single().catalogItems.single().externalId)
        assertEquals("openlibrary", shelves.single().catalogItems.single().namespace)
    }

    @Test fun requiredFieldsFailFastInsteadOfInventingMetadata() {
        val broken = JSONObject("""{"items":[{"id":3,"title":"Incomplete"}],"page":1,"pageSize":25}""")
        assertTrue(runCatching { broken.page { workCard() } }.isFailure)
    }
}
