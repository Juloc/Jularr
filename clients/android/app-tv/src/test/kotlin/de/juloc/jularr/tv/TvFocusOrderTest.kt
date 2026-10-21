package de.juloc.jularr.tv

import androidx.compose.ui.focus.FocusRequester
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

class TvFocusOrderTest {
    @Test
    fun theEntriesOfAnAreaAreWalkedInTheOrderOfTheScreen() {
        val order = TvFocusOrder()
        val first = FocusRequester()
        val second = FocusRequester()
        val third = FocusRequester()
        order.register(TvFocusArea.Content, first)
        order.register(TvFocusArea.Content, second)
        order.register(TvFocusArea.Content, third)

        assertEquals(second, order.nextOf(TvFocusArea.Content, first))
        assertEquals(third, order.nextOf(TvFocusArea.Content, second))
        assertEquals(second, order.previousOf(TvFocusArea.Content, third))
        assertEquals(first, order.previousOf(TvFocusArea.Content, second))
    }

    @Test
    fun theEdgeOfAnAreaHasNoNeighbour() {
        val order = TvFocusOrder()
        val first = FocusRequester()
        order.register(TvFocusArea.Content, first)

        assertNull(order.nextOf(TvFocusArea.Content, first))
        assertNull(order.previousOf(TvFocusArea.Content, first))
    }

    @Test
    fun everyAreaKeepsItsOwnChain() {
        val order = TvFocusOrder()
        val rail = FocusRequester()
        val content = FocusRequester()
        order.register(TvFocusArea.Rail, rail)
        order.register(TvFocusArea.Content, content)

        assertNull(order.nextOf(TvFocusArea.Rail, rail))
        assertNull(order.previousOf(TvFocusArea.Content, content))
        assertEquals(content, order.firstOf(TvFocusArea.Content))
        assertEquals(rail, order.firstOf(TvFocusArea.Rail))
    }

    @Test
    fun anEntryThatLeftTheScreenIsForgotten() {
        val order = TvFocusOrder()
        val first = FocusRequester()
        val second = FocusRequester()
        order.register(TvFocusArea.Content, first)
        order.register(TvFocusArea.Content, second)

        order.unregister(TvFocusArea.Content, first)

        assertNull(order.nextOf(TvFocusArea.Content, first))
        assertNull(order.previousOf(TvFocusArea.Content, second))
        assertEquals(1, order.size)
    }

    @Test
    fun theOrderForgetsEveryEntryWhenTheScreenChanges() {
        val order = TvFocusOrder()
        val first = FocusRequester()
        order.register(TvFocusArea.Content, first)

        order.clear()

        assertNull(order.nextOf(TvFocusArea.Content, first))
        assertEquals(0, order.size)
    }

    @Test
    fun anEntryWithoutAFocusHasNoNeighbour() {
        val order = TvFocusOrder()

        assertNull(order.nextOf(TvFocusArea.Content, null))
        assertNull(order.previousOf(TvFocusArea.Content, null))
    }
}
