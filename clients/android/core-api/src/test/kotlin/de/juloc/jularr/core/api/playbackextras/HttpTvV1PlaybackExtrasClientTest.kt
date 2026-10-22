package de.juloc.jularr.core.api.playbackextras

import com.sun.net.httpserver.HttpExchange
import com.sun.net.httpserver.HttpServer
import de.juloc.jularr.core.api.AppApiException
import de.juloc.jularr.core.api.AppApiRoutes
import de.juloc.jularr.core.api.DeviceTokenStore
import kotlinx.coroutines.runBlocking
import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Test
import java.math.BigDecimal
import java.net.InetSocketAddress
import java.util.concurrent.atomic.AtomicReference

class HttpTvV1PlaybackExtrasClientTest {
    @Test fun realApiV1RoutesDecodeAndApplyPreferencesWithRotatedBearer() {
        val server = HttpServer.create(InetSocketAddress("127.0.0.1", 0), 0)
        val subtitleHeaders = AtomicReference<String>()
        val preferenceBody = AtomicReference<String>()
        val speedBody = AtomicReference<String>()
        val requests = mutableListOf<String>()
        server.createContext("/api/v1/playback/assets/9002") { exchange ->
            synchronized(requests) { requests.add(exchange.requestMethod + " " + exchange.requestURI.path) }
            when (exchange.requestURI.path) {
                "/api/v1/playback/assets/9002/markers" ->
                    exchange.reply(200, """[{"mediaMarkerKindTypeId":1,"startMs":2000,"endMs":99000}]""", "application/json", "fresh")
                "/api/v1/playback/assets/9002/chapters" ->
                    exchange.reply(200, """[{"chapterIndex":0,"startMs":0,"endMs":50000,"title":null}]""")
                "/api/v1/playback/assets/9002/trickplay" ->
                    exchange.reply(200, """{"intervalSeconds":10,"frames":42,"columns":4,"rows":4,"tileWidth":160,"tileHeight":90}""")
                "/api/v1/playback/assets/9002/trickplay/1" ->
                    exchange.replyBytes(200, byteArrayOf(0xff.toByte(), 0xd8.toByte(), 0xff.toByte(), 0xd9.toByte()), "image/jpeg")
                "/api/v1/playback/assets/9002/tracks" ->
                    exchange.reply(200, """[{"key":"s1","mediaTrackTypeId":2,"language":"ja","title":null,"channels":2,"isDefault":true,"isForced":false,"isSupported":true,"isSelected":true},{"key":"f78","mediaTrackTypeId":3,"language":"de","title":null,"channels":null,"isDefault":false,"isForced":false,"isSupported":true,"isSelected":true}]""")
                "/api/v1/playback/assets/9002/subtitles/f78" -> {
                    subtitleHeaders.set(exchange.requestHeaders.getFirst("Authorization"))
                    exchange.reply(200, "WEBVTT\n\n00:00:02.000 --> 00:00:04.000\nHallo", "text/vtt; charset=utf-8")
                }
                else -> exchange.reply(404, """{"code":"missing_resource"}""")
            }
        }
        server.createContext("/api/v1/playback/preferences") { exchange ->
            when (exchange.requestURI.path) {
                "/api/v1/playback/preferences/speed" -> {
                    speedBody.set(exchange.requestBody.bufferedReader().use { it.readText() })
                    exchange.reply(200, """{"success":true}""")
                }
                "/api/v1/playback/preferences" -> {
                    if (exchange.requestMethod == "GET") {
                        exchange.reply(200, """{"audioLanguage":"ja","subtitleLanguage":"de",
                            "subtitleModeTypeId":3,"speed":1.25,"autoplayNext":true,
                            "introSkipModeTypeId":2,"outroSkipModeTypeId":2,"autoplayPreviews":false,
                            "profileLanguage":"de","deviceQuality":1}""")
                    } else {
                        preferenceBody.set(exchange.requestBody.bufferedReader().use { it.readText() })
                        exchange.reply(200, """{"success":true}""")
                    }
                }
            }
        }
        server.start()
        try {
            val tokens = DeviceTokenStore("original")
            val api = HttpTvV1PlaybackExtrasClient("http://127.0.0.1:${server.address.port}", tokens)
            runBlocking {
                assertEquals(1, api.markers(9002).single().mediaMarkerKindTypeId)
                assertEquals("fresh", tokens.current)
                assertNull(api.chapters(9002).single().title)
                val trickplay = api.trickplay(9002)
                assertEquals(3, trickplay.sheetCount)
                assertEquals(1, trickplay.previewAt(200_000).sheetIndex)
                assertArrayEquals(byteArrayOf(0xff.toByte(), 0xd8.toByte(), 0xff.toByte(), 0xd9.toByte()), api.trickplaySheet(9002, 1))
                assertEquals("f78", api.tracks(9002).last().key)
                assertTrue(api.subtitleVtt(9002, "f78").startsWith("WEBVTT\n"))
                val prefs = api.preferences()
                assertEquals(BigDecimal("1.25"), prefs.speed)
                assertTrue(api.savePreferences(prefs.editable().copy(autoplayNext = false)))
                assertTrue(api.saveSpeed(BigDecimal("1.50")))
            }
            assertEquals("Bearer fresh", subtitleHeaders.get())
            val saved = JSONObject(preferenceBody.get())
            assertFalse(saved.has("deviceQuality"))
            assertFalse(saved.has("profileLanguage"))
            assertFalse(saved.getBoolean("autoplayNext"))
            assertEquals("1.50", JSONObject(speedBody.get()).get("speed").toString())
            assertTrue(requests.any { it == "GET /api/v1/playback/assets/9002/markers" })
            assertTrue(requests.any { it == "GET /api/v1/playback/assets/9002/trickplay/1" })
        } finally {
            server.stop(0)
        }
    }

