package de.juloc.jularr.core.api

import de.juloc.jularr.core.design.DesignTokens
import de.juloc.jularr.core.model.AuthenticationV1
import de.juloc.jularr.core.model.CollectionV1
import de.juloc.jularr.core.model.ContinueItemV1
import de.juloc.jularr.core.model.DevicePairingClaim
import de.juloc.jularr.core.model.DevicePairingRequest
import de.juloc.jularr.core.model.DevicePairingStarted
import de.juloc.jularr.core.model.DiscoverItemV1
import de.juloc.jularr.core.model.DiscoverScopeV1
import de.juloc.jularr.core.model.DiscoverShelfV1
import de.juloc.jularr.core.model.DiscoverSuggestionV1
import de.juloc.jularr.core.model.HomeShelfV1
import de.juloc.jularr.core.model.InstanceCapabilities
import de.juloc.jularr.core.model.InstancePresentationV1
import de.juloc.jularr.core.model.PlaybackPlanV1
import de.juloc.jularr.core.model.ProfileSelectionV1
import de.juloc.jularr.core.model.UiLocaleV1
import de.juloc.jularr.core.model.WorkCardV1
import de.juloc.jularr.core.model.WorkDetailsV1
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.runInterruptible
import kotlinx.coroutines.withContext
import org.json.JSONArray
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
        requestObject("POST", AppApiRoutes.Pairings, request.toRequestBody()).toDevicePairingStarted()

    override suspend fun claimPairing(pairingId: String): DevicePairingClaim {
        val answer = request("POST", AppApiRoutes.pairingClaim(pairingId), null)
        return JSONObject(answer.text.ifBlank { "{}" }).toDevicePairingClaim(answer.deviceToken)
    }

    override suspend fun capabilities(): InstanceCapabilities =
        requestObject("GET", AppApiRoutes.Capabilities, null).toInstanceCapabilities()

    override suspend fun authentication(): AuthenticationV1 =
        requestObject("GET", AppApiRoutes.Authentication, null).toAuthentication()

    override suspend fun instancePresentation(): InstancePresentationV1 =
        requestObject("GET", AppApiRoutes.InstancePresentation, null).toInstancePresentation()

    override suspend fun uiTexts(uiLocaleId: Long): Map<String, String> =
        requestObject("GET", AppApiRoutes.uiTexts(uiLocaleId), null).toUiTexts()

    override suspend fun watchlist(page: Int, pageSize: Int): List<WorkCardV1> =
        requestObject("GET", AppApiRoutes.watchlistPage(page, pageSize), null).toWorkCards()

    override suspend fun library(page: Int, pageSize: Int): List<WorkCardV1> =
        requestObject("GET", AppApiRoutes.libraryPage(page, pageSize), null).toWorkCards()

    override suspend fun designTokens(): DesignTokens =
        DesignTokens.parse(requestText("GET", AppApiRoutes.DesignTokens))

    override suspend fun continueWatching(): List<ContinueItemV1> =
        requestList("GET", AppApiRoutes.Continue).map { it.toContinueItem() }

    override suspend fun homeLayout(): List<HomeShelfV1> =
        requestList("GET", AppApiRoutes.HomeLayout).map { it.toHomeShelf() }

    override suspend fun discoverScopes(): List<DiscoverScopeV1> =
        requestList("GET", AppApiRoutes.DiscoverScopes).map { it.toDiscoverScope() }

    override suspend fun search(query: String, mediaTypeId: Int?, page: Int): List<DiscoverItemV1> {
        val body = JSONObject().put("query", query).put("page", page)
        if (mediaTypeId != null) {
            body.put("mediaTypeId", mediaTypeId)
        }
        return requestList("POST", AppApiRoutes.DiscoverSearch, body).map { it.toDiscoverItem() }
    }

    override suspend fun suggestions(query: String, scopeSlug: String?): List<DiscoverSuggestionV1> {
        val body = JSONObject().put("query", query)
        if (scopeSlug != null) {
            body.put("scopeSlug", scopeSlug)
        }
        return requestList("POST", AppApiRoutes.DiscoverSuggestions, body).map { it.toDiscoverSuggestion() }
    }

    override suspend fun work(workId: Long): WorkDetailsV1 =
        requestObject("GET", AppApiRoutes.work(workId), null).toWorkDetails()

    override suspend fun discoverShelves(scopeSlug: String?): List<DiscoverShelfV1> {
        val body = JSONObject()
        if (scopeSlug != null) {
            body.put("scopeSlug", scopeSlug)
        }
        return requestList("POST", AppApiRoutes.DiscoverShelves, body).map { it.toDiscoverShelf() }
    }

    override suspend fun startPlayback(mediaAssetId: Long): PlaybackPlanV1 =
        requestObject("POST", AppApiRoutes.playbackSessions(mediaAssetId), JSONObject()).toPlaybackPlan()

    override suspend fun updatePlayback(
        playbackSessionId: String,
        positionMs: Long,
        durationMs: Long?,
        isEnded: Boolean,
        isCompleted: Boolean,
    ) {
        val body = JSONObject()
            .put("positionMs", positionMs)
            .put("isEnded", isEnded)
            .put("isCompleted", isCompleted)
        if (durationMs != null) {
            body.put("durationMs", durationMs)
        }
        request("PUT", AppApiRoutes.playbackSession(playbackSessionId), body)
    }

    override suspend fun locales(): List<UiLocaleV1> =
        requestList("GET", AppApiRoutes.Locales).map { it.toUiLocale() }

    override suspend fun updateProfileLanguage(profileId: Long, uiLocaleId: Long) {
        request("PUT", AppApiRoutes.profile(profileId), JSONObject().put("uiLocaleId", uiLocaleId))
    }

    override suspend fun collections(): List<CollectionV1> =
        requestList("GET", AppApiRoutes.Collections).map { it.toCollection() }

    override suspend fun collectionWorks(collectionId: Long): List<WorkCardV1> =
        requestObject("GET", AppApiRoutes.collectionWorks(collectionId), null).toWorkCards()

    override suspend fun setCollectionWork(collectionId: Long, workId: Long, member: Boolean) {
        val route = AppApiRoutes.collectionWork(collectionId, workId)
        if (member) {
            request("PUT", route, null)
        } else {
            request("DELETE", route, null)
        }
    }

    override suspend fun createCollection(name: String): Long =
        requestObject("POST", AppApiRoutes.Collections, JSONObject().put("name", name).put("isSmart", false))
            .getLong("id")

    override suspend fun renameCollection(collectionId: Long, name: String, version: Long) {
        request(
            "PUT",
            AppApiRoutes.collection(collectionId),
            JSONObject().put("name", name).put("version", version),
        )
    }

    override suspend fun deleteCollection(collectionId: Long) {
        request("DELETE", AppApiRoutes.collection(collectionId), null)
    }

    override suspend fun profiles(): List<ProfileSelectionV1> =
        requestObject("GET", "${AppApiRoutes.Profiles}?page=1&pageSize=50", null).toProfiles()

    override suspend fun selectProfile(profileId: Long) {
        request("PUT", AppApiRoutes.selectProfile(profileId), null)
    }

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

                    AppResponse(text = payload, deviceToken = deviceToken)
                } finally {
                    connection.disconnect()
                }
            }
        }

    /** Reads an object answer; the services answer a page and a detail as an object. */
    private suspend fun requestObject(method: String, route: String, body: JSONObject?): JSONObject =
        JSONObject(request(method, route, body).text.ifBlank { "{}" })

    /** Reads a plain text answer, like the shared design tokens. */
    private suspend fun requestText(method: String, route: String, body: JSONObject? = null): String =
        request(method, route, body).text

    /** Reads a list answer; the services answer a data list as a plain array. */
    private suspend fun requestList(method: String, route: String, body: JSONObject? = null): List<JSONObject> {
        val text = requestText(method, route, body)
        if (text.isBlank()) {
            return emptyList()
        }
        val array = JSONArray(text)
        return (0 until array.length()).map { array.getJSONObject(it) }
    }

    /** One answer of the server: its text and the device token it carried. */
    private class AppResponse(val text: String, val deviceToken: String?)

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
