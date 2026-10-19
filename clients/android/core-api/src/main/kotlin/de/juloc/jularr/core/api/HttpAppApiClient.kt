package de.juloc.jularr.core.api

import de.juloc.jularr.core.model.DevicePairingClaim
import de.juloc.jularr.core.model.DevicePairingRequest
import de.juloc.jularr.core.model.DevicePairingStarted
import de.juloc.jularr.core.model.InstanceCapabilities
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.runInterruptible
import kotlinx.coroutines.withContext
import org.json.JSONObject
import java.net.HttpURLConnection
import java.net.URI
import java.net.URL

/**
 * The app services over HTTP. The device token travels as a bearer token; the server renews
 * it past half its lifetime and sends the new one with the answer.
 */
class HttpAppApiClient(
    origin: String,
    private val deviceTokens: DeviceTokenStore = DeviceTokenStore(),
) : AppApiClient {
    private val originUri = normalizeOrigin(origin)

    override suspend fun startPairing(request: DevicePairingRequest): DevicePairingStarted =
        request("POST", AppApiRoutes.Pairings, request.toRequestBody()).payload.toDevicePairingStarted()

    override suspend fun claimPairing(pairingId: String): DevicePairingClaim {
        val answer = request("POST", AppApiRoutes.pairingClaim(pairingId), null)
        return answer.payload.toDevicePairingClaim(answer.deviceToken)
    }

    override suspend fun capabilities(): InstanceCapabilities =
        request("GET", AppApiRoutes.Capabilities, null).payload.toInstanceCapabilities()

    private suspend fun request(method: String, route: String, body: JSONObject?): AppResponse =
        withContext(Dispatchers.IO) {
            runInterruptible {
                val connection = (URL(originUri.toString() + route).openConnection() as HttpURLConnection).apply {
                    requestMethod = method
                    connectTimeout = ConnectTimeoutMs
                    readTimeout = ReadTimeoutMs
                    setRequestProperty("Accept", "application/json")
                    deviceTokens.current?.let { setRequestProperty("Authorization", "Bearer $it") }
                    if (body != null) {
                        doOutput = true
                        setRequestProperty("Content-Type", "application/json")
                    }
                }

                try {
                    body?.let { payload ->
                        connection.outputStream.use { stream ->
                            stream.write(payload.toString().toByteArray(Charsets.UTF_8))
                        }
                    }

                    val status = connection.responseCode
                    val payload = (if (status in 200..299) connection.inputStream else connection.errorStream)
                        ?.bufferedReader(Charsets.UTF_8)
                        ?.use { it.readText() }
                        .orEmpty()

                    // The server renews the device token past half its lifetime.
                    val deviceToken = connection.getHeaderField(AppApiRoutes.DeviceTokenHeader)
                    deviceTokens.update(deviceToken)

                    if (status !in 200..299) {
                        throw appApiException(status, payload)
                    }

                    AppResponse(
                        payload = if (payload.isBlank()) JSONObject() else JSONObject(payload),
                        deviceToken = deviceToken,
                    )
                } finally {
                    connection.disconnect()
                }
            }
        }

    /** One answer of the server: its payload and the device token it carried. */
    private class AppResponse(val payload: JSONObject, val deviceToken: String?)

    private companion object {
        const val ConnectTimeoutMs = 15_000
        const val ReadTimeoutMs = 30_000

        fun normalizeOrigin(origin: String): URI {
            val trimmed = origin.trim().trimEnd('/')
            require(trimmed.isNotEmpty()) { "The server address is empty." }
            val uri = URI(trimmed)
            require(uri.scheme == "http" || uri.scheme == "https") { "The server address must be an http or https address." }
            require(!uri.host.isNullOrEmpty()) { "The server address has no host." }
            return uri
        }
    }
}
