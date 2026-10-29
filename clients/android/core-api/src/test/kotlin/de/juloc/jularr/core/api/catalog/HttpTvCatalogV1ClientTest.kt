package de.juloc.jularr.core.api.catalog

import com.sun.net.httpserver.HttpExchange
import com.sun.net.httpserver.HttpServer
import de.juloc.jularr.core.api.AppApiException
import de.juloc.jularr.core.api.AppApiRoutes
import de.juloc.jularr.core.api.DeviceTokenStore
import kotlinx.coroutines.runBlocking
import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Test
import java.net.InetSocketAddress
import java.util.concurrent.atomic.AtomicReference

class HttpTvCatalogV1ClientTest {
    @Test fun catalogUsesActualRoutesQueryPaginationAndBearerRotation() {
        val server = server()
        val libraryAuthorization = AtomicReference<String>()
        val libraryUri = AtomicReference<String>()
        val watchlistBody = AtomicReference<String>()
        val sawCookie = AtomicReference<String?>()
        server.createContext("/api/v1/library") { exchange ->
            libraryAuthorization.set(exchange.requestHeaders.getFirst("Authorization"))
            libraryUri.set(exchange.requestURI.toASCIIString())
            sawCookie.set(exchange.requestHeaders.getFirst("Cookie"))
            exchange.reply(200, """{"items":[${card()}],"page":2,"pageSize":25}""", "rotated")
        }
        server.createContext("/api/v1/continue") { exchange ->
            exchange.reply(200, """[{"workId":741,"mediaAssetId":9002,"mediaAssetTypeId":1,"mediaTypeId":2,
                "mediaSubtypeId":1,"title":"Frieren","releaseYear":2023,"posterUrl":null,
                "workUnitTypeId":2,"unitNumber":2,"parentUnitNumber":1,"positionMs":40000,
                "durationMs":1400000,"progression":null}]""")
        }
        server.createContext("/api/v1/watchlist") { exchange ->
            if (exchange.requestMethod == "PUT") {
                watchlistBody.set(exchange.requestBody.bufferedReader().use { it.readText() })
                exchange.reply(200, """{"success":true}""")
            } else {
                exchange.reply(200, """{"items":[${card()}],"page":1,"pageSize":25}""")
            }
        }
        server.start()
        try {
            val tokens = DeviceTokenStore(initialToken = "paired-token")
            val client = HttpTvCatalogV1Client("http://127.0.0.1:${server.address.port}", tokens)
            runBlocking {
                val page = client.library(page = 2, pageSize = 25, query = "Frieren & Reise")
                assertEquals(741L, page.items.single().id)
                assertEquals(2, page.page)
                assertEquals(9002L, client.continueWatching().single().mediaAssetId)
                assertEquals(741L, client.watchlist().items.single().id)
                assertTrue(client.setWatchlisted(741L, false))
            }
            assertEquals("Bearer paired-token", libraryAuthorization.get())
            assertEquals("rotated", tokens.current)
            assertEquals("/api/v1/library?page=2&pageSize=25&query=Frieren+%26+Reise", libraryUri.get())
            assertNull(sawCookie.get())
            assertFalse(JSONObject(watchlistBody.get()).getBoolean("isWatchlisted"))
        } finally {
            server.stop(0)
        }
    }

