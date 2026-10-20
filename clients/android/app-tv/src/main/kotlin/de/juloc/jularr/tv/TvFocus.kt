package de.juloc.jularr.tv

import androidx.compose.foundation.border
import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import androidx.compose.ui.focus.onFocusChanged
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.Shape

/**
 * Remembers which item was last focused on each TV screen, so a screen that is entered again
 * starts where the viewer left it.
 */
class TvFocusMemory {
    private val focusedItemIds = mutableMapOf<String, String>()

    fun remember(screenKey: String, itemId: String) {
        focusedItemIds[screenKey] = itemId
    }

    fun recall(screenKey: String): String? = focusedItemIds[screenKey]

    fun forget(screenKey: String) {
        focusedItemIds.remove(screenKey)
    }
}

/**
 * The focus ring of the desktop: the accent's text color, a thin border without a glow. The
 * focused card of the desktop grows a little; the television keeps that.
 */
fun Modifier.tvFocusRing(focused: Boolean, color: Color, shape: Shape): Modifier =
    if (focused) {
        this.border(width = TvLayout.FocusRing, color = color, shape = shape)
    } else {
        this
    }

/** Reports the focus of a control to its screen. */
fun Modifier.reportFocus(onFocused: (Boolean) -> Unit): Modifier =
    this.onFocusChanged { onFocused(it.isFocused) }
