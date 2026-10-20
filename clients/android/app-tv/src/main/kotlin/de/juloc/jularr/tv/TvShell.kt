package de.juloc.jularr.tv

import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.interaction.MutableInteractionSource
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.focus.FocusRequester
import androidx.compose.ui.focus.focusRequester
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp

/**
 * The shell of the television: the rail with the surfaces, the top row and the content. It is
 * the shell of the desktop, at the scale of the television.
 */
@Composable
fun TvShell(controller: TvController) {
    val colors = LocalTvColors.current
    CompositionLocalProvider(
        LocalTvColors provides colors,
        LocalTvType provides rememberTvType(controller.designTokens),
    ) {
        Row(modifier = Modifier.fillMaxSize()) {
            TvRail(controller)
            Column(modifier = Modifier.weight(1f).fillMaxHeight()) {
                TvTopRow(controller)
                Box(
                    modifier = Modifier
                        .weight(1f)
                        .fillMaxWidth()
                        .padding(TvLayout.ContentPadding),
                ) {
                    when (controller.route) {
                        TvRoute.Home -> TvHomeScreen(controller)
                        TvRoute.Watchlist -> TvCardGrid(
                            title = controller.texts["watchlist.title"],
                            cards = controller.watchlist,
                            empty = controller.texts["tv.empty.watchlist"],
                        )

                        TvRoute.Library -> TvCardGrid(
                            title = controller.texts["nav.library"],
                            cards = controller.library,
                            empty = controller.texts["tv.empty.library"],
                        )

                        TvRoute.Profiles -> TvProfilesScreen(controller)
                        TvRoute.Settings -> TvSettingsScreen(controller)
                    }
                }
            }
        }
    }
}

/**
 * The rail: the surfaces of the television and, below them, the profile and the settings of
 * the app.
 */
@Composable
private fun TvRail(controller: TvController) {
    val colors = LocalTvColors.current
    val type = LocalTvType.current
    // The rail holds the focus when the shell opens, so the D-pad starts at the first entry.
    val homeFocus = remember { FocusRequester() }
    LaunchedEffect(controller.route) {
        runCatching { homeFocus.requestFocus() }
    }
    Column(
        modifier = Modifier
            .width(TvLayout.RailWidth)
            .fillMaxHeight()
            .background(colors.surface)
            .padding(horizontal = 6.dp, vertical = 11.dp),
        verticalArrangement = Arrangement.spacedBy(2.dp),
    ) {
        Text(
            text = "Jularr",
            fontSize = type.shelfHeading,
            fontWeight = FontWeight.ExtraBold,
            color = colors.text,
            modifier = Modifier.padding(horizontal = 8.dp, vertical = 4.dp),
        )
        Spacer(Modifier.height(TvLayout.Gap))
        TvRailLink(controller, TvRoute.Home, controller.texts["nav.home"], homeFocus)
        TvRailLink(controller, TvRoute.Watchlist, controller.texts["watchlist.title"])
        TvRailLink(controller, TvRoute.Library, controller.texts["nav.library"])
        Spacer(Modifier.weight(1f))
        TvRailLink(controller, TvRoute.Profiles, controller.profileName)
        TvRailLink(controller, TvRoute.Settings, controller.texts["tv.settings.title"])
    }
}

/** One entry of the rail. */
@Composable
private fun TvRailLink(controller: TvController, route: TvRoute, title: String, focus: FocusRequester? = null) {
    val colors = LocalTvColors.current
    val type = LocalTvType.current
    val active = controller.route == route
    var focused by remember { mutableStateOf(false) }
    val shape = RoundedCornerShape(TvLayout.RailItemRadius)
    Row(
        modifier = Modifier
            .fillMaxWidth()
            .height(TvLayout.RailItemHeight)
            .clip(shape)
            .background(if (active) colors.accent else Color.Transparent)
            .then(
                if (focus != null) {
                    Modifier.focusRequester(focus)
                } else {
                    Modifier
                },
            )
            .reportFocus { focused = it }
            .clickable(
                interactionSource = remember { MutableInteractionSource() },
                indication = null,
            ) { controller.open(route) }
            .then(
                if (focused) {
                    Modifier.tvFocusRing(true, colors.focus, shape)
                } else {
                    Modifier
                },
            )
            .padding(horizontal = 14.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Text(
            text = title,
            fontSize = type.body,
            fontWeight = if (active) FontWeight.Bold else FontWeight.Medium,
            color = if (active) colors.accentContrast else if (focused) colors.text else colors.muted,
        )
    }
}

/**
 * The top row: the place of the current page and the profile of the instance. The search
 * opens from the magnifier of the desktop and follows in the search package.
 */
@Composable
private fun TvTopRow(controller: TvController) {
    val colors = LocalTvColors.current
    val type = LocalTvType.current
    Row(
        modifier = Modifier
            .fillMaxWidth()
            .height(TvLayout.TopRowHeight)
            .padding(horizontal = 12.dp, vertical = 6.dp)
            .background(colors.surface, RoundedCornerShape(TvLayout.TopRowRadius))
            .border(1.dp, colors.border, RoundedCornerShape(TvLayout.TopRowRadius))
            .padding(horizontal = 18.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Text(
            text = controller.texts[locationKey(controller.route)],
            fontSize = type.body,
            fontWeight = FontWeight.SemiBold,
            color = colors.text,
        )
    }
}

/** The text key of the current page. */
internal fun locationKey(route: TvRoute): String = when (route) {
    TvRoute.Home -> "nav.home"
    TvRoute.Watchlist -> "watchlist.title"
    TvRoute.Library -> "nav.library"
    TvRoute.Profiles -> "tv.profiles.title"
    TvRoute.Settings -> "tv.settings.title"
}
