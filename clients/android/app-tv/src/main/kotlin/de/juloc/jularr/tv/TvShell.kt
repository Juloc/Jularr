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
import androidx.compose.foundation.layout.size
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
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Search
import androidx.compose.material3.Icon
import androidx.compose.ui.draw.clip
import androidx.compose.ui.focus.FocusRequester
import androidx.compose.ui.focus.focusRequester
import androidx.compose.ui.input.key.onKeyEvent
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
    // The search of the top row; the D-pad reaches it from the content.
    val topRowFocus = remember { FocusRequester() }
    CompositionLocalProvider(
        LocalTvColors provides colors,
        LocalTvType provides rememberTvType(controller.designTokens),
    ) {
        Row(
            modifier = Modifier
                .fillMaxSize()
                .onKeyEvent { event ->
                    // The reading order: the rail, then the top row, then the content. Inside
                    // the content the D-pad walks the rows of the screen.
                    val native = event.nativeKeyEvent
                    if (native.action != android.view.KeyEvent.ACTION_DOWN) {
                        return@onKeyEvent false
                    }
                    when (native.keyCode) {
                        android.view.KeyEvent.KEYCODE_DPAD_LEFT ->
                            controller.moveFocus(TvFocusDirection.Left)

                        android.view.KeyEvent.KEYCODE_DPAD_RIGHT ->
                            controller.moveFocus(TvFocusDirection.Right)

                        android.view.KeyEvent.KEYCODE_DPAD_UP ->
                            controller.moveFocus(TvFocusDirection.Up) ||
                                enterTopRow(controller, topRowFocus)

                        android.view.KeyEvent.KEYCODE_DPAD_DOWN ->
                            controller.moveFocus(TvFocusDirection.Down)

                        else -> false
                    }
                },
        ) {
            if (controller.route != TvRoute.Search) {
                TvRail(controller)
            }
            Column(modifier = Modifier.weight(1f).fillMaxHeight()) {
                // The search has no other chrome: the field and the keyboard are the screen.
                if (controller.route != TvRoute.Search) {
                    TvTopRow(controller, topRowFocus)
                }
                Box(
                    modifier = Modifier
                        .weight(1f)
                        .fillMaxWidth()
                        .padding(TvLayout.ContentPadding),
                ) {
                    when (controller.route) {
                        TvRoute.Home -> {
                            TvHomeScreen(controller)
                            if (controller.filterOpen) {
                                TvFilterDialog(controller) { controller.filterOpen = false }
                            }
                        }
                        TvRoute.Watchlist -> TvCardGrid(
                            title = controller.texts["watchlist.title"],
                            cards = controller.watchlist,
                            empty = controller.texts["tv.empty.watchlist"],
                            controller = controller,
                        )

                        TvRoute.Library -> TvCardGrid(
                            title = controller.texts["nav.library"],
                            cards = controller.library,
                            empty = controller.texts["tv.empty.library"],
                            controller = controller,
                        )

                        TvRoute.Profiles -> TvProfilesScreen(controller)
                        TvRoute.Settings -> TvSettingsScreen(controller)
                        TvRoute.Search -> TvSearchScreen(controller)
                        TvRoute.Details -> TvDetailsScreen(controller)
                        TvRoute.Player -> TvPlayerScreen(controller)
                        TvRoute.Languages -> TvLanguagesScreen(controller)
                        TvRoute.Collections -> {
                            controller.loadCollections()
                            TvCollectionsScreen(controller)
                        }
                    }
                }
            }
        }
    }
}

/**
 * The D-pad's up leaves the content for the top row; the magnifier of the search waits there.
 */
private fun enterTopRow(controller: TvController, focus: FocusRequester): Boolean {
    if (controller.focusArea != TvFocusArea.Content) {
        return false
    }
    controller.focusMovedTo(TvFocusArea.TopRow)
    focus.requestFocus()
    return true
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
        TvRailLink(controller, TvRoute.Collections, controller.texts["collections.title"])
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
            .reportFocus {
                focused = it
                if (it) {
                    controller.focusMovedTo(TvFocusArea.Rail)
                }
            }
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
 * The top row: the place of the current page, the search of the instance and the profile.
 * The search opens with the magnifier of the desktop.
 */
@Composable
private fun TvTopRow(controller: TvController, focus: FocusRequester) {
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
            text = if (controller.route == TvRoute.Player) {
                controller.playingTitle
            } else {
                controller.texts[locationKey(controller.route)]
            },
            fontSize = type.body,
            fontWeight = FontWeight.SemiBold,
            color = colors.text,
        )
        Spacer(Modifier.weight(1f))
        // The magnifier of the desktop opens the search of the instance.
        if (controller.route != TvRoute.Search) {
            var focused by remember { mutableStateOf(false) }
            val shape = RoundedCornerShape(TvLayout.RailItemRadius)
            Box(
                modifier = Modifier
                    .size(TvLayout.RailItemHeight)
                    .clip(shape)
                    .focusRequester(focus)
                    .reportFocus {
                        focused = it
                        if (it) {
                            controller.focusMovedTo(TvFocusArea.TopRow)
                        }
                    }
                    .clickable(
                        interactionSource = remember { MutableInteractionSource() },
                        indication = null,
                    ) { controller.openSearch() }
                    .then(
                        if (focused) {
                            Modifier.tvFocusRing(true, colors.focus, shape)
                        } else {
                            Modifier
                        },
                    ),
                contentAlignment = Alignment.Center,
            ) {
                Icon(
                    imageVector = Icons.Filled.Search,
                    contentDescription = controller.texts["search.global"],
                    tint = if (focused) colors.accentText else colors.muted,
                    modifier = Modifier.size(TvLayout.AvailabilitySymbol),
                )
            }
        }
    }
}

/** The text key of the current page. */
internal fun locationKey(route: TvRoute): String = when (route) {
    TvRoute.Home -> "nav.home"
    TvRoute.Watchlist -> "watchlist.title"
    TvRoute.Library -> "nav.library"
    TvRoute.Profiles -> "tv.profiles.title"
    TvRoute.Settings -> "tv.settings.title"
    TvRoute.Search -> "search.global"
    TvRoute.Details -> "media.details"
    TvRoute.Player -> "media.play"
    TvRoute.Languages -> "tv.settings.language"
    TvRoute.Collections -> "collections.title"
}
