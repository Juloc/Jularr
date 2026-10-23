package de.juloc.jularr.tv

import org.junit.Assert.*
import org.junit.Test

class TvV1NavigationTest {
    @Test fun deepNavigationRestoresRealSourceScreen() {
        var state = TvV1NavigationState()
        state = TvV1Navigation.open(state, TvV1CatalogPage.Search)
        state = TvV1Navigation.open(state, TvV1CatalogPage.Discover)
        state = TvV1Navigation.open(state, TvV1CatalogPage.Details)
        state = TvV1Navigation.open(state, TvV1CatalogPage.MovieEditions)
        state = TvV1Navigation.open(state, TvV1CatalogPage.Player)
        assertEquals(TvV1CatalogPage.MovieEditions, TvV1Navigation.back(state).page)
        state = TvV1Navigation.back(state)
        state = TvV1Navigation.back(state)
        assertEquals(TvV1CatalogPage.Details, state.page)
        state = TvV1Navigation.back(state)
        assertEquals(TvV1CatalogPage.Discover, state.page)
        state = TvV1Navigation.back(state)
        assertEquals(TvV1CatalogPage.Search, state.page)
        assertEquals(TvV1CatalogPage.Home, TvV1Navigation.back(state).page)
    }

    @Test fun sidebarDoesNotRetainPriorProfileOrWorkHistory() {
        var state = TvV1Navigation.open(TvV1NavigationState(), TvV1CatalogPage.Details)
        state = TvV1Navigation.open(state, TvV1CatalogPage.Settings)
        assertTrue(state.previous.isEmpty())
        state = TvV1Navigation.open(state, TvV1CatalogPage.ProfileSelect)
        assertEquals(TvV1CatalogPage.Settings, TvV1Navigation.back(state).page)
        state = TvV1Navigation.open(state, TvV1CatalogPage.Watchlist)
        assertTrue(state.previous.isEmpty())
        assertEquals(TvV1CatalogPage.Home, TvV1Navigation.back(state).page)
    }

    @Test fun playbackOpenedDirectlyFromContinueReturnsHome() {
        val state = TvV1Navigation.open(TvV1NavigationState(), TvV1CatalogPage.Player)
        assertEquals(TvV1CatalogPage.Home, TvV1Navigation.back(state).page)
    }

    @Test fun backRestoresOriginalWorkAndPersonIdentity() {
        var state = TvV1Navigation.open(
            TvV1NavigationState(), TvV1CatalogPage.Details, workId = 701,
        )
        state = TvV1Navigation.open(state, TvV1CatalogPage.Credits, workId = 701)
        state = TvV1Navigation.open(state, TvV1CatalogPage.Person, workId = 701, personId = 19)
        state = TvV1Navigation.open(state, TvV1CatalogPage.Details, workId = 930)
        assertEquals(930L, state.current.workId)
        state = TvV1Navigation.back(state)
        assertEquals(TvV1CatalogPage.Person, state.page)
        assertEquals(19L, state.current.personId)
        assertEquals(701L, state.current.workId)
        state = TvV1Navigation.back(state)
        assertEquals(TvV1CatalogPage.Credits, state.page)
        assertEquals(701L, state.current.workId)
        state = TvV1Navigation.back(state)
        assertEquals(TvV1CatalogPage.Details, state.page)
        assertEquals(701L, state.current.workId)
    }

    @Test fun repeatNavigationToSamePageDoesNotPushDuplicates() {
        val once = TvV1Navigation.open(TvV1NavigationState(), TvV1CatalogPage.Search)
        assertEquals(once, TvV1Navigation.open(once, TvV1CatalogPage.Search))
    }
}
