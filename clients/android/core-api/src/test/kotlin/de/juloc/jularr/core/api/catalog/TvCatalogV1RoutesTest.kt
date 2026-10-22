package de.juloc.jularr.core.api.catalog

import org.junit.Assert.*
import org.junit.Test

class TvCatalogV1RoutesTest {
    @Test fun knownCleanStartEndpointsRemainVersioned() {
        assertEquals("/api/v1/library?page=1&pageSize=48", TvCatalogV1Routes.library(1, 48))
        assertEquals("/api/v1/continue", TvCatalogV1Routes.Continue)
        assertEquals("/api/v1/watchlist?page=1&pageSize=25", TvCatalogV1Routes.watchlist(1, 25))
        assertEquals("/api/v1/watchlist/741", TvCatalogV1Routes.watchlistItem(741))
        assertEquals("/api/v1/home/layout", TvCatalogV1Routes.HomeLayout)
        assertEquals("/api/v1/discover/search", TvCatalogV1Routes.DiscoverSearch)
        assertEquals("/api/v1/discover/feed", TvCatalogV1Routes.DiscoverFeed)
        assertEquals("/api/v1/discover/shelves", TvCatalogV1Routes.DiscoverShelves)
        assertEquals("/api/v1/discover/scopes", TvCatalogV1Routes.DiscoverScopes)
        assertEquals("/api/v1/discover/catalog/search", TvCatalogV1Routes.DiscoverCatalogSearch)
        assertEquals("/api/v1/discover/catalog/feed", TvCatalogV1Routes.DiscoverCatalogFeed)
        assertEquals("/api/v1/discover/suggestions", TvCatalogV1Routes.DiscoverSuggestions)
        assertEquals("/api/v1/works/741", TvCatalogV1Routes.work(741))
        assertEquals("/api/v1/works/741/seasons", TvCatalogV1Routes.seasons(741))
        assertEquals("/api/v1/works/741/seasons/0/episodes", TvCatalogV1Routes.episodes(741, 0))
        assertEquals("/api/v1/works/741/availability?page=2&pageSize=25", TvCatalogV1Routes.availability(741, 2, 25))
    }

    @Test fun urlEncodesLibrarySearchButNeverAcceptsExternalPaths() {
        assertEquals(
            "/api/v1/library?page=3&pageSize=20&query=A%2BB+%26+Anime&librarySortTypeId=2",
            TvCatalogV1Routes.library(3, 20, "A+B & Anime", 2),
        )
        assertTrue(runCatching { TvCatalogV1Routes.work(0) }.isFailure)
        assertTrue(runCatching { TvCatalogV1Routes.episodes(9, -1) }.isFailure)
        assertTrue(runCatching { TvCatalogV1Routes.library(1, 101) }.isFailure)
        assertTrue(runCatching { TvCatalogV1Routes.library(5000, 25) }.isFailure)
    }
}
