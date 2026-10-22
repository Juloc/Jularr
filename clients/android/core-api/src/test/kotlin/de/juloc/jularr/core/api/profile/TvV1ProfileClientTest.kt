package de.juloc.jularr.core.api.profile

import com.sun.net.httpserver.HttpExchange
import com.sun.net.httpserver.HttpServer
import de.juloc.jularr.core.api.AppApiRoutes
import de.juloc.jularr.core.api.DeviceTokenStore
import kotlinx.coroutines.runBlocking
import org.junit.Assert.*
import org.junit.Test
import java.net.InetSocketAddress
import java.util.concurrent.atomic.AtomicReference

class TvV1ProfileClientTest {
    @Test fun profilePinSwitchRotatesBearerAndRestoresSession() = runBlocking {
        val requestAuth = AtomicReference<String>()
        val switchedAuth = AtomicReference<String>()
        val server = HttpServer.create(InetSocketAddress("127.0.0.1", 0), 0)
        server.createContext("/api/v1/profiles") { e ->
            requestAuth.set(e.requestHeaders.getFirst("Authorization"))
            e.respond(200, """{"items":[{"id":7,"displayName":"Owner","isLocked":false,
                "pinLength":null,"isOwned":true},{"id":9,"displayName":"Guest",
                "isLocked":true,"pinLength":4,"isOwned":false}],"page":1,"pageSize":100}""")
        }
        server.createContext("/api/v1/auth/profile/9/pin") { e ->
            assertEquals("POST", e.requestMethod)
            assertEquals("1234", org.json.JSONObject(e.requestBody.bufferedReader().readText()).getString("pin"))
            e.respond(200, """{"profileId":9}""", "new-profile-token")
        }
        server.createContext("/api/v1/sessions") { e ->
            switchedAuth.set(e.requestHeaders.getFirst("Authorization"))
            e.respond(200, """{"items":[{"id":"d034e410-84e1-467f-8858-389bef99b0e2",
                "createdAt":"2026-10-22T10:00:00Z","expiresAt":"2027-10-22T10:00:00Z",
                "isCurrent":true,"deviceName":"Living Room","devicePlatform":"AndroidTV",
                "playbackQuality":1}],"page":1,"pageSize":100}""")
        }
        server.start()
        try {
            val tokens = DeviceTokenStore("paired-token")
            val client = HttpTvV1ProfileClient("http://127.0.0.1:${server.address.port}", tokens)
            val profiles = client.profiles()
            assertEquals(2, profiles.size)
            assertTrue(profiles.last().isLocked)
            assertEquals(4, profiles.last().pinLength)
            assertEquals(9L, client.selectProfile(9, "1234"))
            assertEquals("new-profile-token", tokens.current)
            assertEquals("Living Room", client.sessions().single().deviceName)
            assertEquals("Bearer paired-token", requestAuth.get())
            assertEquals("Bearer new-profile-token", switchedAuth.get())
        } finally { server.stop(0) }
    }

    @Test fun signOutRevokesServerSessionBeforeErasingLocalToken() = runBlocking {
        val server = HttpServer.create(InetSocketAddress("127.0.0.1", 0), 0)
        server.createContext("/api/v1/auth/session") { e ->
            assertEquals("DELETE", e.requestMethod)
            e.respond(200, """{"isAuthenticated":false,"endsCurrentSession":true}""")
        }
        server.start()
        try {
            val tokens = DeviceTokenStore("paired-token")
            HttpTvV1ProfileClient("http://127.0.0.1:${server.address.port}", tokens).signOut()
            assertNull(tokens.current)
        } finally { server.stop(0) }
    }

    @Test fun endingAnotherDeviceSessionKeepsCurrentDeviceBearer() = runBlocking {
        val sessionId = java.util.UUID.fromString("f2e35c1e-e132-4d31-a2c9-a40e84bd68a1")
        val observedAuth = AtomicReference<String>()
        val server = HttpServer.create(InetSocketAddress("127.0.0.1", 0), 0)
        server.createContext("/api/v1/sessions/$sessionId") { exchange ->
            assertEquals("DELETE", exchange.requestMethod)
            observedAuth.set(exchange.requestHeaders.getFirst("Authorization"))
            exchange.respond(200, """{"endsCurrentSession":false}""")
        }
        server.start()
        try {
            val tokens = DeviceTokenStore("current-tv-token")
            val api = HttpTvV1ProfileClient("http://127.0.0.1:${server.address.port}", tokens)
            assertFalse(api.revokeSession(sessionId))
            assertEquals("Bearer current-tv-token", observedAuth.get())
            assertEquals("current-tv-token", tokens.current)
        } finally {
            server.stop(0)
        }
    }

