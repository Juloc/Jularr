package de.juloc.jularr.core.api.profile

import de.juloc.jularr.core.api.AppApiRoutes
import de.juloc.jularr.core.api.DeviceTokenStore
import de.juloc.jularr.core.api.appApiException
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.runInterruptible
import kotlinx.coroutines.withContext
import org.json.JSONObject
import java.net.HttpURLConnection
import java.net.URI
import java.net.URL
import java.util.UUID

data class TvSelectableProfileV1(
    val id: Long,
    val displayName: String,
    val isLocked: Boolean,
    val pinLength: Int?,
    val isOwned: Boolean,
)

data class TvAccountSessionV1(
    val id: UUID,
    val isCurrent: Boolean,
    val deviceName: String?,
    val devicePlatform: String?,
    val playbackQuality: Int,
)

interface TvV1ProfileClient {
    suspend fun profiles(): List<TvSelectableProfileV1>
    suspend fun selectProfile(id: Long, pin: String? = null): Long
    suspend fun sessions(): List<TvAccountSessionV1>
    /** Returns true only if the server reports this was the current session. */
    suspend fun revokeSession(sessionId: UUID): Boolean
    suspend fun signOut()
}

/** Uses the same device-token object as pairing, catalog and playback. */
class HttpTvV1ProfileClient(origin: String, private val tokens: DeviceTokenStore) : TvV1ProfileClient {
    private val base: URI = URI(origin.trim().trimEnd('/')).also {
        require(it.scheme == "http" || it.scheme == "https")
        require(!it.host.isNullOrBlank() && it.rawUserInfo == null &&
            it.rawQuery == null && it.rawFragment == null &&
            (it.rawPath.isNullOrBlank() || it.rawPath == "/"))
    }

    override suspend fun profiles(): List<TvSelectableProfileV1> {
        val result = mutableListOf<TvSelectableProfileV1>()
        for (page in 1..100) {
            val json = JSONObject(request("GET", "/api/v1/profiles?page=$page&pageSize=100"))
            val rows = json.getJSONArray("items")
            for (i in 0 until rows.length()) {
                val profile = rows.getJSONObject(i)
                result.add(TvSelectableProfileV1(
                    id = profile.getLong("id"),
                    displayName = profile.getString("displayName"),
                    isLocked = profile.getBoolean("isLocked"),
                    pinLength = profile.optInt("pinLength").takeIf { !profile.isNull("pinLength") },
                    isOwned = profile.getBoolean("isOwned"),
                ))
            }
            if (rows.length() < 100) return result
        }
        error("Too many selectable profiles to display safely.")
    }

    override suspend fun selectProfile(id: Long, pin: String?): Long {
        require(id > 0)
        val response = if (pin == null) {
            request("PUT", "/api/v1/auth/profile/$id")
        } else {
            require(pin.isNotBlank() && pin.length <= 20)
            request("POST", "/api/v1/auth/profile/$id/pin", JSONObject().put("pin", pin))
        }
        val selectedId = JSONObject(response).getLong("profileId")
        check(selectedId == id) { "The server selected a different profile." }
        return selectedId
    }

    override suspend fun sessions(): List<TvAccountSessionV1> {
        val result = mutableListOf<TvAccountSessionV1>()
        for (page in 1..100) {
            val rows = JSONObject(request("GET", "/api/v1/sessions?page=$page&pageSize=100")).getJSONArray("items")
            for (i in 0 until rows.length()) {
                val item = rows.getJSONObject(i)
                result.add(TvAccountSessionV1(
                    id = UUID.fromString(item.getString("id")),
                    isCurrent = item.getBoolean("isCurrent"),
                    deviceName = item.optString("deviceName").takeUnless { item.isNull("deviceName") },
                    devicePlatform = item.optString("devicePlatform").takeUnless { item.isNull("devicePlatform") },
                    playbackQuality = item.getInt("playbackQuality"),
                ))
            }
            if (rows.length() < 100) return result
        }
        error("Too many sessions to display safely.")
    }

    override suspend fun revokeSession(sessionId: UUID): Boolean {
        val json = JSONObject(request("DELETE", "/api/v1/sessions/$sessionId"))
        val endedCurrent = json.getBoolean("endsCurrentSession")
        if (endedCurrent) tokens.clear()
        return endedCurrent
    }

    override suspend fun signOut() {
        val response = JSONObject(request("DELETE", "/api/v1/auth/session"))
        check(response.getBoolean("endsCurrentSession")) { "Server did not close the session." }
        tokens.clear()
    }

    private suspend fun request(method: String, path: String, body: JSONObject? = null): String =
        withContext(Dispatchers.IO) {
            runInterruptible {
                // A GET can safely retry after a concurrent profile/token rotation.
                // Never retry POST/PUT/DELETE: the old operation may already have
                // committed a profile switch or session revocation on the server.
                var completed: String? = null
                val attempts = if (method == "GET") 2 else 1
                for (attempt in 0 until attempts) {
                    val prior = tokens.current ?: error("Sign in before accessing profiles.")
                    val connection = (URL(base.toString() + path).openConnection() as HttpURLConnection).apply {
                        requestMethod = method
                        instanceFollowRedirects = false
                        connectTimeout = 15_000
                        readTimeout = 30_000
                        setRequestProperty("Accept", "application/json")
                        setRequestProperty("Authorization", "Bearer $prior")
                        if (body != null) {
                            setRequestProperty("Content-Type", "application/json")
                            doOutput = true
                        }
                    }
                    try {
                        body?.let { connection.outputStream.use { out ->
                            out.write(it.toString().toByteArray(Charsets.UTF_8))
                        } }
                        val status = connection.responseCode
                        val payload = (if (status in 200..299) connection.inputStream else connection.errorStream)
                            ?.bufferedReader(Charsets.UTF_8)?.use { it.readText() }.orEmpty()
                        val stale = synchronized(tokens) {
                            if (tokens.current != prior) true
                            else {
                                if (status == HttpURLConnection.HTTP_UNAUTHORIZED) tokens.clear()
                                else if (status in 200..299) {
                                    tokens.update(connection.getHeaderField(AppApiRoutes.DeviceTokenHeader))
                                }
                                false
                            }
                        }
                        if (stale && status == HttpURLConnection.HTTP_UNAUTHORIZED &&
                            method == "GET" && attempt == 0 && tokens.current != null
                        ) {
                            continue
                        }
                        if (status !in 200..299) throw appApiException(status, payload)
                        completed = payload
                        break
                    } finally {
                        connection.disconnect()
                    }
                }
                checkNotNull(completed) { "Profile request was not completed." }
            }
        }
}
