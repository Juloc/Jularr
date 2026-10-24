package de.juloc.jularr.core.api.playback

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

data class TvCompanionInvitationV1(val token: String, val expiresAt: String)
data class TvCompanionStateV1(val revision: Long, val isPlaying: Boolean, val positionMs: Long, val durationMs: Long?)
data class TvCompanionCommandV1(val id: String, val revision: Long, val action: String, val positionMs: Long?)

interface TvCompanionV1Client {
    suspend fun createInvitation(playbackSessionId: String): TvCompanionInvitationV1
    suspend fun readState(playbackSessionId: String): TvCompanionStateV1
    suspend fun reportState(playbackSessionId: String, state: TvCompanionStateV1): Boolean
    suspend fun nextCommand(playbackSessionId: String): TvCompanionCommandV1?
    suspend fun acknowledge(playbackSessionId: String, command: TvCompanionCommandV1, succeeded: Boolean, state: TvCompanionStateV1): Boolean
    suspend fun revoke(playbackSessionId: String): Boolean
}

/** Owner-only /api/v1 TV transport, using the existing rotating device bearer. Never a cookie or a remote grant. */
class HttpTvCompanionV1Client(origin: String, private val tokens: DeviceTokenStore) : TvCompanionV1Client {
    private val originUri = URI(origin.trim().trimEnd('/')).also {
        require((it.scheme == "http" || it.scheme == "https") && !it.host.isNullOrBlank() &&
            it.rawUserInfo == null && it.rawQuery == null && it.rawFragment == null &&
            (it.rawPath.isNullOrEmpty() || it.rawPath == "/"))
    }

    private fun path(session: String): String =
        "/api/v1/playback/sessions/${UUID.fromString(session)}/companion"

    override suspend fun createInvitation(playbackSessionId: String): TvCompanionInvitationV1 =
        request("POST", path(playbackSessionId) + "/invitations").let {
            TvCompanionInvitationV1(it.getString("token"), it.getString("expiresAt"))
        }

    override suspend fun readState(playbackSessionId: String): TvCompanionStateV1 =
        request("GET", path(playbackSessionId)).state()

    override suspend fun reportState(playbackSessionId: String, state: TvCompanionStateV1): Boolean =
        request("PUT", path(playbackSessionId) + "/state", state.body()).getBoolean("success")

    override suspend fun nextCommand(playbackSessionId: String): TvCompanionCommandV1? {
        val value = request("POST", path(playbackSessionId) + "/next")
        if (value.isNull("id")) return null
        val action = value.getString("action")
        require(action in listOf("play", "pause", "seek")) { "Unrecognized companion command." }
        val id = UUID.fromString(value.getString("id")).toString()
        return TvCompanionCommandV1(
            id = id, revision = value.getLong("revision"), action = action,
            positionMs = value.optLong("positionMs").takeUnless { value.isNull("positionMs") },
        )
    }

    override suspend fun acknowledge(
        playbackSessionId: String, command: TvCompanionCommandV1, succeeded: Boolean, state: TvCompanionStateV1,
    ): Boolean = request(
        "PUT", path(playbackSessionId) + "/commands/${UUID.fromString(command.id)}",
        state.body(command.revision).put("succeeded", succeeded),
    ).getBoolean("success")

    override suspend fun revoke(playbackSessionId: String): Boolean =
        request("DELETE", path(playbackSessionId)).getBoolean("success")

    private fun TvCompanionStateV1.body(revision: Long = this.revision): JSONObject =
        JSONObject().put("revision", revision).put("isPlaying", isPlaying)
            .put("positionMs", positionMs)
            .put("durationMs", durationMs ?: JSONObject.NULL)

    private fun JSONObject.state(): TvCompanionStateV1 =
        TvCompanionStateV1(
            revision = getLong("revision"),
            isPlaying = getBoolean("isPlaying"),
            positionMs = getLong("positionMs"),
            durationMs = if (isNull("durationMs")) null else getLong("durationMs"),
        )

    private suspend fun request(method: String, route: String, body: JSONObject? = null): JSONObject =
        withContext(Dispatchers.IO) {
            runInterruptible {
                val credential = checkNotNull(tokens.current) { "TV device token is unavailable." }
                val conn = URL(originUri.toString() + route).openConnection() as HttpURLConnection
                conn.apply {
                    requestMethod = method
                    connectTimeout = 6_000
                    readTimeout = 8_000
                    instanceFollowRedirects = false
                    setRequestProperty("Accept", "application/json")
                    setRequestProperty("Authorization", "Bearer $credential")
                    if (body != null) {
                        doOutput = true
                        setRequestProperty("Content-Type", "application/json")
                    }
                }
                try {
                    body?.let { conn.outputStream.use { stream ->
                        stream.write(it.toString().toByteArray(Charsets.UTF_8))
                    } }
                    val status = conn.responseCode
                    val content = (if (status in 200..299) conn.inputStream else conn.errorStream)
                        ?.bufferedReader(Charsets.UTF_8)?.use { it.readText() }.orEmpty()
                    synchronized(tokens) {
                        check(tokens.current == credential) { "TV profile changed while companion request was active." }
                        if (status == 401) tokens.clear()
                        else if (status in 200..299) tokens.update(conn.getHeaderField(AppApiRoutes.DeviceTokenHeader))
                    }
                    if (status !in 200..299) throw appApiException(status, content)
                    JSONObject(content)
                } finally {
                    conn.disconnect()
                }
            }
        }
}
