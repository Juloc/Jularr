package de.juloc.jularr.tv

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertSame
import org.junit.Test

class TvNavigationTest {
    @Test
    fun firstLaunchWithoutServerStartsSetup() {
        assertEquals(
            TvRoute.Setup,
            TvNavigation.initial(hasServerOrigin = false).route,
        )
    }

    @Test
    fun savedServerStartsAtLogin() {
        assertEquals(
            TvRoute.Login,
            TvNavigation.initial(hasServerOrigin = true).route,
        )
    }

    @Test
    fun signingInLandsOnHomeNotLibrary() {
        val state = TvNavigation.signedIn(TvNavigationState(TvRoute.Login))
        assertEquals(TvRoute.Home, state.route)
    }

    @Test
    fun sidebarSeparatesMainNavigationFromSettingsAndProfileSwitching() {
        assertEquals(
            listOf(TvRoute.Home, TvRoute.Watchlist),
            TvNavigation.sidebarRoutes,
        )
        assertEquals(listOf(TvRoute.Settings, TvRoute.ProfileSelect), TvNavigation.bottomRoutes)
    }

    @Test
    fun homeAnimeEpisodePlayerBackStackIsRemoteFriendly() {
        var state = TvNavigationState(TvRoute.Home)
        state = TvNavigation.openAnime(state, "anime")
        state = TvNavigation.openEpisode(state, "episode", "anime")
        state = TvNavigation.openPlayer(state, "episode", "anime")

        state = TvNavigation.back(state)!!
        assertEquals(TvRoute.Episode("episode", "anime"), state.route)

        state = TvNavigation.back(state)!!
        assertEquals(TvRoute.Anime("anime"), state.route)

        state = TvNavigation.back(state)!!
        assertEquals(TvRoute.Home, state.route)

        assertNull(TvNavigation.back(state))
    }

    @Test
    fun searchOpensFromHomeAndBackReturnsToHome() {
        var state = TvNavigationState(TvRoute.Home)
        state = TvNavigation.openSearch(state)
        assertEquals(TvRoute.Search, state.route)

        state = TvNavigation.back(state)!!
        assertEquals(TvRoute.Home, state.route)
    }

    @Test
    fun animeOpenedFromSearchReturnsToSearchOnBack() {
        var state = TvNavigationState(TvRoute.Home)
        state = TvNavigation.openSearch(state)
        state = TvNavigation.openAnime(state, "anime")

        state = TvNavigation.back(state)!!
        assertEquals(TvRoute.Search, state.route)
    }

    @Test
    fun switchingSidebarTabsReplacesInsteadOfStacking() {
        var state = TvNavigationState(TvRoute.Home)
        state = TvNavigation.openSidebarRoute(state, TvRoute.Watchlist)
        state = TvNavigation.openSidebarRoute(state, TvRoute.Settings)

        assertEquals(TvRoute.Settings, state.route)
        assertEquals(emptyList<TvRoute>(), state.previous)
        assertNull(TvNavigation.back(state))
    }

    @Test
    fun selectingTheAlreadyActiveSidebarRouteIsANoOp() {
        val state = TvNavigationState(TvRoute.Watchlist)
        val result = TvNavigation.openSidebarRoute(state, TvRoute.Watchlist)
        assertSame(state, result)
    }

    @Test
    fun backFromContentPushedOverAnySidebarTabReturnsToThatTab() {
        var state = TvNavigationState(TvRoute.Home)
        state = TvNavigation.openSidebarRoute(state, TvRoute.Watchlist)
        state = TvNavigation.openAnime(state, "anime")

        state = TvNavigation.back(state)!!
        assertEquals(TvRoute.Watchlist, state.route)
    }

    @Test
    fun profileSwitchOpenedFromSettingsReturnsThereOnBack() {
        val next = TvNavigation.openProfileSelect(TvNavigationState(TvRoute.Settings))
        assertEquals(TvRoute.ProfileSelect, next.route)
        assertEquals(TvRoute.Settings, TvNavigation.back(next)?.route)
    }

    @Test
    fun switchingAccountsDropsTheOldSettingsBackStack() {
        val selecting = TvNavigation.openProfileSelect(TvNavigationState(TvRoute.Settings))
        val signedIn = TvNavigation.signedIn(selecting)
        assertEquals(TvRoute.Home, signedIn.route)
        assertEquals(emptyList<TvRoute>(), signedIn.previous)
    }

    @Test
    fun signOutDropsProtectedBackStack() {
        var state = TvNavigationState(TvRoute.Home)
        state = TvNavigation.openAnime(state, "anime")
        state = TvNavigation.signOut(state)

        assertEquals(TvRoute.Login, state.route)
        assertEquals(emptyList<TvRoute>(), state.previous)
    }

    @Test
    fun changingServerDropsAuthenticatedNavigation() {
        val state = TvNavigation.changeServer()

        assertEquals(TvRoute.Setup, state.route)
        assertNull(TvNavigation.back(state))
    }

    @Test
    fun screenKeysAreStablePerScreenIdentity() {
        assertEquals("home", TvNavigation.screenKey(TvRoute.Home))
        assertEquals("search", TvNavigation.screenKey(TvRoute.Search))
        assertEquals("watchlist", TvNavigation.screenKey(TvRoute.Watchlist))
        assertEquals("settings", TvNavigation.screenKey(TvRoute.Settings))
        assertEquals(
            "anime:one",
            TvNavigation.screenKey(TvRoute.Anime("one")),
        )
        assertEquals(
            TvNavigation.screenKey(TvRoute.Anime("one")),
            TvNavigation.screenKey(TvRoute.Anime("one")),
        )
    }
}
