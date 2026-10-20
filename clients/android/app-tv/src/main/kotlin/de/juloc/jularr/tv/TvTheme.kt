package de.juloc.jularr.tv

import androidx.compose.runtime.Composable
import androidx.compose.runtime.Immutable
import androidx.compose.runtime.staticCompositionLocalOf
import androidx.compose.ui.graphics.Color
import de.juloc.jularr.core.design.DesignTokens

/**
 * The colors of the skin the instance serves. The client reads them from the shared design
 * tokens; it carries no color of its own.
 */
@Immutable
data class TvColors(
    val background: Color,
    val surface: Color,
    val field: Color,
    val border: Color,
    val text: Color,
    val muted: Color,
    val accent: Color,
    val accentContrast: Color,
    val accentText: Color,
    val accentHover: Color,
    val danger: Color,
    val success: Color,
) {
    /** The focus ring of the desktop is the accent's text color. */
    val focus: Color get() = accentText
}

/** The colors of the running screen. */
val LocalTvColors = staticCompositionLocalOf { neutralColors() }

/**
 * Reads the palette of a skin. Before the instance answered, the app shows a neutral
 * placeholder; the colors of a skin come from the tokens alone.
 */
fun DesignTokens.colors(skin: String, dark: Boolean): TvColors {
    val palette = skin(skin)
    fun color(name: String, fallback: String): Color = parse(palette.color(name, dark) ?: fallback)
    return TvColors(
        background = color("background", if (dark) "#141319" else "#f6f5f9"),
        surface = color("surface", if (dark) "#1d1b24" else "#ffffff"),
        field = color("field", if (dark) "#19171f" else "#faf9fc"),
        border = color("border", if (dark) "#393441" else "#dedbe7"),
        text = color("text", if (dark) "#f4f0f8" else "#25202f"),
        muted = color("muted", if (dark) "#b2aaba" else "#696174"),
        accent = color("accent", "#7c3aed"),
        accentContrast = color("accentContrast", "#ffffff"),
        accentText = color("accentText", if (dark) "#bea2ff" else "#7041c9"),
        accentHover = color("accentHover", "#692aca"),
        danger = color("danger", if (dark) "#ff9aa7" else "#b12b3c"),
        success = color("success", if (dark) "#7fe0a9" else "#1d7a4c"),
    )
}

/** The neutral placeholder before the instance answered. */
fun neutralColors(): TvColors = TvColors(
    background = Color(0xFF141319),
    surface = Color(0xFF1D1B24),
    field = Color(0xFF19171F),
    border = Color(0xFF393441),
    text = Color(0xFFF4F0F8),
    muted = Color(0xFFB2AABA),
    accent = Color(0xFF7C3AED),
    accentContrast = Color(0xFFFFFFFF),
    accentText = Color(0xFFBEA2FF),
    accentHover = Color(0xFF692ACA),
    danger = Color(0xFFFF9AA7),
    success = Color(0xFF7FE0A9),
)

/**
 * The color of the availability of a title, as the shared tokens know it. The nine states of
 * the Backend are the states of the web.
 */
fun availabilityStateName(stateId: Int?): String? = when (stateId) {
    1 -> "available"
    2 -> "partial"
    3 -> "missing"
    4 -> "downloading"
    5 -> "searching"
    6 -> "requested"
    7 -> "profileAudio"
    8 -> "otherAudio"
    9 -> "partialOtherAudio"
    else -> null
}

private fun parse(value: String): Color = runCatching { Color(android.graphics.Color.parseColor(value)) }
    .getOrDefault(Color(0xFF7C3AED))
