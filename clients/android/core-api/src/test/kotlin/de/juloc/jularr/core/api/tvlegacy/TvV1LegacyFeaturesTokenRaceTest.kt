package de.juloc.jularr.core.api.tvlegacy

import com.sun.net.httpserver.HttpExchange
import com.sun.net.httpserver.HttpServer
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

class TvV1LegacyFeaturesTokenRaceTest {
    @Test fun staleUnauthorizedRequestRetriesAgainstNewToken() = verifyConcurrentResponse(401)

    @Test fun lateSuccessDoesNotUndoTokenRotation() = verifyConcurrentResponse(200)

    private fun verifyConcurrentResponse(firstStatus: Int) {
        val server = HttpServer.create(InetSocketAddress("127.0.0.1", 0), 0)
        val pool = Executors.newCachedThreadPool()
        server.executor = pool
        val requestStarted = CountDownLatch(1)
        val releaseOldRequest = CountDownLatch(1)
        val attempts = mutableListOf<String?>()
        val failure = AtomicReference<Throwable?>()
        val result = AtomicReference<TvV1PlaybackAssetInfo?>()
        server.createContext("/api/v1/playback/assets/777") { exchange ->
            val authorization = exchange.requestHeaders.getFirst("Authorization")
            synchronized(attempts) { attempts.add(authorization) }
            if (authorization == "Bearer old") {
                requestStarted.countDown()
                check(releaseOldRequest.await(8, TimeUnit.SECONDS))
                if (firstStatus == 401) {
                    exchange.reply(401, """{"code":"invalid_token"}""")
                } else {
                    exchange.reply(200, assetJson(), "stale")
                }
            } else {
                exchange.reply(200, assetJson())
            }
        }
        server.createContext("/api/v1/statistics") {
            it.reply(200, """{"watchedMs":0,"sessions":0,"gamePlayedMs":0,"completed":[],"topWorks":[]}""", "fresh")
        }
        server.start()
        try {
            val tokens = DeviceTokenStore("old")
            val api = HttpTvV1LegacyFeaturesClient("http://127.0.0.1:${server.address.port}", tokens)
            val thread = Thread {
                try {
                    result.set(runBlocking { api.playbackAsset(777) })
                } catch (e: Throwable) {
                    failure.set(e)
                }
            }
            thread.start()
            assertTrue(requestStarted.await(8, TimeUnit.SECONDS))
            assertEquals(0L, runBlocking { api.statistics(0).sessions })
            assertEquals("fresh", tokens.current)
            releaseOldRequest.countDown()
            thread.join(8000)
            assertFalse(thread.isAlive)
            assertNull(failure.get())
            assertEquals(777L, result.get()?.mediaAssetId)
            assertEquals("fresh", tokens.current)
            assertEquals(
                if (firstStatus == 401) listOf("Bearer old", "Bearer fresh") else listOf("Bearer old"),
                synchronized(attempts) { attempts.toList() },
            )
        } finally {
            releaseOldRequest.countDown()
            server.stop(0)
            pool.shutdownNow()
        }
    }

    private fun assetJson() =
        """{"mediaAssetId":777,"workId":42,"canonicalTitle":"Work","mediaAssetTypeId":1,
          "workUnitTypeId":null,"unitNumber":null,"parentUnitNumber":null,"unitTitle":null,
          "resumePositionMs":null,"previousMediaAssetId":null,"nextMediaAssetId":null}"""

    private fun HttpExchange.reply(status: Int, json: String, deviceToken: String? = null) {
        val bytes = json.toByteArray(Charsets.UTF_8)
        responseHeaders.add("Content-Type", "application/json")
        if (deviceToken != null) responseHeaders.add(AppApiRoutes.DeviceTokenHeader, deviceToken)
        sendResponseHeaders(status, bytes.size.toLong())
        responseBody.use { it.write(bytes) }
    }
}
