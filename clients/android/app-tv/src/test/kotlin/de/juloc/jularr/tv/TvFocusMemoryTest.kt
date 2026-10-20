package de.juloc.jularr.tv

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

class TvFocusMemoryTest {
    @Test
    fun theFocusOfAScreenIsRemembered() {
        val memory = TvFocusMemory()

        memory.remember("home", "work-12")

        assertEquals("work-12", memory.recall("home"))
    }

    @Test
    fun everyScreenHasItsOwnFocus() {
        val memory = TvFocusMemory()

        memory.remember("home", "work-12")
        memory.remember("watchlist", "work-30")

        assertEquals("work-12", memory.recall("home"))
        assertEquals("work-30", memory.recall("watchlist"))
    }

    @Test
    fun anUnknownScreenHasNoFocus() {
        val memory = TvFocusMemory()

        assertNull(memory.recall("library"))
    }

    @Test
    fun aForgottenScreenHasNoFocus() {
        val memory = TvFocusMemory()
        memory.remember("home", "work-12")

        memory.forget("home")

        assertNull(memory.recall("home"))
    }
}
