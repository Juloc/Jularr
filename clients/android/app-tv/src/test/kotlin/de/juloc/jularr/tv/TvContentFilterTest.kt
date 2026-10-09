package de.juloc.jularr.tv

import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class TvContentFilterTest {
    @Test
    fun tabsOnlyExposeMediaWithTheMatchingServerFormat() {
        assertTrue(TvContentFilter.ALL.includes("MOVIE"))
        assertTrue(TvContentFilter.ANIME.includes("TV"))
        assertTrue(TvContentFilter.SERIES.includes("TV"))
        assertTrue(TvContentFilter.SERIES.includes("OVA"))
        assertFalse(TvContentFilter.SERIES.includes("MOVIE"))
        assertTrue(TvContentFilter.MOVIES.includes("movie"))
        assertFalse(TvContentFilter.MOVIES.includes(null))
        assertFalse(TvContentFilter.MOVIES.includes("TV"))
    }
}