    @Test fun serverRevokingCurrentSessionClearsLocalBearerImmediately() = runBlocking {
        val sessionId = java.util.UUID.fromString("f2e35c1e-e132-4d31-a2c9-a40e84bd68a1")
        val server = HttpServer.create(InetSocketAddress("127.0.0.1", 0), 0)
        server.createContext("/api/v1/sessions/$sessionId") { exchange ->
            assertEquals("DELETE", exchange.requestMethod)
            exchange.respond(200, """{"endsCurrentSession":true}""")
        }
        server.start()
        try {
            val tokens = DeviceTokenStore("current-tv-token")
            val api = HttpTvV1ProfileClient("http://127.0.0.1:${server.address.port}", tokens)
            assertTrue(api.revokeSession(sessionId))
            assertNull(tokens.current)
        } finally {
            server.stop(0)
        }
    }

    @Test fun successfulOldProfileResponseMustNotLeakIntoNewViewer() = runBlocking {
        val tokens = DeviceTokenStore("old-profile")
        val seen = mutableListOf<String?>()
        val server = HttpServer.create(InetSocketAddress("127.0.0.1", 0), 0)
        server.createContext("/api/v1/profiles") { exchange ->
            val bearer = exchange.requestHeaders.getFirst("Authorization")
            synchronized(seen) { seen.add(bearer) }
            if (bearer == "Bearer old-profile") {
                // Old profile was valid when the call began, but another screen
                // rotated the device token before the response arrived.
                tokens.update("new-profile")
                exchange.respond(200, """{"items":[{"id":12,"displayName":"PRIVATE OLD PROFILE",
                    "isLocked":false,"pinLength":null,"isOwned":true}],
                    "page":1,"pageSize":100}""")
            } else {
                exchange.respond(200, """{"items":[{"id":24,"displayName":"New viewer",
                    "isLocked":false,"pinLength":null,"isOwned":true}],
                    "page":1,"pageSize":100}""")
            }
        }
        server.start()
        try {
            val client = HttpTvV1ProfileClient("http://127.0.0.1:${server.address.port}", tokens)
            assertEquals(listOf("New viewer"), client.profiles().map { it.displayName })
            assertEquals(listOf("Bearer old-profile", "Bearer new-profile"), seen)
            assertEquals("new-profile", tokens.current)
        } finally {
            server.stop(0)
        }
    }

    @Test fun staleSessionList401RetriesWithNewProfileCredential() = runBlocking {
        val tokens = DeviceTokenStore("previous-profile")
        val observed = mutableListOf<String?>()
        val server = HttpServer.create(InetSocketAddress("127.0.0.1", 0), 0)
        server.createContext("/api/v1/sessions") { exchange ->
            val bearer = exchange.requestHeaders.getFirst("Authorization")
            synchronized(observed) { observed.add(bearer) }
            if (bearer == "Bearer previous-profile") {
                tokens.update("new-profile")
                exchange.respond(401, """{"code":"invalid_token","resourceKey":"error.invalid_token"}""")
            } else {
                exchange.respond(200, """{"items":[],"page":1,"pageSize":100}""")
            }
        }
        server.start()
        try {
            val client = HttpTvV1ProfileClient("http://127.0.0.1:${server.address.port}", tokens)
            assertTrue(client.sessions().isEmpty())
            assertEquals(listOf("Bearer previous-profile", "Bearer new-profile"), observed)
            assertEquals("new-profile", tokens.current)
        } finally {
            server.stop(0)
        }
    }

    private fun HttpExchange.respond(status: Int, body: String, token: String? = null) {
        val bytes = body.toByteArray()
        responseHeaders.add("Content-Type", "application/json")
        if (token != null) responseHeaders.add(AppApiRoutes.DeviceTokenHeader, token)
        sendResponseHeaders(status, bytes.size.toLong())
        responseBody.use { it.write(bytes) }
    }
}
