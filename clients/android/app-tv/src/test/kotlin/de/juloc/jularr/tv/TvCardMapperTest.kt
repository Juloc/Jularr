package de.juloc.jularr.tv

import de.juloc.jularr.core.model.ContinueItemV1
import de.juloc.jularr.core.model.WorkCardV1
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

class TvCardMapperTest {
    private val texts = TvTexts(
        mapOf(
            "media.state.Available" to "Available",
            "media.state.Requested" to "Requested",
            "library.unit.Season" to "Season",
            "library.unit.Episode" to "Episode",
            "library.unit.Volume" to "Volume",
        ),
    )

    @Test
    fun aWorkCardShowsTheStateOfTheServer() {
        val card = WorkCardV1(
            id = 12,
            mediaTypeId = 2,
            mediaSubtypeId = 1,
            title = "One Show",
            releaseYear = 2006,
            posterUrl = "https://example.test/poster.jpg",
            mediaStateTypeId = 3,
            isWatchlisted = true,
            availabilityStateId = 1,
            availableUnits = 2,
            monitoredUnits = 2,
            audioLanguages = listOf("ja"),
            subtitleLanguages = listOf("de"),
        ).toTvCard(texts)

        assertEquals("work-12", card.id)
        assertEquals("One Show", card.title)
        assertEquals("https://example.test/poster.jpg", card.posterUrl)
        assertEquals("Available", card.meta)
        assertEquals(1, card.availabilityStateId)
        assertNull(card.progress)
    }

    @Test
    fun aContinuedTitleShowsItsUnitAndItsProgress() {
        val card = ContinueItemV1(
            workId = 12,
            mediaAssetId = 55,
            mediaTypeId = 2,
            title = "One Show",
            posterUrl = null,
            workUnitTypeId = 2,
            unitNumber = 3.0,
            parentUnitNumber = 1.0,
            positionMs = 600_000,
            durationMs = 2_400_000,
            progression = 0.25,
            mediaAssetTypeId = ContinueItemV1.AssetVideo,
        ).toTvCard(texts)

        assertEquals("asset-55", card.id)
        assertEquals("Season 1 · Episode 3", card.meta)
        assertEquals(0.25f, card.progress!!, 0.001f)
    }

    @Test
    fun aContinuedTitleWithoutAProgressUsesItsPosition() {
        val card = ContinueItemV1(
            workId = 12,
            mediaAssetId = 56,
            mediaTypeId = 3,
            title = "One Book",
            posterUrl = null,
            workUnitTypeId = 3,
            unitNumber = 2.0,
            parentUnitNumber = null,
            positionMs = 300_000,
            durationMs = 1_200_000,
            progression = null,
            mediaAssetTypeId = ContinueItemV1.AssetEbook,
        ).toTvCard(texts)

        assertEquals("Volume 2", card.meta)
        assertEquals(0.25f, card.progress!!, 0.001f)
    }

    @Test
    fun aContinuedTitleWithoutAUnitShowsNoUnit() {
        val card = ContinueItemV1(
            workId = 12,
            mediaAssetId = 57,
            mediaTypeId = 1,
            title = "One Film",
            posterUrl = null,
            workUnitTypeId = null,
            unitNumber = null,
            parentUnitNumber = null,
            positionMs = 0,
            durationMs = null,
            progression = null,
            mediaAssetTypeId = ContinueItemV1.AssetVideo,
        ).toTvCard(texts)

        assertNull(card.meta)
        assertNull(card.progress)
    }
}
