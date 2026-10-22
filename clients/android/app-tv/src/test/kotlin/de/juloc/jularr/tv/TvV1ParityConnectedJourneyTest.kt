package de.juloc.jularr.tv

import com.sun.net.httpserver.HttpExchange
import com.sun.net.httpserver.HttpServer
import de.juloc.jularr.core.api.AppApiRoutes
import de.juloc.jularr.core.api.catalog.HttpTvCatalogV1Client
import de.juloc.jularr.core.api.playback.HttpTvPlaybackV1Client
import de.juloc.jularr.core.api.playback.TvPlaybackV1Capabilities
import de.juloc.jularr.core.api.playbackextras.HttpTvV1PlaybackExtrasClient
import de.juloc.jularr.core.api.profile.HttpTvV1ProfileClient
import de.juloc.jularr.core.api.tvlegacy.HttpTvV1LegacyFeaturesClient
import kotlinx.coroutines.runBlocking
import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Test
import java.net.InetSocketAddress
import java.util.concurrent.CopyOnWriteArrayList

/**
 * Contract E2E: pairing -> profile/PIN -> home/search -> work/WorkUnit -> MediaAsset
 * -> stream + real markers/tracks/chapters -> progress -> server-reported next asset.
 *
 * HTTP fixture mirrors verified /api/v1 shapes. It does NOT run Compose/Media3 or a real NAS.
 * Fixture IDs are test-only: WorkId 741, WorkUnitId 8002, MediaAssetIds 9002 and 9003.
 */
class TvV1ParityConnectedJourneyTest {
    private val pairingId = "e1487548-52b5-4dfb-a017-bd41e69e4a0f"
    private val firstSessionId = "838e264f-a99e-4422-a85f-f29a8d15e678"
    private val secondSessionId = "3c8d492e-274d-41a5-b85b-a3cdd74e8012"
    private val supported = TvPlaybackV1Capabilities(listOf("mp4", "hls"), listOf("h264"), listOf("aac"))

