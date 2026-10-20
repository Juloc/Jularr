package de.juloc.jularr.tv

import androidx.compose.ui.focus.FocusRequester
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

class TvFocusOrderTest {
    @Test
    fun theEntriesOfARowAreMovedLeftAndRight() {
        val order = TvFocusOrder()
        val first = FocusRequester()
        val second = FocusRequester()
        order.register("row", first)
        order.register("row", second)

        assertEquals(second, order.moveInside(first, forward = true))
        assertEquals(first, order.moveInside(second, forward = false))
    }

    @Test
    fun theEdgeOfARowHasNoNeighbour() {
        val order = TvFocusOrder()
        val first = FocusRequester()
        order.register("row", first)

        assertNull(order.moveInside(first, forward = true))
        assertNull(order.moveInside(first, forward = false))
    }

    @Test
    fun theFocusGoesToTheRowAboveAndBelow() {
        val order = TvFocusOrder()
        val upper = FocusRequester()
        val lower = FocusRequester()
        order.register("upper", upper)
        order.register("lower", lower)

        assertEquals(lower, order.moveToRow(upper, up = false))
        assertEquals(upper, order.moveToRow(lower, up = true))
    }

    @Test
    fun theFirstRowHasNoRowAbove() {
        val order = TvFocusOrder()
        val upper = FocusRequester()
        order.register("upper", upper)

        assertNull(order.moveToRow(upper, up = true))
    }

    @Test
    fun anEntryThatLeftTheScreenIsForgotten() {
        val order = TvFocusOrder()
        val entry = FocusRequester()
        order.register("row", entry)
        order.unregister("row", entry)

        assertNull(order.moveInside(entry, forward = true))
        assertNull(order.where(entry))
    }

    @Test
    fun theOrderForgetsEveryRowWhenTheScreenChanges() {
        val order = TvFocusOrder()
        val entry = FocusRequester()
        order.register("row", entry)

        order.clear()

        assertNull(order.where(entry))
    }
}
