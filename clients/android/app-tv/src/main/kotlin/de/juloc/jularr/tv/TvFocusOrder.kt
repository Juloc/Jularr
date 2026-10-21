package de.juloc.jularr.tv

import androidx.compose.ui.focus.FocusRequester

/**
 * The order of the focus on the television. Every area of the screen keeps its own chain: the
 * reader walks the entries of an area in the reading order, and the areas hand over to each
 * other at their edges. The chain of the content follows the rows of the screen from top to
 * bottom and the entries of a row from left to right.
 */
class TvFocusOrder {
    private val areas = mutableMapOf<TvFocusArea, MutableList<FocusRequester>>()

    /** The number of the entries that stand in the order. */
    val size: Int
        get() = areas.values.sumOf { it.size }

    private fun entries(area: TvFocusArea): MutableList<FocusRequester> =
        areas.getOrPut(area) { mutableListOf() }

    /** An entry takes its place in the order of its area. */
    fun register(area: TvFocusArea, requester: FocusRequester) {
        val entries = entries(area)
        if (!entries.contains(requester)) {
            entries += requester
        }
    }

    /** An entry leaves the order. */
    fun unregister(area: TvFocusArea, requester: FocusRequester) {
        areas[area]?.remove(requester)
    }

    /** Every entry leaves the order; the screen changes. */
    fun clear() {
        areas.clear()
    }

    /** The entry after the given one. */
    fun nextOf(area: TvFocusArea, requester: FocusRequester?): FocusRequester? {
        val entries = areas[area] ?: return null
        val index = entries.indexOf(requester ?: return null)
        return if (index < 0) null else entries.getOrNull(index + 1)
    }

    /** The entry before the given one. */
    fun previousOf(area: TvFocusArea, requester: FocusRequester?): FocusRequester? {
        val entries = areas[area] ?: return null
        val index = entries.indexOf(requester ?: return null)
        return if (index <= 0) null else entries[index - 1]
    }

    /** The first entry of an area. */
    fun firstOf(area: TvFocusArea): FocusRequester? = areas[area]?.firstOrNull()
}
