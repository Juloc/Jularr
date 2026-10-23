package de.juloc.jularr.tv

import de.juloc.jularr.core.api.DeviceTokenStore
import de.juloc.jularr.core.api.catalog.HttpTvCatalogV1Client
import de.juloc.jularr.core.api.catalog.TvCatalogV1Client
import de.juloc.jularr.core.api.catalog.TvCatalogV1Filters
import de.juloc.jularr.core.api.catalog.TvContinueItemV1
import de.juloc.jularr.core.api.catalog.TvHomeShelfV1
import de.juloc.jularr.core.api.catalog.TvPageV1
import de.juloc.jularr.core.api.catalog.TvWorkCardV1
import de.juloc.jularr.core.api.catalog.TvWorkDetailsV1
import de.juloc.jularr.core.api.catalog.TvWorkEpisodeV1
import de.juloc.jularr.core.api.catalog.TvWorkSeasonV1
import de.juloc.jularr.core.model.InstanceCapabilities
import kotlinx.coroutines.runBlocking
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertThrows
import org.junit.Assert.assertTrue
import org.junit.Test
import java.math.BigDecimal

class TvV1ClientFlowMigrationTest {
    private val permissions = InstanceCapabilities(
        permissions = listOf("profile.media.read", "profile.playback"),
        modules = listOf("Library", "Playback", "HomeLayout"),
    )
    private val fallback = HttpTvCatalogV1Client(
        "http://127.0.0.1:1", DeviceTokenStore("test-token"),
    )

    private fun card(id: Long, mediaType: Int = 2) = TvWorkCardV1(
        id = id, mediaTypeId = mediaType, mediaSubtypeId = if (mediaType == 2) 1 else null,
        title = "Test $id", releaseYear = 2026, posterUrl = null,
        rating = null, mediaStateTypeId = 1, isWatchlisted = true,
        availabilityStateId = 2, availableUnits = 0, monitoredUnits = 2,
        audioLanguages = emptyList(), subtitleLanguages = emptyList(),
        progressStateId = 1, progressPercent = 0,
    )

    private fun work(id: Long) = TvWorkDetailsV1(
        id = id, mediaTypeId = 2, mediaSubtypeId = 1,
        canonicalTitle = "Test series", isWatchlisted = true,
        releaseYear = 2026, overview = null, posterUrl = null,
        backdropUrl = null, plexUrl = null, platforms = emptyList(),
        franchiseId = null, franchiseName = null, isRequestable = false,
        requestStatusTypeId = null, runtimeMinutes = null, genres = null,
        trailerKey = null, studio = null, availabilityStateId = 2,
        availableUnits = 1, monitoredUnits = 2, progressStateId = 1,
        progressPercent = 0, rating = null, ratingSource = null,
        originalTitle = null, releasedOn = null, originalLanguage = null,
        franchisePosition = null,
    )

    @Test fun homeAndSearchUseActualServerSideVideoFiltersAndOnlyVideoContinue() = runBlocking {
        val seenQueries = mutableListOf<String?>()
        val seenTypes = mutableListOf<List<Int>>()
        val seenSubtypes = mutableListOf<List<Int>>()
        val backend = object : TvCatalogV1Client by fallback {
            override suspend fun libraryFiltered(
                page: Int, pageSize: Int, filters: TvCatalogV1Filters,
                query: String?, sortTypeId: Int?,
            ): TvPageV1<TvWorkCardV1> {
                seenQueries.add(query)
                seenTypes.add(filters.mediaTypeIds.orEmpty())
                seenSubtypes.add(filters.mediaSubtypeIds)
                return TvPageV1(listOf(card(701)), page, pageSize)
            }
            override suspend fun continueWatching() = listOf(
                TvContinueItemV1(
                    workId = 701, mediaAssetId = 9002, mediaAssetTypeId = 1,
                    mediaTypeId = 2, mediaSubtypeId = 1, title = "Episode",
                    releaseYear = 2026, posterUrl = null, workUnitTypeId = 2,
                    unitNumber = BigDecimal.ONE, parentUnitNumber = BigDecimal.ONE,
                    positionMs = 20000, durationMs = 100000, progression = null,
                ),
                TvContinueItemV1(
                    workId = 733, mediaAssetId = 9901, mediaAssetTypeId = 2,
                    mediaTypeId = 3, mediaSubtypeId = null, title = "Book",
                    releaseYear = null, posterUrl = null, workUnitTypeId = null,
                    unitNumber = null, parentUnitNumber = null,
                    positionMs = 0, durationMs = null, progression = null,
                ),
            )
            override suspend fun homeLayout() = listOf(TvHomeShelfV1(1, true))
        }
        val flow = TvV1ClientFlow(backend, permissions)
        val home = flow.home()
        assertEquals(listOf("701"), home.library.anime.map { it.id })
        assertEquals(9002L, home.continueWatching.single().mediaAssetId)
        assertFalse(home.showContinueWatching)
        assertEquals(listOf("701"), flow.search("Anime").anime.map { it.id })
        assertEquals(listOf(null, "Anime"), seenQueries)
        assertEquals(listOf(listOf(1, 2), listOf(1, 2)), seenTypes)
        assertEquals(listOf(listOf(1), listOf(1)), seenSubtypes)
    }

