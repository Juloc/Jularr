package de.juloc.jularr.core.api

import de.juloc.jularr.core.model.DevicePairingClaim
import de.juloc.jularr.core.model.DevicePairingRequest
import de.juloc.jularr.core.model.DevicePairingStarted
import de.juloc.jularr.core.model.InstanceCapabilities
import org.json.JSONArray
import org.json.JSONObject

internal fun JSONObject.toDevicePairingStarted(): DevicePairingStarted =
    DevicePairingStarted(
        pairingId = getString("pairingId"),
        code = getString("code"),
        expiresAt = getString("expiresAt"),
    )

internal fun JSONObject.toDevicePairingClaim(deviceToken: String?): DevicePairingClaim {
    val isPaired = getBoolean("isPaired")
    if (isPaired && deviceToken.isNullOrBlank()) {
        throw IllegalStateException("The server confirmed pairing without a device token.")
    }
    return DevicePairingClaim(
        isPaired = isPaired,
        // The server sends the device token only with the answer that confirms the pairing.
        deviceToken = if (isPaired) deviceToken else null,
    )
}

internal fun JSONObject.toInstanceCapabilities(): InstanceCapabilities =
    InstanceCapabilities(
        permissions = getJSONArray("permissions").toStringList(),
        modules = getJSONArray("modules").toStringList(),
    )

internal fun DevicePairingRequest.toRequestBody(): JSONObject =
    JSONObject()
        .put("deviceName", deviceName)
        .put("devicePlatform", devicePlatform.wireValue)
        .apply {
            if (replacesPairingId != null) {
                put("replacesPairingId", replacesPairingId)
            }
        }

/**
 * Reads a failed answer. The server answers with problem details that carry the machine code,
 * the key of the localized message and a trace reference; an answer without them keeps its
 * status and reports a generic code.
 */
internal fun appApiException(statusCode: Int, payload: String): AppApiException {
    val problem = runCatching { JSONObject(payload) }.getOrNull()
    val code = problem?.optString("code")?.takeIf { it.isNotEmpty() }
        ?: problem?.optString("title")?.takeIf { it.isNotEmpty() }
        ?: "http_error"
    val resourceKey = problem?.optString("resourceKey")?.takeIf { it.isNotEmpty() } ?: code
    val traceId = problem?.optString("traceId")?.takeIf { it.isNotEmpty() }
    return AppApiException(statusCode, code, resourceKey, traceId, "$code ($statusCode)")
}

private fun JSONArray.toStringList(): List<String> = (0 until length()).map { getString(it) }