    @Test fun unsupportedAssetAndUnsafeKeysCannotBeReplacedWithFakeData() {
        val server = HttpServer.create(InetSocketAddress("127.0.0.1", 0), 0)
        server.createContext("/api/v1/playback/assets/9002/trickplay") {
            it.reply(404, """{"code":"not_found","resourceKey":"trickplay_unavailable"}""")
        }
        server.start()
        try {
            val api = HttpTvV1PlaybackExtrasClient("http://127.0.0.1:${server.address.port}", DeviceTokenStore("token"))
            val missing = assertThrows(AppApiException::class.java) { runBlocking { api.trickplay(9002) } }
            assertEquals(404, missing.statusCode)
            assertThrows(IllegalArgumentException::class.java) { runBlocking { api.markers(0) } }
            assertThrows(IllegalArgumentException::class.java) { runBlocking { api.subtitleVtt(9002, "../etc/passwd") } }
            assertThrows(IllegalArgumentException::class.java) { runBlocking { api.trickplaySheet(9002, -1) } }
            assertThrows(IllegalArgumentException::class.java) { runBlocking { api.saveSpeed(BigDecimal("5")) } }
        } finally {
            server.stop(0)
        }
    }

    @Test fun redirectDoesNotForwardTheBearerToExternalHost() {
        val target = HttpServer.create(InetSocketAddress("127.0.0.1", 0), 0)
        val leaked = AtomicReference<String>()
        target.createContext("/") { exchange ->
            leaked.set(exchange.requestHeaders.getFirst("Authorization"))
            exchange.reply(200, "[]")
        }
        target.start()
        val server = HttpServer.create(InetSocketAddress("127.0.0.1", 0), 0)
        server.createContext("/api/v1/playback/assets/9002/markers") {
            it.responseHeaders.add("Location", "http://127.0.0.1:${target.address.port}/")
            it.sendResponseHeaders(302, -1)
            it.close()
        }
        server.start()
        try {
            val api = HttpTvV1PlaybackExtrasClient("http://127.0.0.1:${server.address.port}", DeviceTokenStore("secret"))
            assertThrows(AppApiException::class.java) { runBlocking { api.markers(9002) } }
            assertNull(leaked.get())
        } finally {
            server.stop(0)
            target.stop(0)
        }
    }

    private fun HttpExchange.reply(status: Int, text: String, type: String = "application/json", token: String? = null) {
        replyBytes(status, text.toByteArray(Charsets.UTF_8), type, token)
    }
    private fun HttpExchange.replyBytes(status: Int, data: ByteArray, type: String, token: String? = null) {
        responseHeaders.add("Content-Type", type)
        if (token != null) responseHeaders.add(AppApiRoutes.DeviceTokenHeader, token)
        sendResponseHeaders(status, data.size.toLong())
        responseBody.use { it.write(data) }
    }
}
