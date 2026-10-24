package de.juloc.jularr.tv

import de.juloc.jularr.core.api.catalog.TvDiscoverCatalogItemV1
import de.juloc.jularr.core.api.catalog.TvDiscoverItemV1
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class TvV1HomeHeroTest {
    private fun movie(id: Int, overview: String? = null, backdrop: String? = "https://example.test/$id.jpg") =
        TvDiscoverItemV1(
            mediaTypeId = 1, tmdbId = id, workId = null,
            title = "Movie $id", releaseYear = 2026, overview = overview,
            posterUrl = "https://example.test/poster/$id.jpg", backdropUrl = backdrop,
            mediaStateTypeId = 1, plexUrl = null, rating = null, trailerKey = null,
        )

    private fun movies(feed: Int, vararg items: TvDiscoverItemV1) = TvV1HomeVideoFeed(
        scopeSlug = "movies", mediaTypeId = 1, mediaSubtypeId = null,
        feedTypeId = feed, items = items.toList(),
    )

    @Test fun ranksGenuineTrendingAndNewAndDeduplicatesAcrossShelves() {
        val heroes = tvV1HomeHeroCandidates(
            listOf(
                movies(2, movie(2), movie(3)),
                movies(3, movie(1), movie(4)),
                movies(1, movie(1, overview = "From trending"), movie(2)),
            ),
        )
        assertEquals(listOf("tmdb:1:1", "tmdb:1:2", "tmdb:1:4", "tmdb:1:3"),
            heroes.map { it.key })
        assertEquals("From trending", heroes.first().overview)
        assertEquals("Filme · Trending", heroes.first().subtitle)
        assertEquals(1, heroes.first().discoverItem?.mediaTypeId)
        assertEquals(null, heroes.first().catalogItem)
    }

    @Test fun acceptsCatalogNamespaceButDoesNotForgeWorkId() {
        val catalog = TvDiscoverCatalogItemV1(
            namespace = "anilist", externalId = "30499", title = "Anime",
            releaseYear = 2026, author = null, workId = null,
            posterUrl = "https://example.test/anime.jpg",
            rating = null, mediaStateTypeId = null,
        )
        val feed = TvV1HomeVideoFeed(
            scopeSlug = "anime", mediaTypeId = 2, mediaSubtypeId = 1,
            feedTypeId = 1, items = emptyList(),
            catalogItems = listOf(catalog), isCatalog = true,
        )
        val heroes = tvV1HomeHeroCandidates(listOf(feed))
        assertEquals("catalog:anilist:30499", heroes.single().key)
        assertEquals("Anime · Trending", heroes.single().subtitle)
        assertEquals(null, heroes.single().discoverItem)
        assertEquals(null, heroes.single().catalogItem?.workId)
        assertEquals("30499", heroes.single().catalogItem?.externalId)
    }

    @Test fun limitsToEightAndSkipsUnusableArtworkAndTitles() {
        val bad = movie(1).copy(title = " ")
        val noArt = movie(2).copy(posterUrl = null, backdropUrl = null)
        val valid = (10..26).map(::movie)
        val heroes = tvV1HomeHeroCandidates(
            listOf(movies(1, bad, noArt, *valid.toTypedArray())),
        )
        assertEquals(8, heroes.size)
        assertEquals(8, heroes.map { it.key }.distinct().size)
        assertTrue(heroes.first().key.endsWith(":10"))
        assertFalse(heroes.any { it.key.endsWith(":1") || it.key.endsWith(":2") })
    }
}
