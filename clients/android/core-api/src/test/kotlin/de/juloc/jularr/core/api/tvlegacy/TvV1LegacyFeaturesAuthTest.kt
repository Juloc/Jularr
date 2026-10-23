package de.juloc.jularr.core.api.tvlegacy

import com.sun.net.httpserver.HttpExchange
import com.sun.net.httpserver.HttpServer
import de.juloc.jularr.core.api.AppApiException
import de.juloc.jularr.core.api.DeviceTokenStore
import kotlinx.coroutines.runBlocking
import org.junit.Assert.*
import org.junit.Test
import java.net.InetSocketAddress

class TvV1LegacyFeaturesAuthTest {
    @Test fun currentTokenRevocationClearsSharedPairedSessionWithoutRetriableFallback() {
        val server = HttpServer.create(InetSocketAddress("127.0.0.1", 0), 0)
        server.createContext("/api/v1/statistics") { exchange ->
            exchange.respond(401, """{"code":"invalid_token","resourceKey":"error.invalid_token"}""")
        }
        server.start()
        try {
            val changes = mutableListOf<String?>()
            val shared = DeviceTokenStore("revoked") { changes.add(it) }
            val api = HttpTvV1LegacyFeaturesClient("http://127.0.0.1:${server.address.port}", shared)
            val error = assertThrows(AppApiException::class.java) {
                runBlocking { api.statistics(30) }
            }
            assertEquals(401, error.statusCode)
            assertNull(shared.current)
            assertEquals(listOf<String?>(null), changes)
            assertThrows(IllegalStateException::class.java) { runBlocking { api.playbackAsset(55) } }
        } finally {
            server.stop(0)
        }
    }

    @Test fun refusesUnpairedRequestAndNonOriginUrlsWithoutNetwork() {
        val api = HttpTvV1LegacyFeaturesClient("https://jularr.example", DeviceTokenStore())
        assertThrows(IllegalStateException::class.java) { runBlocking { api.statistics() } }
        for (address in listOf(
            "https://other.example/path",
            "https://name:secret@other.example/",
            "https://other.example/?token=abc",
            "file:///tmp/untrusted",
        )) {
            assertThrows(IllegalArgumentException::class.java) {
                HttpTvV1LegacyFeaturesClient(address, DeviceTokenStore("token"))
            }
        }
    }

    private fun HttpExchange.respond(status: Int, text: String) {
        val bytes = text.toByteArray(Charsets.UTF_8)
        responseHeaders.add("Content-Type", "application/json")
        sendResponseHeaders(status, bytes.size.toLong())
        responseBody.use { it.write(bytes) }
    }
}
