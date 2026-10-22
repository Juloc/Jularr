package de.juloc.jularr.core.api.catalog

import com.sun.net.httpserver.HttpExchange
import com.sun.net.httpserver.HttpServer
import de.juloc.jularr.core.api.DeviceTokenStore
import kotlinx.coroutines.runBlocking
import org.junit.Assert.*
import org.junit.Test
import java.net.InetSocketAddress
import java.util.concurrent.atomic.AtomicReference

class TvCatalogV1FiltersContractTest {
    @Test fun serverFilteringUsesV1QueryPropertiesAndRealWatchlistCount() {
        val server = HttpServer.create(InetSocketAddress("127.0.0.1", 0), 0)
        val libraryUri = AtomicReference<String>()
        val watchlistUri = AtomicReference<String>()
        server.createContext("/api/v1/library") {
            libraryUri.set(it.requestURI.rawQuery)
            it.respond("""{"items":[],"page":3,"pageSize":24}""")
        }
        server.createContext("/api/v1/watchlist/count") {
            it.respond("""{"total":61}""")
        }
        server.createContext("/api/v1/watchlist") {
            watchlistUri.set(it.requestURI.rawQuery)
            it.respond("""{"items":[],"page":1,"pageSize":20}""")
        }
        server.start()
        try {
            val api = HttpTvCatalogV1Client("http://127.0.0.1:${server.address.port}", DeviceTokenStore("paired"))
            val filters = TvCatalogV1Filters(
                mediaTypeIds = listOf(1, 2), mediaSubtypeIds = listOf(1),
                releaseYearFrom = 2020, releaseYearTo = 2026, progressStateId = 2,
            )
            runBlocking {
                assertEquals(3, api.libraryFiltered(page = 3, pageSize = 24, filters = filters, query = "Anime").page)
                assertEquals(61, api.watchlistCount())
                assertTrue(api.watchlistFiltered(page = 1, pageSize = 20, filters = filters).items.isEmpty())
            }
            assertEquals(
                "page=3&pageSize=24&query=Anime&mediaTypeIds=1&mediaTypeIds=2&mediaSubtypeIds=1&releaseYearFrom=2020&releaseYearTo=2026",
                libraryUri.get(),
            )
            assertEquals(
                "page=1&pageSize=20&mediaTypeIds=1&mediaTypeIds=2&mediaSubtypeIds=1&releaseYearFrom=2020&releaseYearTo=2026&progressStateId=2",
                watchlistUri.get(),
            )
        } finally {
            server.stop(0)
        }
    }

    @Test fun invalidYearsAndProgressAreRejectedClientSide() {
        assertTrue(runCatching { TvCatalogV1Filters(releaseYearFrom = 2027, releaseYearTo = 2020) }.isFailure)
        assertTrue(runCatching { TvCatalogV1Filters(mediaTypeIds = listOf(-2)) }.isFailure)
        assertTrue(runCatching { TvCatalogV1Filters(progressStateId = 5) }.isFailure)
        assertEquals(
            "/api/v1/watchlist?page=1&pageSize=25&progressStateId=3",
            TvCatalogV1Routes.watchlistFiltered(1, 25, TvCatalogV1Filters(progressStateId = 3)),
        )
    }

    private fun HttpExchange.respond(json: String) {
        val bytes = json.toByteArray(Charsets.UTF_8)
        responseHeaders.add("Content-Type", "application/json")
        sendResponseHeaders(200, bytes.size.toLong())
        responseBody.use { it.write(bytes) }
    }
}
