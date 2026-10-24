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

class TvWorkExtendedV1ContractTest {
    @Test fun readsActualEditionsCreditsAndPersonEndpointsWithoutFakingAssetIds() {
        val server = HttpServer.create(InetSocketAddress("127.0.0.1", 0), 0)
        val personWorksUri = AtomicReference<String>()
        server.createContext("/api/v1/works/741/editions") {
            it.respond("""[
                {"id":520,"name":"Extended cut","title":null,"languageCode":"ja",
                 "editionFormatId":null,"publisher":null,"releaseYear":2023,
                 "fileCount":1,"isAvailable":true},
                {"id":521,"name":null,"title":null,"languageCode":null,
                 "editionFormatId":null,"publisher":null,"releaseYear":null,
                 "fileCount":0,"isAvailable":false}
            ]""")
        }
        server.createContext("/api/v1/works/741/credits") {
            it.respond("""[{"personId":305,"name":"Composer","personRoleId":2,
                "credit":"Music","imageUrl":null}]""")
        }
        server.createContext("/api/v1/people/305/works") { exchange ->
            personWorksUri.set(exchange.requestURI.rawQuery)
            exchange.respond("""{"items":[
                {"id":741,"mediaTypeId":2,"mediaSubtypeId":1,"title":"Series",
                 "releaseYear":2023,"posterUrl":null,"rating":null,"mediaStateTypeId":3,
                 "isWatchlisted":false,"availabilityStateId":1,"availableUnits":12,
                 "monitoredUnits":12,"audioLanguages":["ja"],"subtitleLanguages":[],
                 "progressStateId":1,"progressPercent":0}
            ],"page":2,"pageSize":25}""")
        }
        server.createContext("/api/v1/people/305") {
            it.respond("""{"id":305,"name":"Composer","imageUrl":null}""")
        }
        server.start()
        try {
            val client = HttpTvCatalogV1Client(
                "http://127.0.0.1:${server.address.port}", DeviceTokenStore("paired")
            )
            runBlocking {
                val editions = client.editions(741)
                assertEquals(520L, editions[0].id)
                assertEquals(1L, editions[0].fileCount)
                assertTrue(editions[0].isAvailable)
                assertNull(editions[1].name)
                assertFalse(editions[1].isAvailable)
                val credit = client.credits(741).single()
                assertEquals(305L, credit.personId)
                assertEquals("Music", credit.credit)
                assertNull(credit.imageUrl)
                assertEquals(305L, client.person(credit.personId).id)
                val page = client.personWorks(credit.personId, page = 2)
                assertEquals(741L, page.items.single().id)
                assertEquals(2, page.page)
            }
            assertEquals("page=2&pageSize=25", personWorksUri.get())
        } finally {
            server.stop(0)
        }
    }

    @Test fun rejectsInvalidPersonAndWorkIdsWithoutGuessingThem() {
        assertEquals("/api/v1/works/741/editions", TvCatalogV1Routes.editions(741))
        assertEquals("/api/v1/works/741/credits", TvCatalogV1Routes.credits(741))
        assertEquals("/api/v1/people/305", TvCatalogV1Routes.person(305))
        assertTrue(runCatching { TvCatalogV1Routes.person(-5) }.isFailure)
        assertTrue(runCatching { TvCatalogV1Routes.personWorks(305, 0, 25) }.isFailure)
        assertTrue(runCatching { TvCatalogV1Routes.editions(0) }.isFailure)
        assertTrue(runCatching { JSONObject("{}").workEdition() }.isFailure)
    }

    private fun HttpExchange.respond(json: String) {
        val bytes = json.toByteArray(Charsets.UTF_8)
        responseHeaders.add("Content-Type", "application/json")
        sendResponseHeaders(200, bytes.size.toLong())
        responseBody.use { it.write(bytes) }
    }
}
