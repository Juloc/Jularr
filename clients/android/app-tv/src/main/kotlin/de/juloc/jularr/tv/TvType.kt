package de.juloc.jularr.tv

import androidx.compose.runtime.Composable
import androidx.compose.runtime.remember
import androidx.compose.runtime.staticCompositionLocalOf
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.unit.Density
import androidx.compose.ui.unit.TextUnit
import androidx.compose.ui.unit.sp
import de.juloc.jularr.core.design.DesignTokens

/**
 * The sizes of the type scale of the shared tokens, grown to the scale of the television.
 * The tokens carry the sizes of the desktop; the television shows them larger.
 */
class TvType(private val tokens: DesignTokens?, private val density: Density) {
    private fun size(name: String, fallback: Float): TextUnit =
        with(density) { TvLayout.dp((tokens?.type?.get(name) ?: fallback.toInt()).toFloat()).toSp() }

    val body: TextUnit = size("body", 14f)
    val cardTitle: TextUnit = size("cardTitle", 14f)
    val cardMeta: TextUnit = size("cardMeta", 12f)
    val shelfHeading: TextUnit = size("shelfHeading", 20f)
    val heroTitle: TextUnit = size("heroTitle", 28f)
    val pageTitle: TextUnit = size("pageTitle", 28f)
}

/** The type scale of the running screen. */
val LocalTvType = staticCompositionLocalOf { TvType(null, Density(1f)) }

/** Reads the type scale of the tokens for the screen. */
@Composable
fun rememberTvType(tokens: DesignTokens?): TvType {
    val density = LocalDensity.current
    return remember(tokens, density) { TvType(tokens, density) }
}
