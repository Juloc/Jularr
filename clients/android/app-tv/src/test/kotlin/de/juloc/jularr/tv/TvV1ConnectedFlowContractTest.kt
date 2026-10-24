package de.juloc.jularr.tv

import com.sun.net.httpserver.HttpExchange
import com.sun.net.httpserver.HttpServer
import de.juloc.jularr.core.api.AppApiRoutes
import de.juloc.jularr.core.api.catalog.HttpTvCatalogV1Client
import de.juloc.jularr.core.api.playback.HttpTvPlaybackV1Client
import de.juloc.jularr.core.api.playback.TvPlaybackV1Capabilities
import kotlinx.coroutines.runBlocking
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test
import java.net.InetSocketAddress
import java.util.concurrent.CopyOnWriteArrayList

/**
 * Real HTTP adapter chain, using backend-shaped responses. This does NOT run Compose,
 * Media3 video decoding or an actual clean-start server.
 */
class TvV1ConnectedFlowContractTest {
    private val sessionId = "1d378ccd-e0bd-4af9-8fbc-6604762e35ca"

    @Test fun pairingThroughSearchAndEpisodesIntoRealAssetPlayback() = runBlocking {
        val requests = CopyOnWriteArrayList<String>()
        val server = HttpServer.create(InetSocketAddress("127.0.0.1", 0), 0)
        server.createContext("/api/v1/") { e ->
            requests.add("${e.requestMethod} ${e.requestURI.path}")
            val path = e.requestURI.path
            if (path != "/api/v1/devices/pairings" && !path.endsWith("/claim")) {
                if (e.requestHeaders.getFirst("Authorization") != "Bearer test-token") {
                    e.respond(401, """{"code":"invalid_token","resourceKey":"error.invalid_token"}""")
                    return@createContext
                }
            }
            when {
                path == "/api/v1/devices/pairings" -> e.respond(
                    200, """{"pairingId":"pair-1","code":"314159","expiresAt":"2026-10-22T23:00:00Z"}"""
                )
                path == "/api/v1/devices/pairings/pair-1/claim" ->
                    e.respond(200, """{"isPaired":true}""", "test-token")
                path == "/api/v1/instance/capabilities" -> e.respond(
                    200, """{"permissions":["profile.media.read","profile.playback"],
                        "modules":["Library","Playback","Devices"]}"""
                )
                path == "/api/v1/library" -> e.respond(
                    200, """{"items":[${card()}],"page":1,"pageSize":25}"""
                )
                path == "/api/v1/continue" -> e.respond(
                    200, """[{"workId":741,"mediaAssetId":9002,"mediaAssetTypeId":1,
                    "mediaTypeId":2,"mediaSubtypeId":1,"title":"Sample Anime","releaseYear":2026,
                    "posterUrl":null,"workUnitTypeId":2,"unitNumber":2,"parentUnitNumber":1,
                    "positionMs":1000,"durationMs":100000,"progression":null}]"""
                )
                path == "/api/v1/works/741" -> e.respond(
                    200, """{"id":741,"mediaTypeId":2,"mediaSubtypeId":1,
                        "canonicalTitle":"Sample Anime","isWatchlisted":true,
                        "releaseYear":2026,"overview":null,"posterUrl":null,
                        "backdropUrl":null,"plexUrl":null,"platforms":[],
                        "franchiseId":null,"franchiseName":null,"isRequestable":false,
                        "requestStatusTypeId":null,"runtimeMinutes":null,"genres":null,
                        "trailerKey":null,"studio":null,"availabilityStateId":2,
                        "availableUnits":1,"monitoredUnits":2,"progressStateId":2,
                        "progressPercent":1,"rating":null,"ratingSource":null,
                        "originalTitle":null,"releasedOn":null,"originalLanguage":null,
                        "franchisePosition":null}"""
                )
                path == "/api/v1/works/741/seasons" -> e.respond(
                    200, """[{"id":11,"seasonNumber":1,"title":null,"imageUrl":null,
                        "releaseYear":2026,"episodeCount":2,"availableCount":1,"watchedCount":0}]"""
                )
                path == "/api/v1/works/741/seasons/1/episodes" -> e.respond(
                    200, """[{"id":8001,"seasonNumber":1,"number":1,"title":"Missing",
                        "overview":null,"imageUrl":null,"runtimeMinutes":24,"releasedOn":null,
                        "mediaAssetId":null,"resumePositionMs":null,"durationMs":null,
                        "isCompleted":false,"mediaStateTypeId":2,"isUpcoming":false,"groupName":null},
                        {"id":8002,"seasonNumber":1,"number":2,"title":"Playable",
                        "overview":null,"imageUrl":null,"runtimeMinutes":24,"releasedOn":null,
                        "mediaAssetId":9002,"resumePositionMs":1000,"durationMs":100000,
                        "isCompleted":false,"mediaStateTypeId":3,"isUpcoming":false,"groupName":null}]"""
                )
                path == "/api/v1/playback/assets/9002/sessions" -> e.respond(
                    200, """{"playbackSessionId":"$sessionId","playbackModeTypeId":1,
                    "streamUrl":"/api/v1/playback/sessions/$sessionId/stream",
                    "resumePositionMs":1000,"durationMs":100000,"audioStreamIndex":null,
                    "quality":1,"isReduced":false}"""
                )
                path == "/api/v1/playback/sessions/$sessionId" -> e.respond(
                    200, """{"success":true}"""
                )
                else -> e.respond(404, """{"code":"not_found","resourceKey":"error.not_found"}""")
            }
        }
        server.start()
        try {
            val serverOrigin = "http://127.0.0.1:${server.address.port}"
            val vault = MemoryVault()
            val session = TvV1DeviceSession(vault)
            session.selectServer(serverOrigin)
            val pairing = session.startPairing()
            assertEquals("314159", pairing.code)
            assertTrue(session.claimPairing(pairing.pairingId).isPaired)
            val capabilities = requireNotNull(session.restore())
            assertTrue(capabilities.hasModule("Playback"))
            assertEquals("test-token", vault.token)

            val tokens = session.authenticatedTokenStore()
            val catalog = HttpTvCatalogV1Client(serverOrigin, tokens)
            val player = HttpTvPlaybackV1Client(serverOrigin, tokens)

            val home = catalog.library(page = 1, pageSize = 25)
            val homeItem = TvV1LegacyUiModels.videoCard(home.items.single())
            assertEquals("741", homeItem?.id)
            val search = catalog.library(page = 1, pageSize = 25, query = "Anime")
            assertEquals(741L, search.items.single().id)
            val continueItem = catalog.continueWatching().single()
            assertEquals(9002L, continueItem.mediaAssetId)

            val work = catalog.work(741L)
            val seasons = catalog.seasons(741L)
            val episodes = catalog.episodes(741L, seasons.single().seasonNumber)
            val displayed = TvV1LegacyUiModels.detail(work, seasons, mapOf(1 to episodes))
            assertEquals(listOf("8001", "8002"), displayed.seasons.single().episodes.map { it.id })
            assertFalse(displayed.seasons.single().episodes.first().hasMedia)
            assertTrue(displayed.seasons.single().episodes.last().hasMedia)
            assertNull(episodes.first().playableMediaAssetId())
            val actualAssetId = requireNotNull(episodes.last().playableMediaAssetId())
            assertEquals(9002L, actualAssetId)
            assertFalse(actualAssetId == episodes.last().id)
            assertFalse(actualAssetId == work.id)

            val plan = player.start(actualAssetId, TvPlaybackV1Capabilities(
                containers = listOf("mp4", "hls"), videoCodecs = listOf("h264"),
                audioCodecs = listOf("aac")
            ))
            assertEquals(sessionId, plan.playbackSessionId)
            assertTrue(plan.streamUrl.endsWith("/api/v1/playback/sessions/$sessionId/stream"))
            player.update(sessionId, 2500, 100000, isEnded = true, isCompleted = false)
            assertTrue(requests.contains("POST /api/v1/playback/assets/9002/sessions"))
            assertFalse(requests.contains("POST /api/v1/playback/assets/8002/sessions"))
            assertFalse(requests.contains("POST /api/v1/playback/assets/741/sessions"))
        } finally {
            server.stop(0)
        }
    }

    private class MemoryVault : TvDeviceTokenPersistence {
        var token: String? = null
        override fun loadToken(serverOrigin: String): String? = token
        override fun storeToken(serverOrigin: String, token: String?) { this.token = token }
    }

    private fun card() = """{"id":741,"mediaTypeId":2,"mediaSubtypeId":1,
        "title":"Sample Anime","releaseYear":2026,"posterUrl":null,"rating":null,
        "mediaStateTypeId":3,"isWatchlisted":true,"availabilityStateId":2,
        "availableUnits":1,"monitoredUnits":2,"audioLanguages":[],
        "subtitleLanguages":[],"progressStateId":2,"progressPercent":1}"""

    private fun HttpExchange.respond(status: Int, json: String, deviceToken: String? = null) {
        val bytes = json.toByteArray(Charsets.UTF_8)
        responseHeaders.add("Content-Type", "application/json")
        if (deviceToken != null) responseHeaders.add(AppApiRoutes.DeviceTokenHeader, deviceToken)
        sendResponseHeaders(status, bytes.size.toLong())
        responseBody.use { it.write(bytes) }
    }
}
