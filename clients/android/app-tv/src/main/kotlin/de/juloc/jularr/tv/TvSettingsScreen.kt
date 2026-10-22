package de.juloc.jularr.tv

import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.interaction.MutableInteractionSource
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.focus.focusRequester
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp

/**
 * The settings of the television: the skin and the brightness of the device and the end of
 * its pairing. They belong to this device, not to the profile.
 */
@Composable
fun TvSettingsScreen(controller: TvController) {
    val colors = LocalTvColors.current
    val type = LocalTvType.current
    LazyColumn(
        modifier = Modifier.fillMaxSize(),
        verticalArrangement = Arrangement.spacedBy(TvLayout.Gap),
    ) {
        item {
            Text(
                text = controller.texts["tv.settings.title"],
                fontSize = type.shelfHeading,
                fontWeight = FontWeight.Bold,
                color = colors.text,
            )
        }
        item {
            TvSettingRow(
                controller = controller,
                title = controller.texts["tv.settings.skin"],
                value = controller.skin,
                onClick = {
                    controller.useSkin(if (controller.skin == "clean") "original" else "clean")
                },
            )
        }
        item {
            TvSettingRow(
                controller = controller,
                title = controller.texts["tv.settings.brightness"],
                value = controller.texts["common.yes"].takeIf { controller.dark } ?: controller.texts["common.no"],
                onClick = { controller.useDark(!controller.dark) },
            )
        }
        item {
            TvSettingRow(
                controller = controller,
                title = controller.texts["tv.settings.language"],
                value = controller.language,
                onClick = {
                    controller.loadLocales()
                    controller.open(TvRoute.Languages)
                },
            )
        }
        item {
            // The settings of the player.
            Text(
                text = controller.texts["tv.player.settings"],
                fontSize = type.body,
                fontWeight = FontWeight.SemiBold,
                color = colors.muted,
                modifier = Modifier.padding(top = 6.dp),
            )
        }
        item {
            TvSettingRow(
                controller = controller,
                title = controller.texts["tv.player.autoplay"],
                value = controller.texts[if (controller.autoplay) "common.yes" else "common.no"],
                onClick = { controller.useAutoplay(!controller.autoplay) },
            )
        }
        item {
            TvSettingRow(
                controller = controller,
                title = controller.texts["tv.player.speed"],
                value = "${controller.speed}x",
                onClick = {
                    val next = when {
                        controller.speed >= 2f -> 0.5f
                        controller.speed >= 1.5f -> 2f
                        controller.speed >= 1.25f -> 1.5f
                        controller.speed >= 1f -> 1.25f
                        else -> 1f
                    }
                    controller.useSpeed(next)
                },
            )
        }
        item {
            TvSettingRow(
                controller = controller,
                title = controller.texts["tv.settings.forget"],
                value = "",
                onClick = { controller.forget() },
            )
        }
    }
}

/** One setting of the television: its name and its value. */
@Composable
private fun TvSettingRow(
    controller: TvController,
    title: String,
    value: String,
    onClick: () -> Unit,
) {
    val colors = LocalTvColors.current
    val type = LocalTvType.current
    var focused by remember { mutableStateOf(false) }
    val shape = RoundedCornerShape(TvLayout.RailItemRadius)
    val requester = rememberTvFocusEntry(controller, TvFocusArea.List, "setting-$title")
    Row(
        modifier = Modifier
            .fillMaxWidth()
            .height(TvLayout.RailItemHeight)
            .clip(shape)
            .background(colors.surface, shape)
            .focusRequester(requester)
            .reportFocus {
                focused = it
                if (it) {
                    controller.focusMovedTo(TvFocusArea.List)
                    controller.focusMovedTo(requester)
                } else if (controller.focusedEntry == requester) {
                    controller.focusMovedTo(null)
                }
            }
            .clickable(
                interactionSource = remember { MutableInteractionSource() },
                indication = null,
            ) { onClick() }
            .then(
                if (focused) {
                    Modifier.tvFocusRing(true, colors.focus, shape)
                } else {
                    Modifier
                },
            )
            .padding(horizontal = 14.dp),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.SpaceBetween,
    ) {
        Text(
            text = title,
            fontSize = type.body,
            fontWeight = FontWeight.Medium,
            color = if (focused) colors.accentText else colors.text,
        )
        if (value.isNotEmpty()) {
            Text(text = value, fontSize = type.body, color = colors.muted)
        }
    }
}
