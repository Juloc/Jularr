package de.juloc.jularr.core.api.catalog

import com.sun.net.httpserver.HttpExchange
import com.sun.net.httpserver.HttpServer
import de.juloc.jularr.core.api.DeviceTokenStore
import kotlinx.coroutines.runBlocking
import org.junit.Assert.*
import org.junit.Test
import java.net.InetSocketAddress

class TvCollectionsV1ContractTest {
    @Test fun collectionsAndVideoPageRespectRealServerShapesAndPermissionsRoute() {
        val server = HttpServer.create(InetSocketAddress("127.0.0.1", 0), 0)
        val observed = mutableListOf<String>()
        server.createContext("/api/v1/collections") { exchange ->
            synchronized(observed) { observed.add("${exchange.requestMethod} ${exchange.requestURI} ${exchange.requestHeaders.getFirst("Authorization")}") }
            if (exchange.requestURI.path == "/api/v1/collections") {
                exchange.respond("""[{"id":72,"name":"Horror","isSmart":false,"workCount":201,
                    "posterUrls":["https://example.test/p.jpg"],"version":4}]""")
            } else {
                exchange.respond("""{"items":[{"id":92001,"mediaTypeId":2,"mediaSubtypeId":1,
                    "title":"Anime","releaseYear":2026,"posterUrl":null,"rating":null,
                    "mediaStateTypeId":1,"isWatchlisted":false,"availabilityStateId":1,
                    "availableUnits":0,"monitoredUnits":0,"audioLanguages":[],"subtitleLanguages":[],
                    "progressStateId":1,"progressPercent":0}],"page":2,"pageSize":100}""")
            }
        }
        server.start()
        try {
            val api = HttpTvCatalogV1Client("http://127.0.0.1:${server.address.port}", DeviceTokenStore("paired"))
            runBlocking {
                val collections = api.collections()
                assertEquals(72L, collections.single().id)
                assertEquals(201, collections.single().workCount)
                assertEquals(4L, collections.single().version)
                val page = api.collectionWorks(72L, 2, 100)
                assertEquals(2, page.page)
                assertEquals(92001L, page.items.single().id)
                assertEquals(1, page.items.single().mediaSubtypeId)
            }
            assertEquals(2, observed.size)
            assertTrue(observed[0].startsWith("GET /api/v1/collections Bearer paired"))
            assertTrue(observed[1].contains("/api/v1/collections/72/works?page=2&pageSize=100"))
            assertTrue(observed[1].contains("mediaTypeIds=1&mediaTypeIds=2&mediaSubtypeIds=1"))
            assertTrue(observed[1].endsWith("Bearer paired"))
        } finally {
            server.stop(0)
        }
    }

    private fun HttpExchange.respond(body: String) {
        val bytes = body.toByteArray()
        responseHeaders.add("Content-Type", "application/json")
        sendResponseHeaders(200, bytes.size.toLong())
        responseBody.use { it.write(bytes) }
    }
}