    @Test fun favoritesUseActualVideoAndAnimeFiltersAndServerSort() = runBlocking {
        val requests = mutableListOf<Triple<Int, TvCatalogV1Filters, Int?>>()
        val backend = object : TvCatalogV1Client by fallback {
            override suspend fun watchlistFiltered(
                page: Int, pageSize: Int, filters: TvCatalogV1Filters, sortTypeId: Int?,
            ): TvPageV1<TvWorkCardV1> {
                requests.add(Triple(page, filters, sortTypeId))
                return TvPageV1(listOf(card(701)), page, pageSize)
            }
        }
        val flow = TvV1ClientFlow(backend, permissions)
        assertEquals(701L, flow.watchlistPage(1, 1).items.single().id)
        assertEquals(701L, flow.watchlistPage(2, 2).items.single().id)
        assertEquals(listOf(1, 2), requests.map { it.first })
        assertEquals(listOf(1, 2), requests.map { it.third })
        assertTrue(requests.all { it.second.mediaTypeIds == listOf(1, 2) })
        assertTrue(requests.all { it.second.mediaSubtypeIds == listOf(1) })
    }

    @Test fun workDetailsPreserveFractionalUnitAndRealPlaybackAssetOnly() = runBlocking {
        val backend = object : TvCatalogV1Client by fallback {
            override suspend fun work(workId: Long) =
                this@TvV1ClientFlowMigrationTest.work(workId)
            override suspend fun seasons(workId: Long) = listOf(
                TvWorkSeasonV1(11, 1, null, null, 2026, 2, 1, 0),
            )
            override suspend fun episodes(workId: Long, seasonNumber: Int) = listOf(
                TvWorkEpisodeV1(
                    id = 401, seasonNumber = 1, number = BigDecimal.ONE,
                    title = "Missing", overview = null, imageUrl = null,
                    runtimeMinutes = 24, releasedOn = null, mediaAssetId = null,
                    resumePositionMs = null, durationMs = null,
                    isCompleted = false, mediaStateTypeId = 2,
                    isUpcoming = false, groupName = null,
                ),
                TvWorkEpisodeV1(
                    id = 402, seasonNumber = 1, number = BigDecimal("1.5"),
                    title = "Special", overview = null, imageUrl = null,
                    runtimeMinutes = 24, releasedOn = null, mediaAssetId = 9002,
                    resumePositionMs = 12000, durationMs = 90000,
                    isCompleted = false, mediaStateTypeId = 3,
                    isUpcoming = false, groupName = null,
                ),
            )
        }
        val result = TvV1ClientFlow(backend, permissions).work(701)
        assertEquals("Test series", result.title)
        assertTrue(result.isWatchlisted)
        assertEquals(listOf(402L), result.fractionalEpisodes.map { it.id })
        assertEquals(null, result.assetByUnit["401"])
        assertEquals(9002L, result.assetByUnit["402"])
        assertEquals(listOf("401"), result.detail!!.seasons.single().episodes.map { it.id })
    }

    @Test fun libraryAfterTwoThousandWorksKeepsRealServerPages() = runBlocking {
        val requestedPages = mutableListOf<Int>()
        val backend = object : TvCatalogV1Client by fallback {
            override suspend fun libraryFiltered(
                page: Int, pageSize: Int, filters: TvCatalogV1Filters,
                query: String?, sortTypeId: Int?,
            ): TvPageV1<TvWorkCardV1> {
                requestedPages.add(page)
                val items = if (page == 22) listOf(card(2_101), card(2_102))
                    else (1..100).map { card((page - 1) * 100L + it) }
                return TvPageV1(items, page, pageSize)
            }
        }
        val flow = TvV1ClientFlow(backend, permissions)
        val first = flow.videoPage(1)
        val beyond = flow.videoPage(21)
        val last = flow.videoPage(22)
        assertTrue(first.hasMore)
        assertTrue(beyond.hasMore)
        assertFalse(last.hasMore)
        assertEquals(listOf(1, 21, 22), requestedPages)
        assertEquals(2_102L, last.items.last().id)
    }

    @Test fun homeRespectsRealConfiguredVideoShelfOrderAndHiddenStatus() = runBlocking {
        val backend = object : TvCatalogV1Client by fallback {
            override suspend fun libraryFiltered(
                page: Int, pageSize: Int, filters: TvCatalogV1Filters,
                query: String?, sortTypeId: Int?,
            ) = TvPageV1(listOf(card(701)), page, pageSize)
            override suspend fun continueWatching() = emptyList<TvContinueItemV1>()
            override suspend fun homeLayout() = listOf(
                TvHomeShelfV1(5, false), // Recommendations require their own source.
                TvHomeShelfV1(4, false), // Actual recently added.
                TvHomeShelfV1(1, true),  // The user explicitly hid Continue Watching.
            )
        }
        val home = TvV1ClientFlow(backend, permissions).home()
        assertEquals(listOf(5, 4), home.shelfOrder)
        assertFalse(home.showContinueWatching)
    }

    @Test fun incompatibleOrUnauthorizedMediaMustNotBeShownAsLoaded() {
        val noPermission = InstanceCapabilities(
            permissions = emptyList(), modules = listOf("Library"),
        )
        val flow = TvV1ClientFlow(fallback, noPermission)
        assertThrows(IllegalStateException::class.java) {
            runBlocking { flow.home() }
        }
    }
}
