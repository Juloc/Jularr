package de.juloc.jularr.core.api.catalog

import com.sun.net.httpserver.HttpExchange
import com.sun.net.httpserver.HttpServer
import de.juloc.jularr.core.api.DeviceTokenStore
import kotlinx.coroutines.runBlocking
import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Test
import java.net.InetSocketAddress
import java.util.concurrent.atomic.AtomicReference

class TvDiscoverCatalogV1ContractTest {
    @Test fun subtypeAwareCatalogSearchAndFeedUseRealExternalIdentities() {
        val server = HttpServer.create(InetSocketAddress("127.0.0.1", 0), 0)
        val searchPayload = AtomicReference<String>()
        val feedPayload = AtomicReference<String>()
        val suggestionsPayload = AtomicReference<String>()
        val bearer = AtomicReference<String>()
        server.createContext("/api/v1/discover/catalog/search") { exchange ->
            searchPayload.set(exchange.requestBody.bufferedReader().use { it.readText() })
            bearer.set(exchange.requestHeaders.getFirst("Authorization"))
            exchange.respond(
                """[{"namespace":"openlibrary","externalId":"OL12345W","title":"Example Light Novel",
                "releaseYear":2024,"author":"Example Author","workId":null,"posterUrl":null,
                "rating":null,"mediaStateTypeId":null}]""",
            )
        }
        server.createContext("/api/v1/discover/catalog/feed") { exchange ->
            feedPayload.set(exchange.requestBody.bufferedReader().use { it.readText() })
            exchange.respond("""[]""")
        }
        server.createContext("/api/v1/discover/suggestions") { exchange ->
            suggestionsPayload.set(exchange.requestBody.bufferedReader().use { it.readText() })
            exchange.respond(
                """[{"scopeSlug":"light-novels","namespace":"openlibrary","externalId":"OL12345W",
                "title":"Example Light Novel","releaseYear":2024,"posterUrl":null,"workId":null}]""",
            )
        }
        server.start()
        try {
            val api = HttpTvCatalogV1Client("http://127.0.0.1:${server.address.port}", DeviceTokenStore("paired"))
            runBlocking {
                val result = api.discoverCatalogSearch(mediaTypeId = 3, query = "Novel", mediaSubtypeId = 3)
                assertNull(result.single().workId)
                assertEquals("OL12345W", result.single().externalId)
                assertTrue(api.discoverCatalogFeed(mediaTypeId = 3, feedTypeId = 1, mediaSubtypeId = 3).isEmpty())
                assertEquals("light-novels", api.discoverSuggestions("Example", "light-novels").single().scopeSlug)
            }
            assertEquals("Bearer paired", bearer.get())
            assertEquals(3, JSONObject(searchPayload.get()).getInt("mediaTypeId"))
            assertEquals(3, JSONObject(searchPayload.get()).getInt("mediaSubtypeId"))
            assertEquals("Novel", JSONObject(searchPayload.get()).getString("query"))
            assertEquals(1, JSONObject(feedPayload.get()).getInt("discoverFeedTypeId"))
            assertEquals("light-novels", JSONObject(suggestionsPayload.get()).getString("scopeSlug"))
        } finally {
            server.stop(0)
        }
    }

    private fun HttpExchange.respond(json: String) {
        val bytes = json.toByteArray(Charsets.UTF_8)
        responseHeaders.add("Content-Type", "application/json")
        sendResponseHeaders(200, bytes.size.toLong())
        responseBody.use { it.write(bytes) }
    }
}
