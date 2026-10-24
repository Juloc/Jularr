package de.juloc.jularr.tv

import org.junit.Assert.*
import org.junit.Test

/**
 * Exercises the active clean-start navigation stack with original screen destinations.
 * JVM tests do not prove actual Compose focus movement; that is in the manual TV checklist.
 */
class TvV1ParityNavigationMatrixTest {
    @Test fun pairingProfileHomeSearchEpisodePlaybackBackRestoresEveryRealIdentity() {
        var navigation = TvV1NavigationState() // The pairing/profile gate enters Home after authorization.
        assertEquals(TvV1CatalogPage.Home, navigation.page)
        navigation = TvV1Navigation.open(navigation, TvV1CatalogPage.Search)
        navigation = TvV1Navigation.open(navigation, TvV1CatalogPage.Details, workId = 741)
        navigation = TvV1Navigation.open(
            navigation, TvV1CatalogPage.Episode, workId = 741, workUnitId = 8002,
        )
        navigation = TvV1Navigation.open(
            navigation, TvV1CatalogPage.Player, workId = 741, workUnitId = 8002,
        )
        assertEquals(741L, navigation.current.workId)
        assertEquals(8002L, navigation.current.workUnitId)
        assertNull(navigation.current.personId)

        navigation = TvV1Navigation.back(navigation)
        assertEquals(TvV1CatalogPage.Episode, navigation.page)
        assertEquals(8002L, navigation.current.workUnitId)
        navigation = TvV1Navigation.back(navigation)
        assertEquals(TvV1CatalogPage.Details, navigation.page)
        assertEquals(741L, navigation.current.workId)
        assertNull(navigation.current.workUnitId)
        navigation = TvV1Navigation.back(navigation)
        assertEquals(TvV1CatalogPage.Search, navigation.page)
        navigation = TvV1Navigation.back(navigation)
        assertEquals(TvV1CatalogPage.Home, navigation.page)
        assertTrue(navigation.previous.isEmpty())
    }

    @Test fun discoverProviderAndCreditsReturnToTheExactWorkTheyCameFrom() {
        var state = TvV1Navigation.open(TvV1NavigationState(), TvV1CatalogPage.Search)
        state = TvV1Navigation.open(state, TvV1CatalogPage.Discover)
        state = TvV1Navigation.open(state, TvV1CatalogPage.Details, workId = 102)
        state = TvV1Navigation.open(state, TvV1CatalogPage.Credits, workId = 102)
        state = TvV1Navigation.open(state, TvV1CatalogPage.Person, workId = 102, personId = 12)
        state = TvV1Navigation.open(state, TvV1CatalogPage.Details, workId = 206)
        state = TvV1Navigation.back(state)
        assertEquals(TvV1CatalogPage.Person, state.page)
        assertEquals(12L, state.current.personId)
        assertEquals(102L, state.current.workId)
        state = TvV1Navigation.back(state)
        assertEquals(TvV1CatalogPage.Credits, state.page)
        assertEquals(102L, state.current.workId)
        state = TvV1Navigation.back(state)
        assertEquals(TvV1CatalogPage.Details, state.page)
        assertEquals(102L, state.current.workId)
    }

    @Test fun movieEditionsAndDirectContinuePlaybackReturnWithoutInventingAnEpisode() {
        var state = TvV1Navigation.open(TvV1NavigationState(), TvV1CatalogPage.Details, workId = 603)
        state = TvV1Navigation.open(state, TvV1CatalogPage.MovieEditions, workId = 603)
        state = TvV1Navigation.open(state, TvV1CatalogPage.Player, workId = 603)
        state = TvV1Navigation.back(state)
        assertEquals(TvV1CatalogPage.MovieEditions, state.page)
        assertNull(state.current.workUnitId)
        state = TvV1Navigation.back(state)
        assertEquals(TvV1CatalogPage.Details, state.page)
        assertEquals(603L, state.current.workId)

        val fromContinue = TvV1Navigation.open(
            TvV1NavigationState(), TvV1CatalogPage.Player, workId = 603,
        )
        assertNull(fromContinue.current.workUnitId)
        assertEquals(TvV1CatalogPage.Home, TvV1Navigation.back(fromContinue).page)
    }

    @Test fun sidebarAndProfileSwitcherNeverKeepStaleWorkOrPersonHistory() {
        var state = TvV1Navigation.open(TvV1NavigationState(), TvV1CatalogPage.Details, workId = 741)
        state = TvV1Navigation.open(state, TvV1CatalogPage.Person, workId = 741, personId = 25)
        state = TvV1Navigation.open(state, TvV1CatalogPage.Settings)
        assertTrue(state.previous.isEmpty())
        assertNull(state.current.workId)
        state = TvV1Navigation.open(state, TvV1CatalogPage.ProfileSelect)
        assertEquals(TvV1CatalogPage.Settings, TvV1Navigation.back(state).page)
        state = TvV1Navigation.open(state, TvV1CatalogPage.Home)
        assertTrue(state.previous.isEmpty())
        assertNull(state.current.personId)
        state = TvV1Navigation.open(state, TvV1CatalogPage.Watchlist)
        assertTrue(state.previous.isEmpty())
        assertEquals(TvV1CatalogPage.Home, TvV1Navigation.back(state).page)
    }

    @Test fun stackIsBoundedAndDuplicateDestinationsDoNotAccumulate() {
        var state = TvV1NavigationState()
        repeat(40) { index ->
            state = TvV1Navigation.open(
                state, TvV1CatalogPage.Details, workId = index.toLong() + 1,
            )
        }
        assertEquals(16, state.previous.size)
        val snapshot = state
        assertEquals(snapshot, TvV1Navigation.open(state, TvV1CatalogPage.Details, workId = 40))
        assertTrue(state.previous.all { it.workId == null || it.workId > 0 })
        assertThrows(IllegalArgumentException::class.java) {
            TvV1Navigation.open(state, TvV1CatalogPage.Details, workId = -7)
        }
    }

    @Test fun v1FocusMemorySeparatesScreenAndWorkKeys() {
        val focus = TvFocusMemory()
        val home = "home"
        val search = "search"
        val firstWork = "details:741"
        val secondWork = "details:742"
        focus.remember(home, "continue-9002")
        focus.remember(search, "work-741")
        focus.remember(firstWork, "episode-8002")
        focus.remember(secondWork, "episode-8902")
        assertEquals("continue-9002", focus.recall(home))
        assertEquals("work-741", focus.recall(search))
        assertEquals("episode-8002", focus.recall(firstWork))
        assertEquals("episode-8902", focus.recall(secondWork))
        focus.forget(search)
        assertNull(focus.recall(search))
        assertEquals("continue-9002", focus.recall(home))
        // Unit-level key isolation is not proof of Compose D-pad focus restoration on a device.
    }
}
