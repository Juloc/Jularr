package de.juloc.jularr.tv

import de.juloc.jularr.core.model.WatchlistItem
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

class TvWatchlistScreenTest {
    private fun entry(
        id: String,
        title: String,
        mediaType: String = "anime",
        date: String? = null,
    ) = WatchlistItem(
        id = id,
        mediaType = mediaType,
        title = title,
        artworkUrl = null,
        availability = "external",
        detailsUrl = null,
        addedAtUtc = date,
    )

    @Test
    fun Category_FiltersOnlyMatchingMediaTypes() {
        val entries = listOf(
            entry("anime", "Anime"),
            entry("tv", "Series", "tv"),
            entry("movie", "Movie", "movie"),
            entry("anime-movie", "Anime Movie").copy(format = "MOVIE"),
            entry("book", "Book", "book"),
            entry("novel", "Light novel", "light-novel"),
            entry("manga", "Manga", "manga"),
        )

        assertEquals(7, watchlistVisibleItems(entries, TvWatchlistCategory.ALL, TvWatchlistSort.NEWEST).size)
        assertEquals(setOf("anime", "anime-movie"), watchlistVisibleItems(entries, TvWatchlistCategory.ANIME, TvWatchlistSort.NEWEST).map { it.id }.toSet())
        assertEquals(listOf("tv"), watchlistVisibleItems(entries, TvWatchlistCategory.SERIES, TvWatchlistSort.NEWEST).map { it.id })
        assertEquals(setOf("movie", "anime-movie"), watchlistVisibleItems(entries, TvWatchlistCategory.MOVIES, TvWatchlistSort.NEWEST).map { it.id }.toSet())
        assertEquals(setOf("book", "novel"), watchlistVisibleItems(entries, TvWatchlistCategory.BOOKS, TvWatchlistSort.NEWEST).map { it.id }.toSet())
    }

    @Test
    fun Sort_PreservesStableOrderingAndKeepsUnknownDatesLast() {
        val entries = listOf(
            entry("old", "Alpha", date = "2026-09-01T12:00:00Z"),
            entry("missing", "Zulu"),
            entry("new", "Beta", date = "2026-10-09T12:00:00Z"),
        )

        assertEquals(listOf("new", "old", "missing"), watchlistVisibleItems(entries, TvWatchlistCategory.ALL, TvWatchlistSort.NEWEST).map { it.id })
        assertEquals(listOf("old", "new", "missing"), watchlistVisibleItems(entries, TvWatchlistCategory.ALL, TvWatchlistSort.OLDEST).map { it.id })
        assertEquals(listOf("old", "new", "missing"), watchlistVisibleItems(entries, TvWatchlistCategory.ALL, TvWatchlistSort.TITLE_ASC).map { it.id })
        assertEquals(listOf("missing", "new", "old"), watchlistVisibleItems(entries, TvWatchlistCategory.ALL, TvWatchlistSort.TITLE_DESC).map { it.id })
    }

    @Test
    fun Status_ShowsOnlyKnownProviderStates() {
        assertEquals(R.string.tv_watchlist_status_airing, watchlistStatusLabel("RELEASING"))
        assertEquals(R.string.tv_watchlist_status_continuing, watchlistStatusLabel("RETURNING_SERIES"))
        assertEquals(R.string.tv_watchlist_status_ended, watchlistStatusLabel("FINISHED"))
        assertNull(watchlistStatusLabel(null))
        assertNull(watchlistStatusLabel("unknown"))
    }
}
