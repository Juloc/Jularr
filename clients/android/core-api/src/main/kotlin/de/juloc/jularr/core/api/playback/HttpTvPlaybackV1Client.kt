package de.juloc.jularr.core.api.playback

import de.juloc.jularr.core.api.AppApiRoutes
import de.juloc.jularr.core.api.DeviceTokenStore
import de.juloc.jularr.core.api.appApiException
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.runInterruptible
import kotlinx.coroutines.withContext
import org.json.JSONArray
import org.json.JSONObject
import java.net.HttpURLConnection
import java.net.URI

/**
 * Authenticated API-v1 session coordinator. The video bytes themselves are fetched by
 * Media3 using [TvPlaybackV1Plan.streamUrl] and the same bearer credential.
 */
class HttpTvPlaybackV1Client(
    origin: String,
    private val tokens: DeviceTokenStore,
) : TvPlaybackV1Client {
    private val serverOrigin: URI = URI(origin.trim().trimEnd('/')).also { uri ->
        require(uri.scheme == "https" || uri.scheme == "http") { "Invalid server scheme." }
        require(!uri.host.isNullOrBlank() && uri.rawUserInfo == null && uri.rawQuery == null && uri.rawFragment == null)
        require(uri.rawPath.isNullOrBlank() || uri.rawPath == "/") { "Only the server origin is allowed." }
    }

    override suspend fun start(mediaAssetId: Long, capabilities: TvPlaybackV1Capabilities): TvPlaybackV1Plan {
        require(mediaAssetId > 0) { "Playback requires a positive MediaAssetId." }
        val payload = JSONObject()
            .put("containers", JSONArray(capabilities.containers))
            .put("videoCodecs", JSONArray(capabilities.videoCodecs))
            .put("audioCodecs", JSONArray(capabilities.audioCodecs))
        val json = request("POST", "/api/v1/playback/assets/$mediaAssetId/sessions", payload)
        val sessionId = json.getString("playbackSessionId")
        require(runCatching { java.util.UUID.fromString(sessionId) }.isSuccess) { "Invalid playback session id." }
        val expectedBase = "/api/v1/playback/sessions/$sessionId/"
        val mode = json.getInt("playbackModeTypeId")
        require(mode in 1..3) { "Unsupported playback mode." }
        val streamUrl = json.getString("streamUrl")
        val validStream = streamUrl == expectedBase + "stream" ||
            streamUrl == expectedBase + "playlist.m3u8"
        require(validStream) { "Playback URL is not the authenticated server session." }
        require((mode == 1) == streamUrl.endsWith("/stream")) { "Playback mode and stream URL disagree." }
        return TvPlaybackV1Plan(
            playbackSessionId = sessionId,
            playbackModeTypeId = mode,
            streamUrl = serverOrigin.resolve(streamUrl).toString(),
            resumePositionMs = json.getLong("resumePositionMs").coerceAtLeast(0),
            durationMs = json.optLong("durationMs").takeIf { !json.isNull("durationMs") && it > 0 },
            audioStreamIndex = json.optInt("audioStreamIndex").takeIf { !json.isNull("audioStreamIndex") },
            quality = json.getInt("quality"),
            isReduced = json.getBoolean("isReduced"),
        )
    }

    override suspend fun update(
        playbackSessionId: String,
        positionMs: Long,
        durationMs: Long?,
        isEnded: Boolean,
        isCompleted: Boolean,
    ) {
        require(runCatching { java.util.UUID.fromString(playbackSessionId) }.isSuccess)
        require(positionMs >= 0)
        require(durationMs == null || durationMs >= 0)
        require(!isCompleted || isEnded) { "Completed playback must also be ended." }
        val body = JSONObject()
            .put("positionMs", positionMs)
            .put("durationMs", durationMs ?: JSONObject.NULL)
            .put("isEnded", isEnded)
            .put("isCompleted", isCompleted)
        val response = request("PUT", "/api/v1/playback/sessions/$playbackSessionId", body)
        check(response.getBoolean("success")) { "The server did not accept progress." }
    }

    private suspend fun request(method: String, path: String, body: JSONObject): JSONObject =
        withContext(Dispatchers.IO) {
            runInterruptible {
                var completed: JSONObject? = null
                for (attempt in 0..1) {
                    val sentToken = tokens.current
                    val connection = (serverOrigin.resolve(path).toURL().openConnection() as HttpURLConnection).apply {
                        requestMethod = method
                        connectTimeout = 15_000
                        readTimeout = 30_000
                        instanceFollowRedirects = false
                        setRequestProperty("Accept", "application/json")
                        setRequestProperty("Content-Type", "application/json")
                        sentToken?.let { setRequestProperty("Authorization", "Bearer $it") }
                        doOutput = true
                    }
                    try {
                        connection.outputStream.use {
                            it.write(body.toString().toByteArray(Charsets.UTF_8))
                        }
                        val status = connection.responseCode
                        val payload = (if (status in 200..299) connection.inputStream else connection.errorStream)
                            ?.bufferedReader(Charsets.UTF_8)?.use { it.readText() }.orEmpty()

                        // A concurrent profile switch/rotation can supersede this request.
                        // A stale response must neither revoke nor overwrite the new token.
                        val tokenChanged = synchronized(tokens) {
                            if (tokens.current == sentToken) {
                                if (status == HttpURLConnection.HTTP_UNAUTHORIZED) {
                                    tokens.clear()
                                } else if (status in 200..299) {
                                    tokens.update(connection.getHeaderField(AppApiRoutes.DeviceTokenHeader))
                                }
                            }
                            tokens.current != sentToken
                        }
                        // A 401 for a *previous* credential is not a new sign-out.
                        // Retry once with the active credential instead of leaving playback.
                        if (status == HttpURLConnection.HTTP_UNAUTHORIZED && tokenChanged && attempt == 0 &&
                            tokens.current != null
                        ) {
                            continue
                        }
                        if (status !in 200..299) throw appApiException(status, payload)
                        completed = JSONObject(payload)
                        break
                    } finally {
                        connection.disconnect()
                    }
                }
                checkNotNull(completed) { "Playback request was not completed." }
            }
        }
}
