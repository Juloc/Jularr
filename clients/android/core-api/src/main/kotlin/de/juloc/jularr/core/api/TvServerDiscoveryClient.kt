package de.juloc.jularr.core.api

import de.juloc.jularr.core.model.DiscoveredJularrServer
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import kotlinx.coroutines.withTimeoutOrNull
import org.json.JSONException
import org.json.JSONObject
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress
import java.net.InetSocketAddress
import java.net.SocketException
import java.net.SocketTimeoutException

/**
 * Best-effort LAN discovery for #489: broadcasts [ProbeMessage] to [Port] and collects unique
 * replies from Jularr servers for [timeoutMs]. This is a plain UDP broadcast/reply protocol, not
 * real mDNS/DNS-SD (`_jularr._tcp.local`) — see docs/ANDROID_CLIENTS.md "Discovery" for why, and
 * what a full mDNS responder would need. A server that never replies (blocked broadcast, no
 * server running, firewalled) simply yields an empty list; the caller falls back to manual setup.
 */
class TvServerDiscoveryClient {
    suspend fun discover(timeoutMs: Long = DefaultTimeoutMs): List<DiscoveredJularrServer> =
        withContext(Dispatchers.IO) {
            withTimeoutOrNull(timeoutMs) { runDiscovery(timeoutMs) } ?: emptyList()
        }

    private fun runDiscovery(timeoutMs: Long): List<DiscoveredJularrServer> {
        val found = LinkedHashMap<String, DiscoveredJularrServer>()

        val socket = try {
            DatagramSocket().apply {
                broadcast = true
                soTimeout = SocketPollMs
            }
        } catch (exception: SocketException) {
            return emptyList()
        }

        socket.use {
            val probe = ProbeMessage.toByteArray()
            val destination = InetAddress.getByName(BroadcastAddress)

            runCatching {
                socket.send(DatagramPacket(probe, probe.size, destination, Port))
            }

            val deadline = System.currentTimeMillis() + timeoutMs
            val buffer = ByteArray(MaxReplySize)

            while (System.currentTimeMillis() < deadline) {
                val packet = DatagramPacket(buffer, buffer.size)
                try {
                    socket.receive(packet)
                } catch (timeout: SocketTimeoutException) {
                    continue
                } catch (closed: SocketException) {
                    break
                }

                val server = parseReply(packet) ?: continue
                found[server.origin] = server
            }
        }

        return found.values.toList()
    }

    private fun parseReply(packet: DatagramPacket): DiscoveredJularrServer? {
        val json = try {
            JSONObject(String(packet.data, packet.offset, packet.length, Charsets.UTF_8))
        } catch (exception: JSONException) {
            return null
        }

        if (json.optString("service") != "jularr") {
            return null
        }

        val host = (packet.socketAddress as? InetSocketAddress)?.address?.hostAddress
            ?: return null
        val port = json.optInt("port", -1)
        if (port <= 0) {
            return null
        }

        val scheme = if (json.optBoolean("https", false)) "https" else "http"
        return DiscoveredJularrServer(
            origin = "$scheme://$host:$port",
            name = json.optString("name", "Jularr"),
            version = json.optString("version", ""),
        )
    }

    private companion object {
        const val Port = 37812
        const val ProbeMessage = "JULARR_DISCOVER_V1"
        const val BroadcastAddress = "255.255.255.255"
        const val MaxReplySize = 2048
        const val SocketPollMs = 300
        const val DefaultTimeoutMs = 2_000L
    }
}
