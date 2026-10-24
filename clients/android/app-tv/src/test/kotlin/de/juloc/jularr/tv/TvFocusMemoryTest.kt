package de.juloc.jularr.tv

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

class TvFocusMemoryTest {
    @Test
    fun recallsNothingForAnUnseenScreen() {
        val memory = TvFocusMemory()
        assertNull(memory.recall("home"))
    }

    @Test
    fun recallsTheLastRememberedItemForThatScreen() {
        val memory = TvFocusMemory()
        memory.remember("home", "row-continue-watching")
        memory.remember("home", "row-search-field")

        assertEquals("row-search-field", memory.recall("home"))
    }

    @Test
    fun screensAreIndependent() {
        val memory = TvFocusMemory()
        memory.remember("home", "search-field")
        memory.remember("watchlist", "card-3")

        assertEquals("search-field", memory.recall("home"))
        assertEquals("card-3", memory.recall("watchlist"))
    }

    @Test
    fun forgettingAScreenClearsOnlyThatScreen() {
        val memory = TvFocusMemory()
        memory.remember("home", "search-field")
        memory.remember("watchlist", "card-3")

        memory.forget("home")

        assertNull(memory.recall("home"))
        assertEquals("card-3", memory.recall("watchlist"))
    }
}
