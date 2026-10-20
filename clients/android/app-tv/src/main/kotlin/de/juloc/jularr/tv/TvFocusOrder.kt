package de.juloc.jularr.tv

import androidx.compose.ui.focus.FocusRequester

/**
 * The focus of the television follows the reading order of the layout specification: the rail,
 * then the top row, then the content, and inside the content the rows from top to bottom with
 * their entries from left to right.
 *
 * The screen registers its rows and their entries; the D-pad moves inside this order instead of
 * searching the nearest control, so the focus never jumps where the viewer does not expect it.
 */
class TvFocusOrder {
    private val rows = mutableListOf<Row>()

    private class Row(val key: String, val entries: MutableList<FocusRequester> = mutableListOf())

    /** Registers an entry of a row. */
    fun register(rowKey: String, requester: FocusRequester) {
        val row = rows.firstOrNull { it.key == rowKey } ?: Row(rowKey).also { rows += it }
        if (!row.entries.contains(requester)) {
            row.entries += requester
        }
    }

    /** Forgets an entry of a row. */
    fun unregister(rowKey: String, requester: FocusRequester) {
        rows.firstOrNull { it.key == rowKey }?.entries?.remove(requester)
    }

    /** Forgets every entry; the screen changes. */
    fun clear() {
        rows.clear()
    }

    /** The row the focus is in, and the entry of it. */
    fun where(focused: FocusRequester?): Pair<Int, Int>? {
        if (focused == null) {
            return null
        }
        rows.forEachIndexed { rowIndex, row ->
            val entry = row.entries.indexOf(focused)
            if (entry >= 0) {
                return rowIndex to entry
            }
        }
        return null
    }

    /** The entry left or right of the focus inside its row. */
    fun moveInside(focused: FocusRequester?, forward: Boolean): FocusRequester? {
        val (row, entry) = where(focused) ?: return null
        val entries = rows[row].entries
        val next = entry + if (forward) 1 else -1
        return entries.getOrNull(next)
    }

    /** The first entry of the content; the D-pad returns here from the top row. */
    fun firstEntry(): FocusRequester? = rows.firstOrNull()?.entries?.firstOrNull()

    /** The first entry of the row above or below the focus. */
    fun moveToRow(focused: FocusRequester?, up: Boolean): FocusRequester? {
        val (row, _) = where(focused) ?: return null
        val next = row + if (up) -1 else 1
        if (next < 0 || next >= rows.size) {
            return null
        }
        return rows[next].entries.firstOrNull()
    }
}
