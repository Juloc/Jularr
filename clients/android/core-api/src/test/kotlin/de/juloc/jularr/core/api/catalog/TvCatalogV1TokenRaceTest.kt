package de.juloc.jularr.core.api.catalog

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
import java.util.concurrent.atomic.AtomicBoolean
import java.util.concurrent.atomic.AtomicReference

/** A late response must not undo an overlapping successful bearer-token rotation. */
class TvCatalogV1TokenRaceTest {
    @Test fun lateUnauthorizedResponseDoesNotRevokeNewToken() = checkLateResponse(401)

    @Test fun lateSuccessfulResponseDoesNotRollBackNewToken() = checkLateResponse(200)

    private fun checkLateResponse(delayedStatus: Int) {
        val server = HttpServer.create(InetSocketAddress("127.0.0.1", 0), 0)
        val executor = Executors.newCachedThreadPool()
        server.executor = executor
        val firstStarted = CountDownLatch(1)
        val finishFirst = CountDownLatch(1)
        val sawUnauthorized = AtomicBoolean(false)
        val unexpected = AtomicReference<Throwable?>()

        server.createContext("/api/v1/continue") { exchange ->
            firstStarted.countDown()
            if (!finishFirst.await(5, TimeUnit.SECONDS)) {
                exchange.respond(500, """{"code":"timeout"}""")
            } else if (delayedStatus == 401) {
                exchange.respond(401, """{"code":"invalid_token","resourceKey":"invalid_token"}""")
            } else {
                exchange.respond(200, "[]", "stale-token")
            }
        }
        server.createContext("/api/v1/home/layout") {
            it.respond(200, "[]", "fresh-token")
        }
        server.start()
        try {
            val tokens = DeviceTokenStore("old-token")
            val api = HttpTvCatalogV1Client("http://127.0.0.1:${server.address.port}", tokens)
            val delayed = Thread {
                try {
                    runBlocking { api.continueWatching() }
                } catch (failure: AppApiException) {
                    if (failure.statusCode == 401) sawUnauthorized.set(true) else unexpected.set(failure)
                } catch (failure: Throwable) {
                    unexpected.set(failure)
                }
            }
            delayed.start()
            assertTrue("Delayed request did not start", firstStarted.await(5, TimeUnit.SECONDS))
            runBlocking { assertTrue(api.homeLayout().isEmpty()) }
            assertEquals("fresh-token", tokens.current)
            finishFirst.countDown()
            delayed.join(5_000)
            assertFalse("Delayed request still blocked", delayed.isAlive)
            assertNull(unexpected.get())
            assertEquals(delayedStatus == 401, sawUnauthorized.get())
            assertEquals("fresh-token", tokens.current)
        } finally {
            finishFirst.countDown()
            server.stop(0)
            executor.shutdownNow()
        }
    }

    private fun HttpExchange.respond(status: Int, json: String, token: String? = null) {
        val bytes = json.toByteArray(Charsets.UTF_8)
        responseHeaders.add("Content-Type", "application/json")
        if (token != null) responseHeaders.add(AppApiRoutes.DeviceTokenHeader, token)
        sendResponseHeaders(status, bytes.size.toLong())
        responseBody.use { it.write(bytes) }
    }
}
