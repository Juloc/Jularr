package de.juloc.jularr.tv

import de.juloc.jularr.core.api.AppApiException
import de.juloc.jularr.core.api.DeviceTokenStore
import de.juloc.jularr.core.api.catalog.HttpTvCatalogV1Client
import de.juloc.jularr.core.api.catalog.TvCatalogV1Client
import de.juloc.jularr.core.api.catalog.TvDiscoverItemV1
import de.juloc.jularr.core.api.catalog.TvDiscoverScopeV1
import de.juloc.jularr.core.api.catalog.TvDiscoverShelfV1
import kotlinx.coroutines.delay
import kotlinx.coroutines.runBlocking
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertThrows
import org.junit.Assert.assertTrue
import org.junit.Test
import java.util.concurrent.atomic.AtomicInteger

class TvV1HomeDiscoverFlowTest {
    private val fallback = HttpTvCatalogV1Client(
        "http://127.0.0.1:1", DeviceTokenStore("test-token"),
    )
    private val scopes = listOf(
        TvDiscoverScopeV1("movies", 1, null, false, listOf(1, 2, 3, 4)),
        TvDiscoverScopeV1("anime", 2, 1, false, listOf(1, 2)),
    )
    private fun item(id: Int) = TvDiscoverItemV1(
        mediaTypeId = 1, tmdbId = id, workId = null, title = "Movie $id",
        releaseYear = 2026, overview = null, posterUrl = null, backdropUrl = null,
        mediaStateTypeId = 1, plexUrl = null, rating = null, trailerKey = null,
    )

    @Test fun cachedShelvesAppearBeforeParallelProviderFallbackAndKeepScopedIdentity() = runBlocking {
        val active = AtomicInteger()
        val maximum = AtomicInteger()
        val called = mutableListOf<Int>()
        val cached = listOf(TvDiscoverShelfV1(
            "movies", 1, listOf(item(1001)), emptyList(),
        ))
        val client = object : TvCatalogV1Client by fallback {
            override suspend fun discoverScopes() = scopes
            override suspend fun discoverShelves(scopeSlug: String?) = cached
            override suspend fun discoverFeed(mediaTypeId: Int, feedTypeId: Int, page: Int):
                List<TvDiscoverItemV1> {
                synchronized(called) { called.add(feedTypeId) }
                val count = active.incrementAndGet()
                maximum.updateAndGet { maxOf(it, count) }
                try {
                    delay(30)
                    return listOf(item(2000 + feedTypeId))
                } finally {
                    active.decrementAndGet()
                }
            }
        }
        val updates = mutableListOf<List<TvV1HomeVideoFeed>>()
        val result = TvV1HomeDiscoverFlow(
            client, providerTimeoutMs = 2_000L, providerConcurrency = 3,
        ).load { updates.add(it) }
        assertEquals(listOf(1), updates.first().map { it.feedTypeId })
        assertEquals(listOf(1, 2, 3, 4), result.map { it.feedTypeId })
        assertEquals(listOf(2, 3, 4), called.sorted())
        assertTrue("Missing provider feeds must be loaded in parallel", maximum.get() > 1)
        assertEquals(1001, result.first().items.single().tmdbId)
        assertEquals(null, result.first().items.single().workId)
        assertEquals(1, result.first().mediaTypeId)
        // No generic movie result can claim to be part of the Anime scope.
        assertFalse(result.any { it.scopeSlug == "anime" })
    }

    @Test fun oneUnavailableProviderDoesNotHideOtherCachedOrRankedShelves() = runBlocking {
        val client = object : TvCatalogV1Client by fallback {
            override suspend fun discoverScopes() = scopes.take(1)
            override suspend fun discoverShelves(scopeSlug: String?) = listOf(
                TvDiscoverShelfV1("movies", 1, listOf(item(47)), emptyList()),
            )
            override suspend fun discoverFeed(mediaTypeId: Int, feedTypeId: Int, page: Int):
                List<TvDiscoverItemV1> {
                if (feedTypeId == 2) throw IllegalStateException("offline provider")
                return listOf(item(feedTypeId))
            }
        }
        val result = TvV1HomeDiscoverFlow(client).load {}
        assertEquals(listOf(1, 3, 4), result.map { it.feedTypeId })
        assertEquals(47, result.first().items.single().tmdbId)
    }

    @Test fun unauthorizedProviderResponseMustPropagateInsteadOfHidingProfileExpiry() {
        val client = object : TvCatalogV1Client by fallback {
            override suspend fun discoverScopes() = scopes.take(1)
            override suspend fun discoverShelves(scopeSlug: String?) = emptyList<TvDiscoverShelfV1>()
            override suspend fun discoverFeed(mediaTypeId: Int, feedTypeId: Int, page: Int):
                List<TvDiscoverItemV1> =
                throw AppApiException(401, "unauthorized", "error.unauthorized", null, "expired")
        }
        assertThrows(AppApiException::class.java) {
            runBlocking { TvV1HomeDiscoverFlow(client).load {} }
        }
    }
}
