package de.juloc.jularr.tv

import androidx.compose.foundation.border
import androidx.compose.runtime.Composable
import androidx.compose.runtime.remember
import androidx.compose.ui.Modifier
import androidx.compose.ui.focus.onFocusChanged
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.RectangleShape
import androidx.compose.ui.graphics.Shape
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.unit.dp

@Composable
fun rememberTvFocusColor(): Color {
    val context = LocalContext.current
    return remember(context) {
        TvPlayerDesignLoader.load(context).focus
    }
}

fun Modifier.tvFocusIndication(
    focused: Boolean,
    color: Color = Color(0xFF7B61FF),
    shape: Shape = RectangleShape,
): Modifier =
    if (focused) {
        this
            .border(
                width = 3.dp,
                color = color,
                shape = shape,
            )
            .border(
                width = 6.dp,
                color = color.copy(alpha = 0.45f),
                shape = shape,
            )
    } else {
        this.border(
            width = 1.dp,
            color = Color.White.copy(alpha = 0.08f),
            shape = shape,
        )
    }

fun Modifier.reportFocus(onFocused: (Boolean) -> Unit): Modifier =
    this.onFocusChanged { onFocused(it.isFocused) }
