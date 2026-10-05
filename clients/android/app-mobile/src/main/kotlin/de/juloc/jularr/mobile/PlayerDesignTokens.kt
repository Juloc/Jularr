package de.juloc.jularr.mobile

import android.content.Context
import androidx.compose.ui.graphics.Color
import de.juloc.jularr.core.design.PlayerDesignConfigLoader
import de.juloc.jularr.core.design.PlayerSeekSteps

data class MobilePlayerDesign(
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
    val subtitlePreferredSp: Int,
    val controlsAutoHideMs: Long,
    val seek: PlayerSeekSteps,
)

object MobilePlayerDesignLoader {
    fun load(context: Context): MobilePlayerDesign {
        val config = PlayerDesignConfigLoader.load(context)
        return MobilePlayerDesign(
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
            subtitlePreferredSp = config.subtitlePreferredSp,
            controlsAutoHideMs = config.controlsAutoHideMs,
            seek = config.seek,
        )
    }
}
