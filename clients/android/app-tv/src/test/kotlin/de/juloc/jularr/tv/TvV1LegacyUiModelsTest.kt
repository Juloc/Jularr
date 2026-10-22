package de.juloc.jularr.tv

import de.juloc.jularr.core.api.catalog.TvWorkCardV1
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class TvV1LegacyUiModelsTest {
    private fun card(
        id: Long = 123,
        mediaTypeId: Int = 2,
        mediaSubtypeId: Int? = 1,
    ) = TvWorkCardV1(
        id = id,
        mediaTypeId = mediaTypeId,
        mediaSubtypeId = mediaSubtypeId,
        title = "Test title",
        releaseYear = 2026,
        posterUrl = "/api/v1/media/covers/123",
        rating = null,
        mediaStateTypeId = 1,
        isWatchlisted = false,
        availabilityStateId = 1,
        availableUnits = 2,
        monitoredUnits = 14,
        audioLanguages = emptyList(),
        subtitleLanguages = emptyList(),
        progressStateId = 1,
        progressPercent = 0,
    )

    @Test
    fun videoCardUsesRealWorkIdAndDoesNotInventEpisodeCounts() {
        val mapped = requireNotNull(TvV1LegacyUiModels.videoCard(card()))
        assertEquals("123", mapped.id)
        assertEquals("Test title", mapped.title)
        assertEquals(0, mapped.episodeCount)
        assertEquals(0, mapped.seasonCount)
        assertEquals("ANIME", mapped.format)
        assertNull(mapped.bannerImageUrl)
    }

    @Test
    fun onlyActualVideoTypesAppearInOriginalVideoHome() {
        val books = card(id = 31, mediaTypeId = 3, mediaSubtypeId = 3)
        val movie = card(id = 8, mediaTypeId = 1, mediaSubtypeId = null)
        val videos = TvV1LegacyUiModels.videoLibrary(listOf(books, card(), movie))
        assertEquals(listOf("123", "8"), videos.anime.map { it.id })
        assertEquals("MOVIE", videos.anime.last().format)
        assertFalse(TvContentFilter.ANIME.includes("SERIES"))
        assertTrue(TvContentFilter.ANIME.includes("ANIME"))
        assertTrue(TvContentFilter.MOVIES.includes("ANIME_MOVIE"))
    }

    @Test
    fun watchlistDoesNotPassBookWorksToVideoPlayerAndKeepsRealMediaType() {
        val video = card(id = 8, mediaTypeId = 1, mediaSubtypeId = null)
        val book = card(id = 31, mediaTypeId = 3, mediaSubtypeId = 2)
        val entries = TvV1LegacyUiModels.watchlist(listOf(video, book))
        assertEquals("8", entries[0].localMediaId)
        assertEquals("movie", entries[0].mediaType)
        assertEquals("manga", entries[1].mediaType)
        assertNull(entries[1].localMediaId)
        assertNull(entries[1].addedAtUtc)
        assertNull(entries[1].status)
    }
}
