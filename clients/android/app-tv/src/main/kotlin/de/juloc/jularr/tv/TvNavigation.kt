package de.juloc.jularr.tv

/**
 * TV screens. The sidebar (#522, docs/INFORMATION_ARCHITECTURE.md "TV sidebar") only ever
 * shows [Home], [Watchlist], [Activity] and [Profile] as peer destinations; there is no
 * dedicated Library, Discover or per-media-type screen. [Search] is Home's full browse/
 * discover surface, reached only by activating the search field at the top of Home.
 */
sealed interface TvRoute {
    data object Setup : TvRoute
    data object Login : TvRoute
    data object ProfileSelect : TvRoute
    data object Home : TvRoute
    data object Search : TvRoute
    data object Watchlist : TvRoute
    data object Activity : TvRoute
    data object Profile : TvRoute
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
    /** Top-level destinations selectable directly from the sidebar. */
    val sidebarRoutes: List<TvRoute> = listOf(
        TvRoute.Home,
        TvRoute.Watchlist,
        TvRoute.Activity,
        TvRoute.Profile,
    )

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

    fun connected(state: TvNavigationState): TvNavigationState =
        state.replace(TvRoute.Login)

    fun signedIn(state: TvNavigationState): TvNavigationState =
        state.replace(TvRoute.Home)

    /**
     * Sidebar destinations are peers, not a drill-in stack: switching between Home,
     * Watchlist, Activity and Profile replaces the current top-level screen instead of
     * growing the back stack, so repeated tab switching cannot pile up. Back from a
     * top-level screen leaves the app (the existing empty-stack behavior below), while
     * content pushed from within a tab (Search, Anime, Episode, Player) still restores
     * that tab on Back because it is `push`ed on top of it.
     */
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

    fun nextPlayer(
        state: TvNavigationState,
        episodeId: String,
        animeId: String,
    ): TvNavigationState {
        val previous = if (state.previous.lastOrNull() is TvRoute.Episode) {
            state.previous.dropLast(1) + TvRoute.Episode(episodeId, animeId)
        } else {
            state.previous
        }
        return TvNavigationState(TvRoute.Player(episodeId, animeId), previous)
    }

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
                TvRoute.Activity,
                TvRoute.Profile,
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
            TvRoute.Activity -> "activity"
            TvRoute.Profile -> "profile"
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
