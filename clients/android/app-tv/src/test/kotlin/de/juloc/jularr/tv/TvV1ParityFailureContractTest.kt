package de.juloc.jularr.tv

import com.sun.net.httpserver.HttpExchange
import com.sun.net.httpserver.HttpServer
import de.juloc.jularr.core.api.AppApiException
import de.juloc.jularr.core.api.DeviceTokenStore
import de.juloc.jularr.core.api.playback.HttpTvPlaybackV1Client
import de.juloc.jularr.core.api.playback.TvPlaybackV1Capabilities
import de.juloc.jularr.core.api.playbackextras.HttpTvV1PlaybackExtrasClient
import de.juloc.jularr.core.api.playbackextras.TvV1PlaybackMarker
import de.juloc.jularr.core.api.playbackextras.TvV1PlaybackPreferences
import de.juloc.jularr.core.api.tvlegacy.HttpTvV1LegacyFeaturesClient
import kotlinx.coroutines.runBlocking
import org.junit.Assert.*
import org.junit.Test
import java.io.IOException
import java.math.BigDecimal
import java.net.InetSocketAddress
import java.net.ServerSocket
import java.util.concurrent.atomic.AtomicInteger

/**
 * Missing NAS, wake-in-progress, permission errors and network loss have no fake
 * playback URLs or synthetic chapter/marker data. UI error display/retry needs device QA.
 */
class TvV1ParityFailureContractTest {
    private val capabilities = TvPlaybackV1Capabilities(listOf("mp4"), listOf("h264"), listOf("aac"))
    private val sessionId = "a26642ae-dd19-4adb-a245-d1988d2cf2db"

    @Test fun storageWakingThenUnavailableThenServerReadyRequiresExplicitPlaybackRetry() = runBlocking {
        val server = HttpServer.create(InetSocketAddress("127.0.0.1", 0), 0)
        val attempts = AtomicInteger()
        server.createContext("/api/v1/playback/assets/9002/sessions") { exchange ->
            when (attempts.incrementAndGet()) {
                1 -> exchange.reply(503, """{"code":"storage_waking","resourceKey":"error.storage_waking"}""")
                2 -> exchange.reply(503, """{"code":"storage_unavailable","resourceKey":"error.storage_unavailable"}""")
                else -> exchange.reply(200, """{"playbackSessionId":"$sessionId","playbackModeTypeId":1,
                    "streamUrl":"/api/v1/playback/sessions/$sessionId/stream",
                    "resumePositionMs":1000,"durationMs":100000,"audioStreamIndex":null,
                    "quality":1,"isReduced":false}""")
            }
        }
        server.start()
        try {
            val shared = DeviceTokenStore("authorized")
            val client = HttpTvPlaybackV1Client("http://127.0.0.1:${server.address.port}", shared)
            val waking = assertThrows(AppApiException::class.java) {
                runBlocking { client.start(9002, capabilities) }
            }
            assertEquals("storage_waking", waking.code)
            assertEquals("authorized", shared.current)
            val unavailable = assertThrows(AppApiException::class.java) {
                runBlocking { client.start(9002, capabilities) }
            }
            assertEquals("storage_unavailable", unavailable.code)
            assertEquals("authorized", shared.current)
            // No hidden API call/retry while a NAS wakes: a caller must explicitly try again.
            assertEquals(2, attempts.get())
            val plan = client.start(9002, capabilities)
            assertEquals(sessionId, plan.playbackSessionId)
            assertEquals(3, attempts.get())
        } finally {
            server.stop(0)
        }
    }

    @Test fun missingTrickplayMarkersAndNeighbourAreFailuresNotFabricatedMedia() = runBlocking {
        val server = HttpServer.create(InetSocketAddress("127.0.0.1", 0), 0)
        server.createContext("/api/v1/playback/assets/9002") { exchange ->
            exchange.reply(404, """{"code":"not_found","resourceKey":"error.not_found"}""")
        }
        server.start()
        try {
            val tokens = DeviceTokenStore("authorized")
            val origin = "http://127.0.0.1:${server.address.port}"
            val extras = HttpTvV1PlaybackExtrasClient(origin, tokens)
            val identity = HttpTvV1LegacyFeaturesClient(origin, tokens)
            assertEquals(404, assertThrows(AppApiException::class.java) {
                runBlocking { extras.trickplay(9002) }
            }.statusCode)
            assertEquals(404, assertThrows(AppApiException::class.java) {
                runBlocking { extras.markers(9002) }
            }.statusCode)
            assertEquals(404, assertThrows(AppApiException::class.java) {
                runBlocking { extras.chapters(9002) }
            }.statusCode)
            assertEquals(404, assertThrows(AppApiException::class.java) {
                runBlocking { identity.playbackAsset(9002) }
            }.statusCode)
            assertEquals("authorized", tokens.current)
        } finally {
            server.stop(0)
        }
    }

    @Test fun lostNetworkMustNotRevokeAnOtherwiseValidDeviceToken() {
        val port = ServerSocket(0).use { it.localPort }
        val tokens = DeviceTokenStore("offline-but-paired")
        val client = HttpTvPlaybackV1Client("http://127.0.0.1:$port", tokens)
        assertThrows(IOException::class.java) {
            runBlocking { client.start(9002, capabilities) }
        }
        assertEquals("offline-but-paired", tokens.current)
    }

    @Test fun realSkipModesDifferentiateManualButtonsFromAutomaticSeeking() {
        val markers = listOf(
            TvV1PlaybackMarker(1, 1000, 3000),
            TvV1PlaybackMarker(2, 95000, 100000),
        )
        val prefs = TvV1PlaybackPreferences(
            audioLanguage = "ja", subtitleLanguage = null, subtitleModeTypeId = 1,
            speed = BigDecimal("1.00"), autoplayNext = false,
            introSkipModeTypeId = 2, outroSkipModeTypeId = 3,
            autoplayPreviews = false, profileLanguage = "de", deviceQuality = 1,
        )
        val segments = TvV1PlayerUiMapping.skipSegments(markers, prefs)
        assertTrue(segments[0].canSkip)
        assertTrue(segments[1].canSkip)
        assertNull(TvV1PlayerUiMapping.automaticSkipTarget(2000, markers, prefs))
        assertEquals(100000L, TvV1PlayerUiMapping.automaticSkipTarget(98000, markers, prefs))
        assertNull(TvV1PlayerUiMapping.automaticSkipTarget(100000, markers, prefs))
        assertFalse(TvV1PlayerUiMapping.skipSegments(
            markers, prefs.copy(outroSkipModeTypeId = 1),
        )[1].canSkip)
    }

    private fun HttpExchange.reply(status: Int, json: String) {
        val bytes = json.toByteArray(Charsets.UTF_8)
        responseHeaders.add("Content-Type", "application/json")
        sendResponseHeaders(status, bytes.size.toLong())
        responseBody.use { it.write(bytes) }
    }
}
