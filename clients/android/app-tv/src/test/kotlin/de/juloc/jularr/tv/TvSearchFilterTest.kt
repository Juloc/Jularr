package de.juloc.jularr.tv

import de.juloc.jularr.core.model.AnimeSummary
import org.junit.Assert.assertEquals
import org.junit.Test

class TvSearchFilterTest {
    private fun anime(
        id: String,
        title: String,
        localTitle: String = title,
        nativeTitle: String? = null,
    ) = AnimeSummary(
        id = id,
        title = title,
        localTitle = localTitle,
        nativeTitle = nativeTitle,
        coverImageUrl = null,
        bannerImageUrl = null,
        episodeCount = 12,
        seasonCount = 1,
        seasonYear = 2026,
        format = "TV",
    )

    @Test
    fun blankQueryReturnsEverything() {
        val library = listOf(anime("1", "Frieren"), anime("2", "Bocchi the Rock"))
        assertEquals(library, TvSearchFilter.matches(library, "   "))
    }

    @Test
    fun matchesCaseInsensitiveSubstringOfTitle() {
        val library = listOf(anime("1", "Frieren"), anime("2", "Bocchi the Rock"))
        assertEquals(
            listOf(library[0]),
            TvSearchFilter.matches(library, "frier"),
        )
    }

    @Test
    fun matchesLocalOrNativeTitleToo() {
        val target = anime(
            id = "1",
            title = "Frieren: Beyond Journey's End",
            localTitle = "Frieren",
            nativeTitle = "葬送のフリーレン",
        )
        val library = listOf(target, anime("2", "Bocchi the Rock"))

        assertEquals(listOf(target), TvSearchFilter.matches(library, "フリーレン"))
        assertEquals(listOf(target), TvSearchFilter.matches(library, "Frieren"))
    }

    @Test
    fun selectedTypeFiltersSearchResults() {
        val series = anime("series", "Frieren")
        val movie = series.copy(id = "movie", title = "Frieren Movie", format = "MOVIE")
        val all = listOf(series, movie)

        assertEquals(listOf(movie), TvSearchFilter.matches(all, "frieren", TvContentFilter.MOVIES))
        assertEquals(listOf(series), TvSearchFilter.matches(all, "frieren", TvContentFilter.SERIES))
    }

    @Test
    fun noMatchesReturnsEmptyList() {
        val library = listOf(anime("1", "Frieren"))
        assertEquals(emptyList<AnimeSummary>(), TvSearchFilter.matches(library, "nonexistent"))
    }
}
