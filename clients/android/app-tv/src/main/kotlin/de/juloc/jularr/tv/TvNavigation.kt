package de.juloc.jularr.tv

sealed interface TvRoute {
    data object Setup : TvRoute
    data object Login : TvRoute
    data object ProfileSelect : TvRoute
    data object Home : TvRoute
    data object Search : TvRoute
    data object Watchlist : TvRoute
    data object Settings : TvRoute
    data class Anime(val animeId: String) : TvRoute
    data class Episode(
        val episodeId: String,
        val animeId: String,
    ) : TvRoute
    data class Player(
        val episodeId: String,
        val animeId: String,
    ) : TvRoute
}

data class TvNavigationState(
    val route: TvRoute,
    val previous: List<TvRoute> = emptyList(),
)

object TvNavigation {
    val sidebarRoutes: List<TvRoute> = listOf(TvRoute.Home, TvRoute.Watchlist)
    val bottomRoutes: List<TvRoute> = listOf(TvRoute.Settings, TvRoute.ProfileSelect)

    fun initial(hasServerOrigin: Boolean, hasMultipleSessions: Boolean = false): TvNavigationState =
        TvNavigationState(
            route = when {
                hasMultipleSessions -> TvRoute.ProfileSelect
                hasServerOrigin -> TvRoute.Login
                else -> TvRoute.Setup
            },
        )

    fun profileSelect(): TvNavigationState =
        TvNavigationState(TvRoute.ProfileSelect)

    fun openProfileSelect(state: TvNavigationState): TvNavigationState =
        state.push(TvRoute.ProfileSelect)

    fun connected(state: TvNavigationState): TvNavigationState =
        state.replace(TvRoute.Login)

    fun signedIn(state: TvNavigationState): TvNavigationState =
        TvNavigationState(TvRoute.Home)

    /** Sidebar navigation replaces peers rather than building an ever-growing stack. */
    fun openSidebarRoute(
        state: TvNavigationState,
        route: TvRoute,
    ): TvNavigationState =
        if (state.route == route) state else TvNavigationState(route)

    fun openSearch(state: TvNavigationState): TvNavigationState =
        state.push(TvRoute.Search)

    fun openAnime(
        state: TvNavigationState,
        animeId: String,
    ): TvNavigationState =
        state.push(TvRoute.Anime(animeId))

    fun openEpisode(
        state: TvNavigationState,
        episodeId: String,
        animeId: String,
    ): TvNavigationState =
        state.push(TvRoute.Episode(episodeId, animeId))

    fun openPlayer(
        state: TvNavigationState,
        episodeId: String,
        animeId: String,
    ): TvNavigationState =
        state.push(TvRoute.Player(episodeId, animeId))

    fun signOut(state: TvNavigationState): TvNavigationState =
        TvNavigationState(TvRoute.Login)

    fun changeServer(): TvNavigationState =
        TvNavigationState(TvRoute.Setup)

    fun back(state: TvNavigationState): TvNavigationState? {
        if (state.previous.isEmpty()) {
            return when (state.route) {
                TvRoute.Setup,
                TvRoute.Login,
                TvRoute.ProfileSelect,
                TvRoute.Home,
                TvRoute.Search,
                TvRoute.Watchlist,
                TvRoute.Settings,
                -> null

                is TvRoute.Anime -> TvNavigationState(TvRoute.Home)
                is TvRoute.Episode -> TvNavigationState(
                    TvRoute.Anime(state.route.animeId),
                )
                is TvRoute.Player -> TvNavigationState(
                    TvRoute.Episode(
                        episodeId = state.route.episodeId,
                        animeId = state.route.animeId,
                    ),
                )
            }
        }

        return TvNavigationState(
            route = state.previous.last(),
            previous = state.previous.dropLast(1),
        )
    }

    /**
     * Stable per-screen key for [TvFocusMemory]: identifies "the screen" independent of
     * which instance of it is showing, so remembering the focused row on Home or the
     * focused card in Search survives navigating away and back.
     */
    fun screenKey(route: TvRoute): String =
        when (route) {
            TvRoute.Setup -> "setup"
            TvRoute.Login -> "login"
            TvRoute.ProfileSelect -> "profile_select"
            TvRoute.Home -> "home"
            TvRoute.Search -> "search"
            TvRoute.Watchlist -> "watchlist"
            TvRoute.Settings -> "settings"
            is TvRoute.Anime -> "anime:${route.animeId}"
            is TvRoute.Episode -> "episode:${route.episodeId}"
            is TvRoute.Player -> "player:${route.episodeId}"
        }

    private fun TvNavigationState.push(route: TvRoute): TvNavigationState =
        TvNavigationState(
            route = route,
            previous = previous + this.route,
        )

    private fun TvNavigationState.replace(route: TvRoute): TvNavigationState =
        copy(route = route)
}
