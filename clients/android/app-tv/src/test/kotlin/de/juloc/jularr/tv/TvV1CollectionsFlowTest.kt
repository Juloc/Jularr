package de.juloc.jularr.tv

import de.juloc.jularr.core.api.DeviceTokenStore
import de.juloc.jularr.core.api.catalog.HttpTvCatalogV1Client
import de.juloc.jularr.core.api.catalog.TvCatalogV1Client
import de.juloc.jularr.core.api.catalog.TvCollectionV1
import de.juloc.jularr.core.api.catalog.TvPageV1
import de.juloc.jularr.core.api.catalog.TvWorkCardV1
import de.juloc.jularr.core.model.InstanceCapabilities
import kotlinx.coroutines.runBlocking
import org.junit.Assert.*
import org.junit.Test

class TvV1CollectionsFlowTest {
    private val fallback = HttpTvCatalogV1Client(
        "http://127.0.0.1:1", DeviceTokenStore("paired"),
    )
    private fun card(id: Long, type: Int = 2) = TvWorkCardV1(
        id = id, mediaTypeId = type, mediaSubtypeId = if (type == 2) 1 else null,
        title = "Title $id", releaseYear = null, posterUrl = null, rating = null,
        mediaStateTypeId = 1, isWatchlisted = true, availabilityStateId = 1,
        availableUnits = 0, monitoredUnits = 0, audioLanguages = emptyList(),
        subtitleLanguages = emptyList(), progressStateId = 1, progressPercent = 0,
    )
    private val granted = InstanceCapabilities(
        permissions = listOf("profile.media.read", "profile.collections"),
        modules = listOf("Library", "Collections"),
    )

    @Test fun readsRealCollectionIdsAndContinuesAfterPageOne() = runBlocking {
        val requested = mutableListOf<Pair<Long, Int>>()
        val api = object : TvCatalogV1Client by fallback {
            override suspend fun collections() =
                listOf(TvCollectionV1(72L, "Horror", false, 260, listOf("cover"), 4L))
            override suspend fun collectionWorks(collectionId: Long, page: Int, pageSize: Int): TvPageV1<TvWorkCardV1> {
                requested.add(collectionId to page)
                return TvPageV1(
                    if (page == 1) (1L..100L).map { card(it) }
                    else listOf(card(101L), card(102L, 3)),
                    page, pageSize,
                )
            }
        }
        val flow = TvV1ClientFlow(api, granted)
        assertEquals(72L, flow.collections().single().id)
        assertTrue(flow.collectionPage(72L, 1).hasMore)
        val next = flow.collectionPage(72L, 2)
        assertFalse(next.hasMore)
        assertEquals(listOf(101L), next.items.map { it.id })
        assertEquals(listOf(72L to 1, 72L to 2), requested)
    }

    @Test fun aProfileWithoutCollectionsPermissionCannotReadThem() {
        val denied = TvV1ClientFlow(fallback, InstanceCapabilities(
            permissions = listOf("profile.media.read"), modules = listOf("Library", "Collections"),
        ))
        assertFalse(denied.canReadCollections())
        assertThrows(IllegalStateException::class.java) {
            runBlocking { denied.collections() }
        }
        assertThrows(IllegalStateException::class.java) {
            runBlocking { denied.collectionPage(1, 1) }
        }
    }
}
