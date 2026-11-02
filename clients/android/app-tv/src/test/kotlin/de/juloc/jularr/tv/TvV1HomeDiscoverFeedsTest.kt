package de.juloc.jularr.tv

import de.juloc.jularr.core.api.catalog.TvDiscoverItemV1
import de.juloc.jularr.core.api.catalog.TvDiscoverScopeV1
import de.juloc.jularr.core.api.catalog.TvDiscoverShelfV1
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

class TvV1HomeDiscoverFeedsTest {
    private fun item(workId: Long = 5000, tmdbId: Int? = 123): TvDiscoverItemV1 =
        TvDiscoverItemV1(
            mediaTypeId = 1, tmdbId = tmdbId, workId = workId,
            title = "Film", releaseYear = 2026, overview = null, posterUrl = null,
            backdropUrl = null, mediaStateTypeId = 1, plexUrl = null,
            rating = null, trailerKey = null,
        )

    @Test fun movieRankingsRemainSeparateAndKeepWorkIdentity() {
        val scopes = listOf(
            TvDiscoverScopeV1("movies", 1, null, false, listOf(1, 2, 3, 4)),
            TvDiscoverScopeV1("books", 3, null, true, listOf(1)),
        )
        val shelves = listOf(
            TvDiscoverShelfV1("movies", 1, listOf(item(1001, 1001)), emptyList()),
            TvDiscoverShelfV1("movies", 2, listOf(item(workId = 7001, tmdbId = 1002)), emptyList()),
            TvDiscoverShelfV1("movies", 3, listOf(item(1003, 1003)), emptyList()),
            TvDiscoverShelfV1("movies", 4, listOf(item(1004, 1004)), emptyList()),
            TvDiscoverShelfV1("books", 1, listOf(item()), emptyList()),
        )
        val feeds = tvV1HomeVideoFeeds(scopes, shelves)
        assertEquals(listOf(1, 2, 3, 4), feeds.map { it.feedTypeId })
        assertEquals(listOf("Filme · Trending", "Filme · Top", "Filme · Neu", "Filme · Demnächst"),
            feeds.map(::tvV1HomeFeedTitle))
        assertEquals(1001L, feeds[0].items.single().workId)
        assertEquals(1001, feeds[0].items.single().tmdbId)
        assertEquals(7001L, feeds[1].items.single().workId)
    }

    @Test fun mismatchedMediaTypeOrUnknownRankingsAreNotFaked() {
        val scopes = listOf(TvDiscoverScopeV1("movies", 1, null, false, listOf(1, 5)))
        val shelves = listOf(TvDiscoverShelfV1("movies", 1,
            listOf(item(31, 31).copy(mediaTypeId = 2)), emptyList()))
        assertTrue(tvV1HomeVideoFeeds(scopes, shelves).isEmpty())
    }
}
