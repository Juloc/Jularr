package de.juloc.jularr.core.api.playbackextras

import de.juloc.jularr.core.api.AppApiRoutes
import de.juloc.jularr.core.api.DeviceTokenStore
import de.juloc.jularr.core.api.appApiException
import de.juloc.jularr.core.api.playback.TvPlaybackV1Capabilities
import de.juloc.jularr.core.api.playback.TvPlaybackV1Plan
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.runInterruptible
import kotlinx.coroutines.withContext
import org.json.JSONArray
import org.json.JSONObject
import java.io.InputStream
import java.net.HttpURLConnection
import java.net.URI
import java.util.UUID

/**
 * Extra API for switching to a *real* audio stream from GET /api/v1/playback/assets/{id}/tracks.
 *
 * Starts a new backend playback session. The original TV player/controller owns ending
 * its previous session and replacing the Media3 media item. This class does not edit
 * or wrap the other agent's playback client or player panels.
 */
interface TvV1AudioSelectionSessionClient {
    suspend fun startWithAudioTrack(
        mediaAssetId: Long,
        capabilities: TvPlaybackV1Capabilities,
        audioTrack: TvV1PlaybackTrack,
        quality: Int? = null,
    ): TvPlaybackV1Plan
}

class HttpTvV1AudioSelectionSessionClient(
    origin: String,
    private val tokens: DeviceTokenStore,
) : TvV1AudioSelectionSessionClient {
    private val serverOrigin: URI = URI(origin.trim().trimEnd('/')).also { uri ->
        require(uri.scheme == "http" || uri.scheme == "https")
        require(!uri.host.isNullOrBlank() && uri.rawUserInfo == null && uri.rawQuery == null && uri.rawFragment == null)
        require(uri.rawPath.isNullOrBlank() || uri.rawPath == "/")
    }

    override suspend fun startWithAudioTrack(
        mediaAssetId: Long,
        capabilities: TvPlaybackV1Capabilities,
        audioTrack: TvV1PlaybackTrack,
        quality: Int?,
    ): TvPlaybackV1Plan {
        require(mediaAssetId > 0) { "A playback session needs a real MediaAssetId." }
        require(audioTrack.mediaTrackTypeId == 2 && audioTrack.isSupported && !audioTrack.isExternalSidecar) {
            "Select a supported, embedded audio track returned by the server."
        }
        val streamIndex = audioTrack.key.removePrefix("s").toIntOrNull()
        require(streamIndex != null && streamIndex >= 0) { "Invalid audio stream key." }
        require(quality == null || quality in 1..4) { "Invalid PlaybackQuality enum." }
        val allFormats = capabilities.containers + capabilities.videoCodecs + capabilities.audioCodecs
        require(
            capabilities.containers.size <= 32 && capabilities.videoCodecs.size <= 32 &&
                capabilities.audioCodecs.size <= 32 &&
                allFormats.all { it.isNotEmpty() && it.length <= 32 }
        ) { "Device capability arrays exceed backend limits." }

        val body = JSONObject()
            .put("containers", JSONArray(capabilities.containers))
            .put("videoCodecs", JSONArray(capabilities.videoCodecs))
            .put("audioCodecs", JSONArray(capabilities.audioCodecs))
            .put("audioStreamIndex", streamIndex)
            .apply { quality?.let { put("quality", it) } }
        val json = request(mediaAssetId, body)
        val id = json.getString("playbackSessionId")
        require(runCatching { UUID.fromString(id) }.isSuccess) { "Invalid session ID." }
        val mode = json.getInt("playbackModeTypeId")
        require(mode in 1..3) { "Unsupported playback mode." }
        val stream = json.getString("streamUrl")
        val expected = "/api/v1/playback/sessions/$id/"
        require(stream == expected + "stream" || stream == expected + "playlist.m3u8") {
            "Server supplied an untrusted playback URL."
        }
        require((mode == 1) == stream.endsWith("/stream")) { "Playback mode and stream URL disagree." }
        val selected = if (json.isNull("audioStreamIndex")) null else json.getInt("audioStreamIndex")
        require(selected == streamIndex) { "Server returned an unexpected audio stream." }
        val resume = json.getLong("resumePositionMs")
        require(resume >= 0)
        val duration = if (json.isNull("durationMs")) null else json.getLong("durationMs")
        require(duration == null || duration >= 0)
        return TvPlaybackV1Plan(
            playbackSessionId = id,
            playbackModeTypeId = mode,
            streamUrl = serverOrigin.resolve(stream).toString(),
            resumePositionMs = resume,
            durationMs = duration,
            audioStreamIndex = selected,
            quality = json.getInt("quality").also { require(it in 1..4) },
            isReduced = json.getBoolean("isReduced"),
        )
    }

    /** 401 retries once only after another request rotated the *same* shared token store. */
    private suspend fun request(mediaAssetId: Long, body: JSONObject): JSONObject = withContext(Dispatchers.IO) {
        runInterruptible {
            val url = serverOrigin.resolve("/api/v1/playback/assets/$mediaAssetId/sessions").toURL()
            var result: JSONObject? = null
            for (attempt in 0..1) {
                val sent = tokens.current
                check(!sent.isNullOrBlank()) { "The TV device is not paired." }
                val connection = (url.openConnection() as HttpURLConnection).apply {
                    requestMethod = "POST"
                    connectTimeout = 15_000
                    readTimeout = 30_000
                    instanceFollowRedirects = false
                    setRequestProperty("Authorization", "Bearer $sent")
                    setRequestProperty("Accept", "application/json")
                    setRequestProperty("Content-Type", "application/json")
                    doOutput = true
                }
                try {
                    connection.outputStream.use { it.write(body.toString().toByteArray(Charsets.UTF_8)) }
                    val code = connection.responseCode
                    val bytes = (if (code in 200..299) connection.inputStream else connection.errorStream)
                        ?.use { it.readLimited(maximumBytes = 1024 * 1024) } ?: ByteArray(0)
                    val changed = synchronized(tokens) {
                        val obsolete = tokens.current != sent
                        if (!obsolete) {
                            if (code == HttpURLConnection.HTTP_UNAUTHORIZED) tokens.clear()
                            else if (code in 200..299) {
                                tokens.update(connection.getHeaderField(AppApiRoutes.DeviceTokenHeader))
                            }
                        }
                        obsolete
                    }
                    if (code == HttpURLConnection.HTTP_UNAUTHORIZED && changed && attempt == 0 && tokens.current != null) {
                        continue
                    }
                    if (code !in 200..299) throw appApiException(code, bytes.toString(Charsets.UTF_8))
                    result = JSONObject(bytes.toString(Charsets.UTF_8))
                    break
                } finally {
                    connection.disconnect()
                }
            }
            checkNotNull(result) { "Audio-session request was not completed." }
        }
    }

    private fun InputStream.readLimited(maximumBytes: Int): ByteArray {
        val buffer = ByteArray(8192)
        val result = java.io.ByteArrayOutputStream()
        while (true) {
            val read = read(buffer)
            if (read == -1) break
            if (read == 0) continue
            check(result.size().toLong() + read <= maximumBytes) { "Oversized playback response." }
            result.write(buffer, 0, read)
        }
        return result.toByteArray()
    }
}
