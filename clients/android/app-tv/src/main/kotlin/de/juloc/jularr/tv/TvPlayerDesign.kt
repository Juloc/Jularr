package de.juloc.jularr.tv

import android.content.Context
import androidx.compose.ui.graphics.Color
import de.juloc.jularr.core.design.PlayerDesignConfigLoader
import de.juloc.jularr.core.design.PlayerSeekSteps

data class TvPlayerDesign(
    val overlay: Color,
    val sheet: Color,
    val subtitleText: Color,
    val subtitleBackground: Color,
    val accent: Color,
    val muted: Color,
    val focus: Color,
    val controlRadiusDp: Int,
    val sheetRadiusDp: Int,
    val spacingSmallDp: Int,
    val spacingMediumDp: Int,
    val spacingLargeDp: Int,
    val controlSizeDp: Int,
    val subtitlePreferredSp: Int,
    val controlsAutoHideMs: Long,
    val seek: PlayerSeekSteps,
)

object TvPlayerDesignLoader {
    fun load(context: Context): TvPlayerDesign {
        val config = PlayerDesignConfigLoader.load(context)
        return TvPlayerDesign(
            overlay = Color(config.overlayColor),
            sheet = Color(config.sheetColor),
            subtitleText = Color(config.subtitleTextColor),
            subtitleBackground = Color(config.subtitleBackgroundColor),
            accent = Color(config.accentColor),
            muted = Color(config.mutedColor),
            focus = Color(config.focusColor),
            controlRadiusDp = config.controlRadiusDp,
            sheetRadiusDp = config.sheetRadiusDp,
            spacingSmallDp = config.spacingSmallDp,
            spacingMediumDp = config.spacingMediumDp,
            spacingLargeDp = config.spacingLargeDp,
            controlSizeDp = config.tvControlSizeDp,
            subtitlePreferredSp = config.subtitlePreferredSp,
            controlsAutoHideMs = config.controlsAutoHideMs,
            seek = config.seek,
        )
    }
}
