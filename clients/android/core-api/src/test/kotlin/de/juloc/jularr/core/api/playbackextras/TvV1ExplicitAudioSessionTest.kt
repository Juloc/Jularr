package de.juloc.jularr.core.api.playbackextras

import com.sun.net.httpserver.HttpExchange
import com.sun.net.httpserver.HttpServer
import de.juloc.jularr.core.api.AppApiRoutes
import de.juloc.jularr.core.api.DeviceTokenStore
import de.juloc.jularr.core.api.playback.TvPlaybackV1Capabilities
import kotlinx.coroutines.runBlocking
import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Test
import java.net.InetSocketAddress
import java.util.concurrent.atomic.AtomicReference

/** POST /api/v1/playback/assets/{mediaAssetId}/sessions accepts audioStreamIndex. */
class TvV1ExplicitAudioSessionTest {
    private val session = "83de7c58-a8b2-4005-8d50-7fb4e80c8021"
    private val capabilities = TvPlaybackV1Capabilities(listOf("mp4", "hls"), listOf("h264"), listOf("aac"))
    private val supportedAudio = TvV1PlaybackTrack(
        key = "s2", mediaTrackTypeId = 2, language = "ja", title = "Japanese",
        channels = 2, isDefault = false, isForced = false, isSupported = true, isSelected = false,
    )

    @Test fun startsPlaybackWithChosenServerAudioStreamAndUsesSameRotatedBearer() {
        val server = HttpServer.create(InetSocketAddress("127.0.0.1", 0), 0)
        val startPayload = AtomicReference<String>()
        val sentAuthorization = AtomicReference<String>()
        server.createContext("/api/v1/playback/assets/9002/sessions") { exchange ->
            assertEquals("POST", exchange.requestMethod)
            startPayload.set(exchange.requestBody.bufferedReader().use { it.readText() })
            sentAuthorization.set(exchange.requestHeaders.getFirst("Authorization"))
            exchange.reply("""{"playbackSessionId":"$session","playbackModeTypeId":3,
                "streamUrl":"/api/v1/playback/sessions/$session/playlist.m3u8",
                "resumePositionMs":25000,"durationMs":90000,"audioStreamIndex":2,
                "quality":3,"isReduced":false}""", "next-token")
        }
        server.start()
        try {
            val tokens = DeviceTokenStore("original")
            val origin = "http://127.0.0.1:${server.address.port}"
            val api = HttpTvV1PlaybackExtrasClient(origin, tokens)
            val plan = runBlocking {
                api.startWithAudioTrack(9002, capabilities, supportedAudio, quality = 3)
            }
            assertEquals(session, plan.playbackSessionId)
            assertEquals(2, plan.audioStreamIndex)
            assertEquals(3, plan.playbackModeTypeId)
            assertEquals(25000L, plan.resumePositionMs)
            assertEquals("$origin/api/v1/playback/sessions/$session/playlist.m3u8", plan.streamUrl)
            assertEquals("Bearer original", sentAuthorization.get())
            assertEquals("next-token", tokens.current)
            val payload = JSONObject(startPayload.get())
            assertEquals(2, payload.getInt("audioStreamIndex"))
            assertEquals(3, payload.getInt("quality"))
            assertEquals("hls", payload.getJSONArray("containers").getString(1))
            assertEquals("h264", payload.getJSONArray("videoCodecs").getString(0))
        } finally {
            server.stop(0)
        }
    }

    @Test fun rejectsUnsupportedOrNonAudioTracksBeforeStartingASession() {
        val api = HttpTvV1PlaybackExtrasClient("https://jularr.example", DeviceTokenStore("token"))
        assertTrue(runCatching {
            runBlocking { api.startWithAudioTrack(9002, capabilities, supportedAudio.copy(isSupported = false)) }
        }.isFailure)
        assertTrue(runCatching {
            runBlocking { api.startWithAudioTrack(9002, capabilities, supportedAudio.copy(key = "f123", mediaTrackTypeId = 3)) }
        }.isFailure)
        assertTrue(runCatching {
            runBlocking { api.startWithAudioTrack(0, capabilities, supportedAudio) }
        }.isFailure)
        assertTrue(runCatching {
            runBlocking { api.startWithAudioTrack(9002, capabilities, supportedAudio, quality = 5) }
        }.isFailure)
        assertTrue(runCatching {
            runBlocking { api.startWithAudioTrack(9002, capabilities.copy(containers = List(33) { "mp4" }), supportedAudio) }
        }.isFailure)
    }

    @Test fun rejectsUntrustedPlaybackStreamLocationAndMismatchedAudioStream() {
        val server = HttpServer.create(InetSocketAddress("127.0.0.1", 0), 0)
        server.createContext("/api/v1/playback/assets/9002/sessions") { exchange ->
            exchange.reply("""{"playbackSessionId":"$session","playbackModeTypeId":1,
                "streamUrl":"https://outside.example/stream",
                "resumePositionMs":0,"durationMs":90000,"audioStreamIndex":2,
                "quality":1,"isReduced":false}""")
        }
        server.start()
        try {
            val api = HttpTvV1PlaybackExtrasClient("http://127.0.0.1:${server.address.port}", DeviceTokenStore("token"))
            assertTrue(runCatching {
                runBlocking { api.startWithAudioTrack(9002, capabilities, supportedAudio) }
            }.isFailure)
        } finally {
            server.stop(0)
        }
    }

    private fun HttpExchange.reply(json: String, nextToken: String? = null) {
        val bytes = json.toByteArray(Charsets.UTF_8)
        responseHeaders.add("Content-Type", "application/json")
        if (nextToken != null) responseHeaders.add(AppApiRoutes.DeviceTokenHeader, nextToken)
        sendResponseHeaders(200, bytes.size.toLong())
        responseBody.use { it.write(bytes) }
    }
}
