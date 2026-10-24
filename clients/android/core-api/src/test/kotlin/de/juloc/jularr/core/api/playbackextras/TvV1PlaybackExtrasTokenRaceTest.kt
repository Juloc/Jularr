package de.juloc.jularr.core.api.playbackextras

import com.sun.net.httpserver.HttpExchange
import com.sun.net.httpserver.HttpServer
import de.juloc.jularr.core.api.AppApiException
import de.juloc.jularr.core.api.AppApiRoutes
import de.juloc.jularr.core.api.DeviceTokenStore
import kotlinx.coroutines.runBlocking
import org.junit.Assert.*
import org.junit.Test
import java.net.InetSocketAddress
import java.util.concurrent.CountDownLatch
import java.util.concurrent.Executors
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicReference

/** The shared device token cannot be corrupted by an earlier playback-metadata response. */
class TvV1PlaybackExtrasTokenRaceTest {
    @Test fun late401RetrievesMetadataWithNewBearerInsteadOfSigningOut() = checkLate(401)

    @Test fun staleSuccessCannotOverwriteNewBearer() = checkLate(200)

    private fun checkLate(firstStatus: Int) {
        val server = HttpServer.create(InetSocketAddress("127.0.0.1", 0), 0)
        val executor = Executors.newCachedThreadPool()
        server.executor = executor
        val firstEntered = CountDownLatch(1)
        val unblock = CountDownLatch(1)
        val attempts = mutableListOf<String?>()
        val error = AtomicReference<Throwable?>()
        val result = AtomicReference<List<TvV1PlaybackMarker>>()

        server.createContext("/api/v1/playback/assets/42/markers") { exchange ->
            val supplied = exchange.requestHeaders.getFirst("Authorization")
            synchronized(attempts) { attempts.add(supplied) }
            if (supplied == "Bearer old") {
                firstEntered.countDown()
                if (!unblock.await(5, TimeUnit.SECONDS)) {
                    exchange.reply(500, """{"code":"timed_out"}""")
                } else if (firstStatus == 401) {
                    exchange.reply(401, """{"code":"invalid_token"}""")
                } else {
                    exchange.reply(200, "[]", "stale")
                }
            } else {
                exchange.reply(200, """[{"mediaMarkerKindTypeId":1,"startMs":100,"endMs":200}]""")
            }
        }
        server.createContext("/api/v1/playback/preferences") {
            it.reply(200, preferencesJson(), "fresh")
        }
        server.start()
        try {
            val tokens = DeviceTokenStore("old")
            val api = HttpTvV1PlaybackExtrasClient("http://127.0.0.1:${server.address.port}", tokens)
            val pending = Thread {
                try {
                    result.set(runBlocking { api.markers(42) })
                } catch (e: Throwable) {
                    error.set(e)
                }
            }
            pending.start()
            assertTrue(firstEntered.await(5, TimeUnit.SECONDS))
            runBlocking { assertEquals("de", api.preferences().profileLanguage) }
            assertEquals("fresh", tokens.current)
            unblock.countDown()
            pending.join(5000)
            assertFalse(pending.isAlive)
            assertNull(error.get())
            assertEquals("fresh", tokens.current)
            if (firstStatus == 401) {
                assertEquals(1, result.get().size)
                assertEquals(listOf("Bearer old", "Bearer fresh"), synchronized(attempts) { attempts.toList() })
            } else {
                assertTrue(result.get().isEmpty())
                assertEquals(listOf("Bearer old"), synchronized(attempts) { attempts.toList() })
            }
        } finally {
            unblock.countDown()
            server.stop(0)
            executor.shutdownNow()
        }
    }

    private fun preferencesJson() =
        """{"audioLanguage":null,"subtitleLanguage":null,"subtitleModeTypeId":4,"speed":1,
           "autoplayNext":true,"introSkipModeTypeId":2,"outroSkipModeTypeId":2,
           "autoplayPreviews":true,"profileLanguage":"de","deviceQuality":1}"""

    private fun HttpExchange.reply(status: Int, json: String, token: String? = null) {
        val bytes = json.toByteArray(Charsets.UTF_8)
        responseHeaders.add("Content-Type", "application/json")
        if (token != null) responseHeaders.add(AppApiRoutes.DeviceTokenHeader, token)
        sendResponseHeaders(status, bytes.size.toLong())
        responseBody.use { it.write(bytes) }
    }
}