    @Test fun discoverAndWorkEndpointsRetainWorkUnitAndMediaAssetIdentities() {
        val server = server()
        val searchPayload = AtomicReference<String>()
        val discoveredRoute = AtomicReference<String>()
        server.createContext("/api/v1/discover") { exchange ->
            discoveredRoute.set(exchange.requestURI.path)
            when (exchange.requestURI.path) {
                "/api/v1/discover/scopes" ->
                    exchange.reply(200, """[{"slug":"tv","mediaTypeId":2,"mediaSubtypeId":null,"isCatalog":false,"feeds":[1,2]}]""")
                "/api/v1/discover/shelves" -> exchange.reply(200, """[]""")
                "/api/v1/discover/feed" -> exchange.reply(200, """[]""")
                "/api/v1/discover/search" -> {
                    searchPayload.set(exchange.requestBody.bufferedReader().use { it.readText() })
                    exchange.reply(200, """[{"mediaTypeId":2,"tmdbId":321,"workId":741,"title":"Frieren",
                        "releaseYear":2023,"overview":null,"posterUrl":null,"backdropUrl":null,
                        "mediaStateTypeId":3,"plexUrl":null,"rating":8.8,"trailerKey":null}]""")
                }
                else -> exchange.reply(404, """{"code":"not_found"}""")
            }
        }
        server.createContext("/api/v1/home/layout") {
            it.reply(200, """[{"homeShelfTypeId":1,"isHidden":false}]""")
        }
        server.createContext("/api/v1/works/741") { exchange ->
            when (exchange.requestURI.path) {
                "/api/v1/works/741" -> exchange.reply(200, workDetails())
                "/api/v1/works/741/seasons" -> exchange.reply(200, """[{"id":500,"seasonNumber":1,
                    "title":"Season 1","imageUrl":null,"releaseYear":2023,
                    "episodeCount":28,"availableCount":4,"watchedCount":1}]""")
                "/api/v1/works/741/seasons/1/episodes" -> exchange.reply(200, """[{
                    "id":8002,"seasonNumber":1,"number":2,"title":"Episode 2","overview":null,
                    "imageUrl":null,"runtimeMinutes":24,"releasedOn":null,"mediaAssetId":9002,
                    "resumePositionMs":40000,"durationMs":1400000,"isCompleted":false,
                    "mediaStateTypeId":3,"isUpcoming":false,"groupName":null}]""")
                "/api/v1/works/741/availability" -> exchange.reply(200, """{"items":[${availability()}],
                    "page":1,"pageSize":25}""")
                else -> exchange.reply(404, """{"code":"not_found"}""")
            }
        }
        server.start()
        try {
            val client = HttpTvCatalogV1Client("http://127.0.0.1:${server.address.port}", DeviceTokenStore("token"))
            runBlocking {
                assertEquals(1, client.homeLayout().single().homeShelfTypeId)
                assertEquals("tv", client.discoverScopes().single().slug)
                assertTrue(client.discoverShelves().isEmpty())
                assertTrue(client.discoverFeed(mediaTypeId = 2, feedTypeId = 1).isEmpty())
                assertEquals(741L, client.discoverSearch("Frieren", mediaTypeId = 2).single().workId)
                assertEquals(741L, client.work(741L).id)
                assertEquals(500L, client.seasons(741L).single().id)
                assertEquals(8002L, client.episodes(741L, 1).single().id)
                assertEquals(9002L, client.episodes(741L, 1).single().playableMediaAssetId())
                assertEquals(9002L, client.availability(741L).items.single().id)
            }
            assertEquals("Frieren", JSONObject(searchPayload.get()).getString("query"))
            assertEquals(2, JSONObject(searchPayload.get()).getInt("mediaTypeId"))
            assertEquals("/api/v1/discover/search", discoveredRoute.get())
        } finally {
            server.stop(0)
        }
    }

    @Test fun workOpenAndRequestsUseWorkIdAndChosenLocaleWithoutCookies() {
        val server = server()
        val submitted = mutableListOf<Triple<String, String, String>>()
        server.createContext("/api/v1/localization/locales") { exchange ->
            exchange.reply(200, """[{"id":11,"locale":"de","name":"Deutsch","isRightToLeft":false},
                {"id":12,"locale":"en","name":"English","isRightToLeft":false}]""")
        }
        server.createContext("/api/v1/works") { exchange ->
            val body = exchange.requestBody.bufferedReader().use { it.readText() }
            submitted += Triple(exchange.requestURI.path, exchange.requestHeaders.getFirst("Authorization"), body)
            when (exchange.requestURI.path) {
                "/api/v1/works/321/open" -> exchange.reply(200, """{"areDetailsUnavailable":false}""")
                "/api/v1/works/322/open" -> exchange.reply(200, """{"areDetailsUnavailable":true}""")
                else -> exchange.reply(200, """{"id":933}""")
            }
        }
        server.start()
        try {
            val api = HttpTvCatalogV1Client(
                "http://127.0.0.1:${server.address.port}", DeviceTokenStore("paired"),
            )
            runBlocking {
                assertEquals(listOf(11L, 12L), api.requestLocales().map { it.id })
                assertEquals(false, api.openWork(321))
                assertEquals(true, api.openWork(322))
                assertEquals(933L, api.requestWork(321, 11))
            }
            assertEquals("/api/v1/works/321/open", submitted[0].first)
            assertEquals("Bearer paired", submitted[0].second)
            assertEquals("/api/v1/works/322/open", submitted[1].first)
            assertEquals("/api/v1/works/321/requests", submitted[2].first)
            assertEquals("Bearer paired", submitted[2].second)
            assertEquals(11L, JSONObject(submitted[2].third).getLong("uiLocaleId"))
        } finally {
            server.stop(0)
        }
    }

