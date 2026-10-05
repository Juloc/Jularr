package de.juloc.jularr.core.design

import android.content.Context
import android.graphics.Color
import org.json.JSONObject

/**
 * Manual seek increments of every client, from `playback` in design/player/player-tokens.json. Back and forward
 * are deliberately different (10 s back, 30 s forward); the command ids seekBack10/seekForward10 are stable
 * identifiers and carry no duration.
 */
data class PlayerSeekSteps(
    val backSeconds: Int,
    val forwardSeconds: Int,
) {
    val backMs: Long get() = backSeconds * 1000L
    val forwardMs: Long get() = forwardSeconds * 1000L

    companion object {
        fun fromTokens(tokens: JSONObject): PlayerSeekSteps {
            val playback = tokens.getJSONObject("playback")
            val steps = PlayerSeekSteps(playback.getInt("seekBackSeconds"), playback.getInt("seekForwardSeconds"))
            require(steps.backSeconds > 0 && steps.forwardSeconds > 0) { "Seek increments must be positive." }
            return steps
        }
    }
}

data class PlayerDesignConfig(
    val overlayColor: Int,
    val sheetColor: Int,
    val subtitleTextColor: Int,
    val subtitleBackgroundColor: Int,
    val accentColor: Int,
    val mutedColor: Int,
    val focusColor: Int,
    val controlRadiusDp: Int,
    val sheetRadiusDp: Int,
    val spacingSmallDp: Int,
    val spacingMediumDp: Int,
    val spacingLargeDp: Int,
    val touchControlSizeDp: Int,
    val tvControlSizeDp: Int,
    val subtitlePreferredSp: Int,
    val controlsAutoHideMs: Long,
    val seek: PlayerSeekSteps,
)

object PlayerDesignConfigLoader {
    fun load(context: Context): PlayerDesignConfig {
        val json = context.assets
            .open(PlayerDesignAssets.Tokens)
            .bufferedReader()
            .use { JSONObject(it.readText()) }

        val colors = json.getJSONObject("colors")
        val radius = json.getJSONObject("radiusDp")
        val spacing = json.getJSONObject("spacingDp")
        val controlSize = json.getJSONObject("controlSizeDp")
        val typography = json.getJSONObject("typographySp")
        val timing = json.getJSONObject("timingMs")

        return PlayerDesignConfig(
            overlayColor = colors.color("overlay"),
            sheetColor = colors.color("sheet"),
            subtitleTextColor = colors.color("subtitleText"),
            subtitleBackgroundColor = colors.color("subtitleBackground"),
            accentColor = colors.color("accent"),
            mutedColor = colors.color("muted"),
            focusColor = colors.color("focus"),
            controlRadiusDp = radius.getInt("control"),
            sheetRadiusDp = radius.getInt("sheet"),
            spacingSmallDp = spacing.getInt("sm"),
            spacingMediumDp = spacing.getInt("md"),
            spacingLargeDp = spacing.getInt("lg"),
            touchControlSizeDp = controlSize.getInt("touch"),
            tvControlSizeDp = controlSize.getInt("tv"),
            subtitlePreferredSp = typography.getInt("subtitlePreferred"),
            controlsAutoHideMs = timing.getLong("controlsAutoHide"),
            seek = PlayerSeekSteps.fromTokens(json),
        )
    }

    private fun JSONObject.color(name: String): Int =
        Color.parseColor(getString(name))
}
