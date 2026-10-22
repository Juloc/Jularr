package de.juloc.jularr.core.api

import com.sun.net.httpserver.HttpExchange
import com.sun.net.httpserver.HttpServer
import de.juloc.jularr.core.model.DevicePairingRequest
import de.juloc.jularr.core.model.DevicePlatform
import kotlinx.coroutines.runBlocking
import org.json.JSONObject
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test
import java.net.InetSocketAddress
import java.util.concurrent.atomic.AtomicReference

class HttpAppApiClientContractTest {
    @Test
    fun pairsWithTheV1EndpointsThenSendsAndRotatesTheBearerToken() {
        val pairingBody = AtomicReference<String>()
        val claimCookie = AtomicReference<String?>()
        val authorization = AtomicReference<String?>()
        val capabilitiesCookie = AtomicReference<String?>()
        val server = localServer()
        server.createContext("/api/v1/devices/pairings") { exchange ->
            if (exchange.requestURI.path.endsWith("/claim")) {
                claimCookie.set(exchange.requestHeaders.getFirst("Cookie"))
                exchange.respond(200, """{"isPaired":true}""", "first-token")
            } else {
                pairingBody.set(exchange.requestBody.bufferedReader().use { it.readText() })
                exchange.respond(
                    200,
                    """{"pairingId":"e1427e32-606a-4c93-95e4-23d9632d8710","code":"123456","expiresAt":"2026-10-22T10:00:00Z"}""",
                )
            }
        }
        server.createContext("/api/v1/instance/capabilities") { exchange ->
            authorization.set(exchange.requestHeaders.getFirst("Authorization"))
            capabilitiesCookie.set(exchange.requestHeaders.getFirst("Cookie"))
            exchange.respond(200, """{"permissions":["profile.playback"],"modules":["Devices","Playback"]}""", "rotated-token")
        }
        server.start()
        try {
            val persisted = mutableListOf<String?>()
            val tokens = DeviceTokenStore(onChanged = { persisted.add(it) })
            val client = HttpAppApiClient("http://127.0.0.1:" + server.address.port, tokens)
            runBlocking {
                val pairing = client.startPairing(DevicePairingRequest("Jularr TV", DevicePlatform.AndroidTv))
                assertEquals("123456", pairing.code)
                val claim = client.claimPairing(pairing.pairingId)
                assertTrue(claim.isPaired)
                assertEquals("first-token", claim.deviceToken)
                assertTrue(client.capabilities().hasModule("Playback"))
            }
            assertEquals("AndroidTV", JSONObject(pairingBody.get()).getString("devicePlatform"))
            assertEquals("Bearer first-token", authorization.get())
            assertNull(claimCookie.get())
            assertNull(capabilitiesCookie.get())
            assertEquals("rotated-token", tokens.current)
            assertEquals(listOf("first-token", "rotated-token"), persisted)
        } finally {
            server.stop(0)
        }
    }

    @Test
    fun revokedBearerTokenIsDroppedOn401() {
        val server = localServer()
        server.createContext("/api/v1/instance/capabilities") { exchange ->
            exchange.respond(401, """{"code":"invalid_token","resourceKey":"error.invalid_token"}""")
        }
        server.start()
        try {
            val tokens = DeviceTokenStore(initialToken = "expired-token")
            val client = HttpAppApiClient("http://127.0.0.1:" + server.address.port, tokens)
            var rejected = false
            try {
                runBlocking { client.capabilities() }
            } catch (e: AppApiException) {
                assertEquals(401, e.statusCode)
                rejected = true
            }
            assertTrue(rejected)
            assertNull(tokens.current)
        } finally {
            server.stop(0)
        }
    }

    @Test
    fun rejectsOriginsContainingPathsOrCredentials() {
        val badPath = runCatching { HttpAppApiClient("https://example.com/untrusted") }
        val badCredentials = runCatching { HttpAppApiClient("https://user:pass@example.com") }
        assertTrue(badPath.isFailure)
        assertTrue(badCredentials.isFailure)
        assertFalse(runCatching { HttpAppApiClient("https://example.com") }.isFailure)
    }

    @Test
    fun concurrentTokenRenewalSurvivesStaleCapability401() {
        val tokens = DeviceTokenStore("old-credential")
        val seen = mutableListOf<String?>()
        val server = localServer()
        server.createContext("/api/v1/instance/capabilities") { exchange ->
            val bearer = exchange.requestHeaders.getFirst("Authorization")
            synchronized(seen) { seen.add(bearer) }
            if (bearer == "Bearer old-credential") {
                tokens.update("new-credential")
                exchange.respond(401, """{"code":"invalid_token","resourceKey":"error.invalid_token"}""")
            } else {
                exchange.respond(200, """{"permissions":["profile.playback"],"modules":["Library","Playback"]}""")
            }
        }
        server.start()
        try {
            val client = HttpAppApiClient("http://127.0.0.1:${server.address.port}", tokens)
            val response = runBlocking { client.capabilities() }
            assertTrue(response.hasModule("Library"))
            assertEquals(listOf("Bearer old-credential", "Bearer new-credential"), seen)
            assertEquals("new-credential", tokens.current)
        } finally {
            server.stop(0)
        }
    }

    private fun localServer(): HttpServer = HttpServer.create(InetSocketAddress("127.0.0.1", 0), 0)

    private fun HttpExchange.respond(status: Int, json: String, deviceToken: String? = null) {
        val bytes = json.toByteArray(Charsets.UTF_8)
        responseHeaders.add("Content-Type", "application/json")
        if (deviceToken != null) responseHeaders.add(AppApiRoutes.DeviceTokenHeader, deviceToken)
        sendResponseHeaders(status, bytes.size.toLong())
        responseBody.use { it.write(bytes) }
    }
}
