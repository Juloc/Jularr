package de.juloc.jularr.core.api.playback

import com.sun.net.httpserver.HttpExchange
import com.sun.net.httpserver.HttpServer
import de.juloc.jularr.core.api.DeviceTokenStore
import kotlinx.coroutines.runBlocking
import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Test
import java.net.InetSocketAddress
import java.util.UUID

class TvCompanionV1ClientTest {
    private val session = "e67ad1d5-0fb6-4082-bca4-3719404d3d1d"
    private val command = "a01f75bc-a966-4d23-a9d6-0d250343ac65"

    @Test fun invitationStatusAndRemoteCommandAcksFollowExactServerV1Contract() = runBlocking {
        val server = HttpServer.create(InetSocketAddress("127.0.0.1", 0), 0)
        val seen = mutableListOf<Pair<String, JSONObject>>()
        server.createContext("/api/v1/playback/sessions/$session/companion") { exchange ->
            assertEquals("Bearer paired", exchange.requestHeaders.getFirst("Authorization"))
            val payload = exchange.requestBody.bufferedReader().use { it.readText() }
                .takeIf { it.isNotBlank() }?.let(::JSONObject) ?: JSONObject()
            seen += exchange.requestMethod + " " + exchange.requestURI.path to payload
            val response = when (exchange.requestURI.path.substringAfterLast('/')) {
                "invitations" -> """{"token":"example-secret","expiresAt":"2026-10-24T11:00:00Z"}"""
                "next" -> """{"id":"$command","revision":2,"action":"seek","positionMs":9000}"""
                "state" -> """{"success":true}"""
                command -> """{"success":true}"""
                else -> if (exchange.requestMethod == "DELETE") """{"success":true}"""
                else """{"playbackSessionId":"$session","revision":1,"isPlaying":true,
                    "positionMs":2000,"durationMs":10000,"reportedAt":null,
                    "lastCommandId":null,"lastCommandSucceeded":null}"""
            }
            val bytes = response.toByteArray()
            exchange.responseHeaders.add("Content-Type", "application/json")
            exchange.sendResponseHeaders(200, bytes.size.toLong())
            exchange.responseBody.use { it.write(bytes) }
        }
        server.start()
        try {
            val client = HttpTvCompanionV1Client(
                "http://127.0.0.1:${server.address.port}", DeviceTokenStore("paired"),
            )
            val invite = client.createInvitation(session)
            assertEquals("example-secret", invite.token)
            val state = client.readState(session)
            assertEquals(1L, state.revision)
            val next = requireNotNull(client.nextCommand(session))
            assertEquals("seek", next.action)
            assertEquals(9000L, next.positionMs)
            assertTrue(client.reportState(session, state))
            assertTrue(client.acknowledge(session, next, true, state))
            assertTrue(client.revoke(session))
            assertTrue(seen.any { it.first.endsWith("/invitations") })
            assertTrue(seen.any { it.first == "POST /api/v1/playback/sessions/$session/companion/next" })
            assertEquals(9000L, next.positionMs)
            val ack = seen.first { it.first.endsWith("/commands/$command") }.second
            assertEquals(2L, ack.getLong("revision"))
            assertTrue(ack.getBoolean("succeeded"))
            val heartbeat = seen.first { it.first == "PUT /api/v1/playback/sessions/$session/companion/state" }.second
            assertEquals(1L, heartbeat.getLong("revision"))
            assertEquals(2000L, heartbeat.getLong("positionMs"))
        } finally {
            server.stop(0)
        }
    }

    @Test fun invalidSessionIdIsNotUsedInEndpointPath() {
        val client = HttpTvCompanionV1Client("http://127.0.0.1:1", DeviceTokenStore("test"))
        assertThrows(IllegalArgumentException::class.java) {
            runBlocking { client.createInvitation("not-a-uuid") }
        }
        assertEquals(UUID.fromString(session).toString(), session)
    }
}
