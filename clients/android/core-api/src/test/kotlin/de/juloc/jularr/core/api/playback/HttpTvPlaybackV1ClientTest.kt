package de.juloc.jularr.core.api.playback

import com.sun.net.httpserver.HttpExchange
import com.sun.net.httpserver.HttpServer
import de.juloc.jularr.core.api.AppApiException
import de.juloc.jularr.core.api.AppApiRoutes
import de.juloc.jularr.core.api.DeviceTokenStore
import kotlinx.coroutines.runBlocking
import org.json.JSONObject
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertThrows
import org.junit.Assert.assertTrue
import org.junit.Test
import java.net.InetSocketAddress
import java.util.UUID
import java.util.concurrent.atomic.AtomicReference

class HttpTvPlaybackV1ClientTest {
    private val session = "83de7c58-a8b2-4005-8d50-7fb4e80c8021"

    @Test fun startSessionAndReportProgressUsingOnlyRealMediaAssetIds() {
        val server = localServer()
        val startBody = AtomicReference<String>()
        val progressBody = AtomicReference<String>()
        val startAuth = AtomicReference<String>()
        val progressAuth = AtomicReference<String>()
        server.createContext("/api/v1/playback/assets/9002/sessions") { exchange ->
            startAuth.set(exchange.requestHeaders.getFirst("Authorization"))
            startBody.set(exchange.requestBody.bufferedReader().use { it.readText() })
            exchange.reply(200, """{"playbackSessionId":"$session","playbackModeTypeId":3,
                "streamUrl":"/api/v1/playback/sessions/$session/playlist.m3u8",
                "resumePositionMs":40000,"durationMs":1400000,"audioStreamIndex":1,
                "quality":1,"isReduced":false}""", "rotated-token")
        }
        server.createContext("/api/v1/playback/sessions/$session") { exchange ->
            progressAuth.set(exchange.requestHeaders.getFirst("Authorization"))
            progressBody.set(exchange.requestBody.bufferedReader().use { it.readText() })
            exchange.reply(200, """{"success":true}""")
        }
        server.start()
        try {
            val tokens = DeviceTokenStore("original-token")
            val origin = "http://127.0.0.1:${server.address.port}"
            val api = HttpTvPlaybackV1Client(origin, tokens)
            val plan = runBlocking {
                val created = api.start(
                    9002L,
                    TvPlaybackV1Capabilities(
                        listOf("mp4", "hls"),
                        listOf("h264"),
                        listOf("aac"),
                    ),
                )
                api.update(created.playbackSessionId, 54000, 1400000, isEnded = false, isCompleted = false)
                created
            }
            assertEquals(session, plan.playbackSessionId)
            assertEquals(3, plan.playbackModeTypeId)
            assertEquals("$origin/api/v1/playback/sessions/$session/playlist.m3u8", plan.streamUrl)
            assertEquals(40000L, plan.resumePositionMs)
            assertEquals(1400000L, plan.durationMs)
            assertEquals("Bearer original-token", startAuth.get())
            assertEquals("Bearer rotated-token", progressAuth.get())
            assertEquals("rotated-token", tokens.current)
            val start = JSONObject(startBody.get())
            assertEquals("hls", start.getJSONArray("containers").getString(1))
            assertEquals(1, start.getJSONArray("videoCodecs").length())
            val update = JSONObject(progressBody.get())
            assertEquals(54000L, update.getLong("positionMs"))
            assertFalse(update.getBoolean("isEnded"))
            assertFalse(update.getBoolean("isCompleted"))
        } finally {
            server.stop(0)
        }
    }

    @Test fun invalidStreamRoutesAndIdsAreNeverOpenedWithBearerAuthorization() {
        val server = localServer()
        server.createContext("/api/v1/playback/assets/9002/sessions") { exchange ->
            exchange.reply(200, """{"playbackSessionId":"$session","playbackModeTypeId":1,
                "streamUrl":"https://evil.example/stream",
                "resumePositionMs":0,"durationMs":null,
                "audioStreamIndex":null,"quality":1,"isReduced":false}""")
        }
        server.start()
        try {
            val api = HttpTvPlaybackV1Client("http://127.0.0.1:${server.address.port}", DeviceTokenStore("token"))
            assertThrows(IllegalArgumentException::class.java) {
                runBlocking { api.start(0, TvPlaybackV1Capabilities(emptyList(), emptyList(), emptyList())) }
            }
            assertThrows(IllegalArgumentException::class.java) {
                runBlocking { api.start(9002, TvPlaybackV1Capabilities(listOf("mp4"), listOf("h264"), listOf("aac"))) }
            }
            assertThrows(IllegalArgumentException::class.java) {
                runBlocking { api.update(session, 1, null, isEnded = false, isCompleted = true) }
            }
        } finally {
            server.stop(0)
        }
    }

    @Test fun revokedSessionClearsPairedDeviceToken() {
        val server = localServer()
        server.createContext("/api/v1/playback/assets/9002/sessions") {
            it.reply(401, """{"code":"invalid_token","resourceKey":"error.invalid_token"}""")
        }
        server.start()
        try {
            val tokens = DeviceTokenStore("revoked")
            val api = HttpTvPlaybackV1Client("http://127.0.0.1:${server.address.port}", tokens)
            val failure = assertThrows(AppApiException::class.java) {
                runBlocking { api.start(9002, TvPlaybackV1Capabilities(listOf("mp4"), listOf("h264"), listOf("aac"))) }
            }
            assertEquals(401, failure.statusCode)
            assertNull(tokens.current)
        } finally {
            server.stop(0)
        }
    }

    @Test fun lateUnauthorizedResponseCannotInvalidateRotatedDeviceToken() {
        val tokens = DeviceTokenStore("initial-token")
        val attempts = mutableListOf<String?>()
        val server = localServer()
        server.createContext("/api/v1/playback/assets/9002/sessions") { exchange ->
            val supplied = exchange.requestHeaders.getFirst("Authorization")
            synchronized(attempts) { attempts.add(supplied) }
            if (supplied == "Bearer initial-token") {
                // Another request/profile switch rotated the token while this request was in flight.
                tokens.update("fresh-token")
                exchange.reply(401, """{"code":"invalid_token","resourceKey":"error.invalid_token"}""")
            } else {
                exchange.reply(200, """{"playbackSessionId":"$session","playbackModeTypeId":1,
                    "streamUrl":"/api/v1/playback/sessions/$session/stream",
                    "resumePositionMs":0,"durationMs":50000,"audioStreamIndex":null,
                    "quality":1,"isReduced":false}""")
            }
        }
        server.start()
        try {
            val client = HttpTvPlaybackV1Client("http://127.0.0.1:${server.address.port}", tokens)
            val started = runBlocking {
                client.start(9002, TvPlaybackV1Capabilities(listOf("mp4"), listOf("h264"), listOf("aac")))
            }
            assertEquals(1, started.playbackModeTypeId)
            assertEquals(listOf("Bearer initial-token", "Bearer fresh-token"), attempts)
            assertEquals("fresh-token", tokens.current)
        } finally {
            server.stop(0)
        }
    }

    private fun localServer() = HttpServer.create(InetSocketAddress("127.0.0.1", 0), 0)

    private fun HttpExchange.reply(status: Int, json: String, token: String? = null) {
        val bytes = json.toByteArray(Charsets.UTF_8)
        responseHeaders.add("Content-Type", "application/json")
        if (token != null) responseHeaders.add(AppApiRoutes.DeviceTokenHeader, token)
        sendResponseHeaders(status, bytes.size.toLong())
        responseBody.use { it.write(bytes) }
    }
}
