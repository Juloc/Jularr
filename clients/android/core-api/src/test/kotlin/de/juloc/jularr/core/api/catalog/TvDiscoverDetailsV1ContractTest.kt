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

/** Actual POST contracts of ReadDiscoverItemV1 and ReadDiscoverCatalogEntryV1. */
class TvDiscoverDetailsV1ContractTest {
    @Test fun catalogDetailsKeepExternalIdentitySeparateFromNullableWorkId() {
        val server = HttpServer.create(InetSocketAddress("127.0.0.1", 0), 0)
        val catalogBody = AtomicReference<String>()
        val catalogAuthorization = AtomicReference<String>()
        val tmdbMethod = AtomicReference<String>()
        server.createContext("/api/v1/discover/catalog/entry") { exchange ->
            catalogBody.set(exchange.requestBody.bufferedReader().use { it.readText() })
            catalogAuthorization.set(exchange.requestHeaders.getFirst("Authorization"))
            exchange.respond(
                """{"title":"Example Light Novel","releaseYear":2024,"overview":"Provider synopsis",
                   "posterUrl":null,"backdropUrl":null,"trailerKey":null,
                   "workId":null,"author":"An author"}"""
            )
        }
        server.createContext("/api/v1/discover/2/123") { exchange ->
            tmdbMethod.set(exchange.requestMethod)
            exchange.respond(
                """{"mediaTypeId":2,"tmdbId":123,"workId":null,"title":"Upcoming series",
                    "releaseYear":null,"overview":null,"posterUrl":null,"backdropUrl":null,
                    "mediaStateTypeId":1,"plexUrl":null,"rating":null,"trailerKey":null}"""
            )
        }
        server.start()
        try {
            val client = HttpTvCatalogV1Client("http://127.0.0.1:${server.address.port}", DeviceTokenStore("paired"))
            runBlocking {
                val detail = client.discoverCatalogEntry(3, "anilist", "12345", 3)
                assertEquals("Example Light Novel", detail.title)
                assertNull(detail.workId)
                assertEquals("An author", detail.author)
                assertEquals("Provider synopsis", detail.overview)

                val tmdb = client.discoverTmdbItem(2, 123)
                assertEquals(123, tmdb.tmdbId)
                assertNull(tmdb.workId)
            }
            val body = JSONObject(catalogBody.get())
            assertEquals(3, body.getInt("mediaTypeId"))
            assertEquals(3, body.getInt("mediaSubtypeId"))
            assertEquals("anilist", body.getString("namespace"))
            assertEquals("12345", body.getString("externalId"))
            assertEquals("Bearer paired", catalogAuthorization.get())
            assertEquals("POST", tmdbMethod.get())
        } finally {
            server.stop(0)
        }
    }

    @Test fun providerRoutesRejectInvalidAndFabricatedIds() {
        assertEquals("/api/v1/discover/catalog/entry", TvCatalogV1Routes.DiscoverCatalogEntry)
        assertEquals("/api/v1/discover/1/456", TvCatalogV1Routes.discoverTmdbItem(1, 456))
        assertTrue(runCatching { TvCatalogV1Routes.discoverTmdbItem(3, 456) }.isFailure)
        assertTrue(runCatching { TvCatalogV1Routes.discoverTmdbItem(2, 0) }.isFailure)
    }

    private fun HttpExchange.respond(json: String) {
        val bytes = json.toByteArray(Charsets.UTF_8)
        responseHeaders.add("Content-Type", "application/json")
        sendResponseHeaders(200, bytes.size.toLong())
        responseBody.use { it.write(bytes) }
    }
}