    @Test fun originalTvUserJourneyKeepsOnlyRealServerAssetsAndRotatingBearer() = runBlocking {
        val requests = CopyOnWriteArrayList<String>()
        val progress = CopyOnWriteArrayList<JSONObject>()
        val server = HttpServer.create(InetSocketAddress("127.0.0.1", 0), 0)
        server.createContext("/api/v1/") { exchange ->
            val path = exchange.requestURI.path
            requests.add("${exchange.requestMethod} $path")
            val authorization = exchange.requestHeaders.getFirst("Authorization")
            if (path == "/api/v1/devices/pairings") {
                exchange.json(200, """{"pairingId":"$pairingId","code":"314159","expiresAt":"2026-10-23T18:00:00Z"}""")
                return@createContext
            }
            if (path == "/api/v1/devices/pairings/$pairingId/claim") {
                exchange.json(200, """{"isPaired":true}""", nextToken = "paired-token")
                return@createContext
            }
            val expectedToken = when (path) {
                "/api/v1/instance/capabilities", "/api/v1/profiles", "/api/v1/auth/profile/9/pin" ->
                    if (path == "/api/v1/instance/capabilities" && requests.count { it.contains("capabilities") } > 1) "profile-token"
                    else "paired-token"
                "/api/v1/playback/assets/9002/sessions" -> "profile-token"
                "/api/v1/playback/sessions/$firstSessionId",
                "/api/v1/playback/assets/9003/sessions",
                "/api/v1/playback/sessions/$secondSessionId" -> "stream-token"
                else -> if (requests.any { it == "POST /api/v1/playback/assets/9002/sessions" }) "stream-token" else "profile-token"
            }
            if (authorization != "Bearer $expectedToken") {
                exchange.json(401, """{"code":"invalid_token","resourceKey":"error.invalid_token"}""")
                return@createContext
            }
            when (path) {
                "/api/v1/instance/capabilities" -> exchange.json(200,
                    """{"permissions":["profile.media.read","profile.playback"],"modules":["Library","Playback","Devices"]}""")
                "/api/v1/profiles" -> exchange.json(200,
                    """{"items":[{"id":7,"displayName":"Default","isLocked":false,"pinLength":null,"isOwned":true},
                        {"id":9,"displayName":"Viewer","isLocked":true,"pinLength":4,"isOwned":false}],
                        "page":1,"pageSize":100}""")
                "/api/v1/auth/profile/9/pin" -> {
                    assertEquals("1234", JSONObject(exchange.requestBody.bufferedReader().readText()).getString("pin"))
                    exchange.json(200, """{"profileId":9}""", nextToken = "profile-token")
                }
                "/api/v1/library" -> exchange.json(200,
                    """{"items":[${card()}],"page":1,"pageSize":25}""")
                "/api/v1/works/741" -> exchange.json(200,
                    """{"id":741,"mediaTypeId":2,"mediaSubtypeId":1,"canonicalTitle":"Test Series",
                       "isWatchlisted":true,"releaseYear":2026,"overview":null,"posterUrl":null,
                       "backdropUrl":null,"plexUrl":null,"platforms":[],"franchiseId":null,
                       "franchiseName":null,"isRequestable":false,"requestStatusTypeId":null,
                       "runtimeMinutes":null,"genres":null,"trailerKey":null,"studio":null,
                       "availabilityStateId":2,"availableUnits":2,"monitoredUnits":2,
                       "progressStateId":2,"progressPercent":15,"rating":null,"ratingSource":null,
                       "originalTitle":null,"releasedOn":null,"originalLanguage":null,
                       "franchisePosition":null}""")
                "/api/v1/works/741/seasons" -> exchange.json(200,
                    """[{"id":100,"seasonNumber":1,"title":null,"imageUrl":null,"releaseYear":2026,
                       "episodeCount":2,"availableCount":2,"watchedCount":0}]""")
                "/api/v1/works/741/seasons/1/episodes" -> exchange.json(200,
                    """[{"id":8002,"seasonNumber":1,"number":1.5,"title":"Special",
                         "overview":null,"imageUrl":null,"runtimeMinutes":24,"releasedOn":null,
                         "mediaAssetId":9002,"resumePositionMs":5000,"durationMs":100000,
                         "isCompleted":false,"mediaStateTypeId":3,"isUpcoming":false,"groupName":null}]""")
                "/api/v1/playback/assets/9002" -> exchange.json(200, asset(9002, 9003))
                "/api/v1/playback/assets/9003" -> exchange.json(200, asset(9003, null))
                "/api/v1/playback/assets/9002/markers" -> exchange.json(200,
                    """[{"mediaMarkerKindTypeId":1,"startMs":300,"endMs":5000}]""")
                "/api/v1/playback/assets/9002/chapters" -> exchange.json(200,
                    """[{"chapterIndex":0,"startMs":0,"endMs":60000,"title":"Chapter one"}]""")
                "/api/v1/playback/assets/9002/tracks" -> exchange.json(200,
                    """[{"key":"s2","mediaTrackTypeId":2,"language":"ja","title":"Japanese",
                         "channels":2,"isDefault":true,"isForced":false,"isSupported":true,"isSelected":true},
                        {"key":"f31","mediaTrackTypeId":3,"language":"de","title":null,"channels":null,
                         "isDefault":false,"isForced":false,"isSupported":true,"isSelected":false}]""")
                "/api/v1/playback/assets/9002/subtitles/f31" ->
                    exchange.text(200, "WEBVTT\n\n00:00:01.000 --> 00:00:02.000\nHallo", "text/vtt")
                "/api/v1/playback/assets/9002/trickplay" -> exchange.json(200,
                    """{"intervalSeconds":10,"frames":41,"columns":4,"rows":3,"tileWidth":160,"tileHeight":90}""")
                "/api/v1/playback/preferences" -> exchange.json(200,
                    """{"audioLanguage":"ja","subtitleLanguage":"de","subtitleModeTypeId":3,
                       "speed":1.0,"autoplayNext":true,"introSkipModeTypeId":2,
                       "outroSkipModeTypeId":1,"autoplayPreviews":false,
                       "profileLanguage":"de","deviceQuality":1}""")
                "/api/v1/playback/assets/9002/sessions" -> {
                    val request = JSONObject(exchange.requestBody.bufferedReader().readText())
                    assertEquals("h264", request.getJSONArray("videoCodecs").getString(0))
                    exchange.json(200, plan(firstSessionId, 5000, null), nextToken = "stream-token")
                }
                "/api/v1/playback/assets/9003/sessions" -> {
                    val request = JSONObject(exchange.requestBody.bufferedReader().readText())
                    assertEquals(2, request.getInt("audioStreamIndex"))
                    exchange.json(200, plan(secondSessionId, 0, 2))
                }
                "/api/v1/playback/sessions/$firstSessionId",
                "/api/v1/playback/sessions/$secondSessionId" -> {
                    progress.add(JSONObject(exchange.requestBody.bufferedReader().readText()))
                    exchange.json(200, """{"success":true}""")
                }
                else -> exchange.json(404, """{"code":"not_found","resourceKey":"error.not_found"}""")
            }
        }
        server.start()
        try {
            val origin = "http://127.0.0.1:${server.address.port}"
            val vault = MemoryVault()
            val device = TvV1DeviceSession(vault)
            device.selectServer(origin)
            val pairing = device.startPairing()
            assertEquals("314159", pairing.code)
            assertTrue(device.claimPairing(pairing.pairingId).isPaired)
            assertEquals("paired-token", vault.token)
            val tokens = device.authenticatedTokenStore()
            val profiles = HttpTvV1ProfileClient(origin, tokens)
            assertEquals(listOf(7L, 9L), profiles.profiles().map { it.id })
            assertEquals(9L, profiles.selectProfile(9, "1234"))
            assertEquals("profile-token", tokens.current)
            val catalog = HttpTvCatalogV1Client(origin, tokens)
            val player = HttpTvPlaybackV1Client(origin, tokens)
            val extras = HttpTvV1PlaybackExtrasClient(origin, tokens)
            val identity = HttpTvV1LegacyFeaturesClient(origin, tokens)

            assertEquals(741L, catalog.library(page = 1, pageSize = 25).items.single().id)
            assertEquals(741L, catalog.library(page = 1, pageSize = 25, query = "Test").items.single().id)
            assertEquals("Test Series", catalog.work(741).canonicalTitle)
            assertEquals(1, catalog.seasons(741).single().seasonNumber)
            val episode = catalog.episodes(741, 1).single()
            assertEquals(8002L, episode.id) // WorkUnitId: never a playable MediaAssetId.
            assertEquals("1.5", episode.number.toPlainString())
            assertEquals(9002L, episode.playableMediaAssetId())
            assertNotEquals(741L, episode.playableMediaAssetId())
            val asset = identity.playbackAsset(9002)
            assertEquals(741L, asset.workId)
            assertEquals(9003L, asset.nextMediaAssetId)

            val prefs = extras.preferences()
            assertTrue(prefs.autoplayNext)
            assertEquals(5000L, extras.markers(9002).single().endMs)
            assertEquals("Chapter one", extras.chapters(9002).single().title)
            val tracks = extras.tracks(9002)
            assertEquals("s2", tracks.first().key)
            assertEquals("f31", tracks.last().key)
            assertTrue(extras.subtitleVtt(9002, "f31").startsWith("WEBVTT"))
            assertEquals(4, extras.trickplay(9002).sheetCount)

            val first = player.start(9002, supported)
            assertEquals(firstSessionId, first.playbackSessionId)
            assertEquals(5000L, first.resumePositionMs)
            assertEquals("stream-token", tokens.current)
            player.update(first.playbackSessionId, 89000, 100000, isEnded = true, isCompleted = false)

            // The next asset is supplied by the backend, not derived from fractional episode 1.5.
            val next = identity.playbackAsset(requireNotNull(asset.nextMediaAssetId))
            assertEquals(9003L, next.mediaAssetId)
            val second = player.start(next.mediaAssetId, supported, audioStreamIndex = 2)
            assertEquals(secondSessionId, second.playbackSessionId)
            player.update(second.playbackSessionId, 100000, 100000, isEnded = true, isCompleted = true)

            assertEquals(2, progress.size)
            assertFalse(progress[0].getBoolean("isCompleted"))
            assertTrue(progress[0].getBoolean("isEnded"))
            assertTrue(progress[1].getBoolean("isCompleted"))
            assertTrue(requests.contains("PUT /api/v1/auth/profile/9/pin").not()) // PIN uses POST.
            assertTrue(requests.contains("POST /api/v1/auth/profile/9/pin"))
            assertTrue(requests.contains("POST /api/v1/playback/assets/9003/sessions"))
            assertFalse(requests.contains("POST /api/v1/playback/assets/8002/sessions"))
            assertFalse(requests.any { it.contains("/api/client/v1") })
            assertEquals("stream-token", vault.token)
        } finally {
            server.stop(0)
        }
    }

