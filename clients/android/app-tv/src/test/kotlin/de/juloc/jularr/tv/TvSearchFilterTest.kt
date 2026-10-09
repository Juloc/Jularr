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
    fun mediaSelectionAlsoFiltersWithoutAQuery() {
        val series = anime("tv", "Night Series")
        val film = anime("film", "Night Film").copy(format = "MOVIE")
        val library = listOf(series, film)

        assertEquals(
            listOf(film),
            TvSearchFilter.matches(library, "night", TvContentFilter.MOVIES),
        )
        assertEquals(
            listOf(series),
            TvSearchFilter.matches(library, "", TvContentFilter.SERIES),
        )
    }

    @Test
    fun noMatchesReturnsEmptyList() {
        val library = listOf(anime("1", "Frieren"))
        assertEquals(emptyList<AnimeSummary>(), TvSearchFilter.matches(library, "nonexistent"))
    }
}
