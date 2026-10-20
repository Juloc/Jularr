package de.juloc.jularr.core.api

import de.juloc.jularr.core.model.AuthenticationV1
import de.juloc.jularr.core.model.ContinueItemV1
import de.juloc.jularr.core.model.DevicePairingClaim
import de.juloc.jularr.core.model.DevicePairingRequest
import de.juloc.jularr.core.model.DevicePairingStarted
import de.juloc.jularr.core.model.DiscoverScopeV1
import de.juloc.jularr.core.model.HomeShelfV1
import de.juloc.jularr.core.model.InstanceCapabilities
import de.juloc.jularr.core.model.InstancePresentationV1
import de.juloc.jularr.core.model.ProfileSelectionV1
import de.juloc.jularr.core.model.WorkCardV1
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

internal fun JSONObject.toInstancePresentation(): InstancePresentationV1 =
    InstancePresentationV1(
        name = getString("name"),
        uiLocaleId = getLong("uiLocaleId"),
        publicUrl = if (isNull("publicUrl")) null else getString("publicUrl"),
        devicesEnabled = getBoolean("devicesEnabled"),
        registrationEnabled = getBoolean("registrationEnabled"),
    )

internal fun JSONObject.toUiTexts(): Map<String, String> {
    val texts = getJSONObject("texts")
    val result = mutableMapOf<String, String>()
    for (key in texts.keys()) {
        result[key] = texts.getString(key)
    }
    return result
}

internal fun JSONObject.toWorkCards(): List<WorkCardV1> =
    getJSONArray("items").mapObjects { it.toWorkCard() }

internal fun JSONObject.toProfiles(): List<ProfileSelectionV1> =
    getJSONArray("items").mapObjects { it.toProfileSelection() }

internal fun JSONObject.toWorkCard(): WorkCardV1 =
    WorkCardV1(
        id = getLong("id"),
        mediaTypeId = getInt("mediaTypeId"),
        mediaSubtypeId = if (isNull("mediaSubtypeId")) null else getInt("mediaSubtypeId"),
        title = getString("title"),
        releaseYear = if (isNull("releaseYear")) null else getInt("releaseYear"),
        posterUrl = if (isNull("posterUrl")) null else getString("posterUrl"),
        mediaStateTypeId = getInt("mediaStateTypeId"),
        isWatchlisted = getBoolean("isWatchlisted"),
        availabilityStateId = getInt("availabilityStateId"),
        availableUnits = getInt("availableUnits"),
        monitoredUnits = getInt("monitoredUnits"),
        audioLanguages = getJSONArray("audioLanguages").toStringList(),
        subtitleLanguages = getJSONArray("subtitleLanguages").toStringList(),
    )

internal fun JSONObject.toContinueItem(): ContinueItemV1 =
    ContinueItemV1(
        workId = getLong("workId"),
        mediaAssetId = getLong("mediaAssetId"),
        mediaTypeId = getInt("mediaTypeId"),
        title = getString("title"),
        posterUrl = if (isNull("posterUrl")) null else getString("posterUrl"),
        workUnitTypeId = if (isNull("workUnitTypeId")) null else getInt("workUnitTypeId"),
        unitNumber = if (isNull("unitNumber")) null else getDouble("unitNumber"),
        parentUnitNumber = if (isNull("parentUnitNumber")) null else getDouble("parentUnitNumber"),
        positionMs = getLong("positionMs"),
        durationMs = if (isNull("durationMs")) null else getLong("durationMs"),
        progression = if (isNull("progression")) null else getDouble("progression"),
        mediaAssetTypeId = getInt("mediaAssetTypeId"),
    )

internal fun JSONObject.toHomeShelf(): HomeShelfV1 =
    HomeShelfV1(
        homeShelfTypeId = getInt("homeShelfTypeId"),
        isHidden = getBoolean("isHidden"),
    )

internal fun JSONObject.toDiscoverScope(): DiscoverScopeV1 =
    DiscoverScopeV1(
        slug = getString("slug"),
        mediaTypeId = getInt("mediaTypeId"),
        mediaSubtypeId = if (isNull("mediaSubtypeId")) null else getInt("mediaSubtypeId"),
        isCatalog = getBoolean("isCatalog"),
        feeds = getJSONArray("feeds").toIntList(),
    )

internal fun JSONObject.toAuthentication(): AuthenticationV1 =
    AuthenticationV1(
        accountId = if (isNull("accountId")) null else getLong("accountId"),
        profileId = if (isNull("profileId")) null else getLong("profileId"),
        uiLocaleId = if (isNull("uiLocaleId")) null else getLong("uiLocaleId"),
        permissions = getJSONArray("permissions").toStringList(),
    )

internal fun JSONObject.toProfileSelection(): ProfileSelectionV1 =
    ProfileSelectionV1(
        id = getLong("id"),
        displayName = getString("displayName"),
        isLocked = getBoolean("isLocked"),
        pinLength = if (isNull("pinLength")) null else getInt("pinLength"),
        isOwned = getBoolean("isOwned"),
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

private fun JSONArray.toIntList(): List<Int> = (0 until length()).map { getInt(it) }

private fun <T> JSONArray.mapObjects(read: (JSONObject) -> T): List<T> =
    (0 until length()).map { read(getJSONObject(it)) }