    private fun card() = """{"id":741,"mediaTypeId":2,"mediaSubtypeId":1,"title":"Test Series",
        "releaseYear":2026,"posterUrl":null,"rating":null,"mediaStateTypeId":3,
        "isWatchlisted":true,"availabilityStateId":2,"availableUnits":2,"monitoredUnits":2,
        "audioLanguages":["ja"],"subtitleLanguages":["de"],"progressStateId":2,"progressPercent":15}"""

    private fun asset(id: Long, next: Long?) =
        """{"mediaAssetId":$id,"workId":741,"canonicalTitle":"Test Series",
            "mediaAssetTypeId":1,"workUnitTypeId":2,"unitNumber":1.5,"parentUnitNumber":1,
            "unitTitle":"Special","resumePositionMs":5000,"previousMediaAssetId":null,
            "nextMediaAssetId":${next ?: "null"}}"""

    private fun plan(session: String, position: Long, index: Int?) =
        """{"playbackSessionId":"$session","playbackModeTypeId":1,
            "streamUrl":"/api/v1/playback/sessions/$session/stream",
            "resumePositionMs":$position,"durationMs":100000,
            "audioStreamIndex":${index ?: "null"},"quality":1,"isReduced":false}"""

    private class MemoryVault : TvDeviceTokenPersistence {
        var token: String? = null
        override fun loadToken(serverOrigin: String): String? = token
        override fun storeToken(serverOrigin: String, token: String?) { this.token = token }
    }

    private fun HttpExchange.json(status: Int, value: String, nextToken: String? = null) =
        text(status, value, "application/json", nextToken)

    private fun HttpExchange.text(status: Int, value: String, contentType: String, nextToken: String? = null) {
        val bytes = value.toByteArray(Charsets.UTF_8)
        responseHeaders.add("Content-Type", contentType)
        if (nextToken != null) responseHeaders.add(AppApiRoutes.DeviceTokenHeader, nextToken)
        sendResponseHeaders(status, bytes.size.toLong())
        responseBody.use { it.write(bytes) }
    }
}