    @Test fun revokedSessionClearsTokensAndSurfacesServerProblem() {
        val server = server()
        server.createContext("/api/v1/continue") {
            it.reply(401, """{"code":"invalid_token","resourceKey":"error.invalid_token"}""")
        }
        server.start()
        try {
            val tokens = DeviceTokenStore("revoked")
            val client = HttpTvCatalogV1Client("http://127.0.0.1:${server.address.port}", tokens)
            var caught = false
            try {
                runBlocking { client.continueWatching() }
            } catch (error: AppApiException) {
                assertEquals(401, error.statusCode)
                caught = true
            }
            assertTrue(caught)
            assertNull(tokens.current)
        } finally {
            server.stop(0)
        }
    }

    private fun server() = HttpServer.create(InetSocketAddress("127.0.0.1", 0), 0)

    private fun HttpExchange.reply(status: Int, json: String, token: String? = null) {
        val bytes = json.toByteArray(Charsets.UTF_8)
        responseHeaders.add("Content-Type", "application/json")
        if (token != null) responseHeaders.add(AppApiRoutes.DeviceTokenHeader, token)
        sendResponseHeaders(status, bytes.size.toLong())
        responseBody.use { it.write(bytes) }
    }

    private fun card() = """{"id":741,"mediaTypeId":2,"mediaSubtypeId":1,"title":"Frieren",
        "releaseYear":2023,"posterUrl":null,"rating":8.8,"mediaStateTypeId":3,
        "isWatchlisted":true,"availabilityStateId":2,"availableUnits":4,"monitoredUnits":28,
        "audioLanguages":["ja"],"subtitleLanguages":["de"],"progressStateId":2,"progressPercent":7}"""

    private fun workDetails() = """{"id":741,"mediaTypeId":2,"mediaSubtypeId":1,
        "canonicalTitle":"Frieren","isWatchlisted":true,"releaseYear":2023,"overview":null,
        "posterUrl":null,"backdropUrl":null,"plexUrl":null,"platforms":[],
        "franchiseId":null,"franchiseName":null,"isRequestable":false,"requestStatusTypeId":null,
        "runtimeMinutes":null,"genres":null,"trailerKey":null,"studio":null,
        "availabilityStateId":2,"availableUnits":4,"monitoredUnits":28,
        "progressStateId":2,"progressPercent":7,"rating":8.8,"ratingSource":"anilist",
        "originalTitle":null,"releasedOn":"2023-09-29","originalLanguage":"ja",
        "franchisePosition":null}"""

    private fun availability() = """{"id":9002,"workVersionId":901,"workEditionId":902,
        "workUnitId":8002,"mediaAssetTypeId":1,"workUnitTypeId":2,"unitNumber":2,
        "parentUnitNumber":1,"fileCount":1,"sizeBytes":1500000000,"durationMs":1400000,
        "isAvailable":true,"height":1080,"audioLanguages":["ja"],"subtitleLanguages":["de"],
        "resumePositionMs":40000,"isCompleted":false,"isEmulated":false,
        "groupName":null,"unitTitle":null,"hasProgress":true,"platformName":null,
        "unitImageUrl":null,"discNumber":null}"""
}
