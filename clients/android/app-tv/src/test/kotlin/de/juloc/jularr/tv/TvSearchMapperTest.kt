package de.juloc.jularr.tv

import de.juloc.jularr.core.model.DiscoverItemV1
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

class TvSearchMapperTest {
    private val texts = TvTexts(mapOf("media.state.Available" to "Available", "media.state.Requested" to "Requested"))

    @Test
    fun aSearchResultBecomesACardOfTheDesktop() {
        val card = DiscoverItemV1(
            mediaTypeId = 2,
            tmdbId = 1234,
            workId = 12,
            title = "One Show",
            releaseYear = 2006,
            overview = "A short text.",
            posterUrl = "https://example.test/poster.jpg",
            backdropUrl = null,
            mediaStateTypeId = 3,
            plexUrl = null,
        ).toTvCard(texts)

        assertEquals("work-12", card.id)
        assertEquals("One Show", card.title)
        assertEquals("https://example.test/poster.jpg", card.posterUrl)
        assertEquals("Available", card.meta)
        assertNull(card.progress)
    }

    @Test
    fun aSearchResultWithoutAWorkKeepsItsProviderId() {
        val card = DiscoverItemV1(
            mediaTypeId = 1,
            tmdbId = 99,
            workId = null,
            title = "One Film",
            releaseYear = null,
            overview = null,
            posterUrl = null,
            backdropUrl = null,
            mediaStateTypeId = 2,
            plexUrl = null,
        ).toTvCard(texts)

        assertEquals("tmdb-99", card.id)
        assertEquals("Requested", card.meta)
        assertNull(card.posterUrl)
    }
}
