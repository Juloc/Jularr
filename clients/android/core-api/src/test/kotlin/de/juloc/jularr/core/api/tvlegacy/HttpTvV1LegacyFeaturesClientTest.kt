package de.juloc.jularr.core.api.tvlegacy

import com.sun.net.httpserver.HttpExchange
import com.sun.net.httpserver.HttpServer
import de.juloc.jularr.core.api.AppApiException
import de.juloc.jularr.core.api.AppApiRoutes
import de.juloc.jularr.core.api.DeviceTokenStore
import kotlinx.coroutines.runBlocking
import org.junit.Assert.*
import org.junit.Test
import java.net.InetSocketAddress
import java.util.concurrent.atomic.AtomicReference

class HttpTvV1LegacyFeaturesClientTest {
    @Test fun readsActualServerRoutesAndUsesSharedRotatingDeviceToken() {
        val server = HttpServer.create(InetSocketAddress("127.0.0.1", 0), 0)
        val statsQuery = AtomicReference<String>()
        val statsAuthorization = AtomicReference<String>()
        val assetAuthorization = AtomicReference<String>()
        server.createContext("/api/v1/statistics") { exchange ->
            statsQuery.set(exchange.requestURI.rawQuery)
            statsAuthorization.set(exchange.requestHeaders.getFirst("Authorization"))
            exchange.reply(200, """{"watchedMs":560000,"sessions":2,"gamePlayedMs":0,
                "completed":[{"mediaTypeId":2,"count":1}],
                "topWorks":[{"workId":46,"title":"Work","durationMs":50000}]}""", "next-token")
        }
        server.createContext("/api/v1/playback/assets/9002") { exchange ->
            assetAuthorization.set(exchange.requestHeaders.getFirst("Authorization"))
            exchange.reply(200, """{"mediaAssetId":9002,"workId":46,"canonicalTitle":"Work",
                "mediaAssetTypeId":1,"workUnitTypeId":2,"unitNumber":3,
                "parentUnitNumber":1,"unitTitle":null,"resumePositionMs":23000,
                "previousMediaAssetId":9001,"nextMediaAssetId":null}""")
        }
        server.start()
        try {
            val tokens = DeviceTokenStore("paired-token")
            val client = HttpTvV1LegacyFeaturesClient("http://127.0.0.1:${server.address.port}", tokens)
            runBlocking {
                assertEquals(2L, client.statistics(30).sessions)
                assertEquals(9002L, client.playbackAsset(9002).mediaAssetId)
                assertEquals(9001L, client.playbackAsset(9002).previousMediaAssetId)
            }
            assertEquals("days=30", statsQuery.get())
            assertEquals("Bearer paired-token", statsAuthorization.get())
            assertEquals("Bearer next-token", assetAuthorization.get())
            assertEquals("next-token", tokens.current)
        } finally {
            server.stop(0)
        }
    }

    @Test fun missingRowsStayUnavailableAndAnInvalidAssetIsNotRequested() {
        val server = HttpServer.create(InetSocketAddress("127.0.0.1", 0), 0)
        server.createContext("/api/v1/playback/assets/55") {
            it.reply(404, """{"code":"not_found","resourceKey":"not_found"}""")
        }
        server.start()
        try {
            val client = HttpTvV1LegacyFeaturesClient("http://127.0.0.1:${server.address.port}", DeviceTokenStore("paired"))
            val ex = assertThrows(AppApiException::class.java) {
                runBlocking { client.playbackAsset(55) }
            }
            assertEquals(404, ex.statusCode)
            assertThrows(IllegalArgumentException::class.java) {
                runBlocking { client.playbackAsset(0) }
            }
        } finally {
            server.stop(0)
        }
    }

    @Test fun redirectCannotSendDeviceBearerToAnotherOrigin() {
        val attacker = HttpServer.create(InetSocketAddress("127.0.0.1", 0), 0)
        val stolen = AtomicReference<String>()
        attacker.createContext("/") { exchange ->
            stolen.set(exchange.requestHeaders.getFirst("Authorization"))
            exchange.reply(200, """{}""")
        }
        attacker.start()
        val server = HttpServer.create(InetSocketAddress("127.0.0.1", 0), 0)
        server.createContext("/api/v1/statistics") {
            it.responseHeaders.add("Location", "http://127.0.0.1:${attacker.address.port}/")
            it.sendResponseHeaders(302, -1)
            it.close()
        }
        server.start()
        try {
            val client = HttpTvV1LegacyFeaturesClient("http://127.0.0.1:${server.address.port}", DeviceTokenStore("secret"))
            assertThrows(AppApiException::class.java) { runBlocking { client.statistics(0) } }
            assertNull(stolen.get())
        } finally {
            server.stop(0)
            attacker.stop(0)
        }
    }

    private fun HttpExchange.reply(status: Int, json: String, token: String? = null) {
        val bytes = json.toByteArray(Charsets.UTF_8)
        responseHeaders.add("Content-Type", "application/json")
        if (token != null) responseHeaders.add(AppApiRoutes.DeviceTokenHeader, token)
        sendResponseHeaders(status, bytes.size.toLong())
        responseBody.use { it.write(bytes) }
    }
}
