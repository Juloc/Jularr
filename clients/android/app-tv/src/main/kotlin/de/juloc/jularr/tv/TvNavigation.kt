package de.juloc.jularr.tv

/** Sidebar destinations retained by the original TV Compose surface.
 * All active /api/v1 navigation and Back stack state lives in TvV1Navigation.
 */
sealed interface TvRoute {
    data object Home : TvRoute
    data object Watchlist : TvRoute
    data object Settings : TvRoute
    data object ProfileSelect : TvRoute
}
