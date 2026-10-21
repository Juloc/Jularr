package de.juloc.jularr.tv

import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.remember
import androidx.compose.ui.focus.FocusRequester

/**
 * The focus of one entry of the television. The entry takes its place in the order of its area,
 * so the D-pad walks the reading order instead of searching for the nearest control.
 */
@Composable
fun rememberTvFocusEntry(
    controller: TvController,
    area: TvFocusArea,
    key: String,
): FocusRequester {
    val requester = remember(key) { FocusRequester() }
    // The entry takes its place while the screen is composed, so the order is complete before
    // the D-pad asks for the next entry.
    remember(area, requester) {
        controller.focusOrder.register(area, requester)
        requester
    }
    DisposableEffect(requester) {
        onDispose { controller.focusOrder.unregister(area, requester) }
    }
    return requester
}

/**
 * The focus of an entry of the rail. The rail knows its entries, so the content can hand the
 * focus back to the surface the viewer is on.
 */
@Composable
fun rememberRailEntry(controller: TvController, route: TvRoute, key: String): FocusRequester {
    val requester = rememberTvFocusEntry(controller, TvFocusArea.Rail, key)
    remember(route, requester) {
        controller.registerRailEntry(route, requester)
        requester
    }
    return requester
}
