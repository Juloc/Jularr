package de.juloc.jularr.core.api.playbackextras

import de.juloc.jularr.core.api.AppApiRoutes
import de.juloc.jularr.core.api.DeviceTokenStore
import de.juloc.jularr.core.api.appApiException
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.runInterruptible
import kotlinx.coroutines.withContext
import org.json.JSONArray
import org.json.JSONObject
import java.io.ByteArrayOutputStream
import java.io.InputStream
import java.math.BigDecimal
import java.net.HttpURLConnection
import java.net.URI

/**
 * Authenticated, same-origin HTTP adapter for backend playback extras.
 *
 * A new instance MUST share the exact DeviceTokenStore object with pairing, TV catalog
 * and session clients. No persistent credential copy is held here.
 */
class HttpTvV1PlaybackExtrasClient(
    origin: String,
    private val tokens: DeviceTokenStore,
) : TvV1PlaybackExtrasClient {
    private val serverOrigin: URI = URI(origin.trim().trimEnd('/')).also { uri ->
        require(uri.scheme == "http" || uri.scheme == "https")
        require(!uri.host.isNullOrBlank() && uri.rawUserInfo == null && uri.rawQuery == null && uri.rawFragment == null)
        require(uri.rawPath.isNullOrBlank() || uri.rawPath == "/")
    }

    override suspend fun markers(mediaAssetId: Long): List<TvV1PlaybackMarker> =
        JSONArray(read("GET", TvV1PlaybackExtrasRoutes.markers(mediaAssetId)).text())
            .toTvExtrasList { tvPlaybackMarker() }

    override suspend fun chapters(mediaAssetId: Long): List<TvV1PlaybackChapter> =
        JSONArray(read("GET", TvV1PlaybackExtrasRoutes.chapters(mediaAssetId)).text())
            .toTvExtrasList { tvPlaybackChapter() }

    override suspend fun trickplay(mediaAssetId: Long): TvV1Trickplay =
        JSONObject(read("GET", TvV1PlaybackExtrasRoutes.trickplay(mediaAssetId)).text()).tvTrickplay()

    override suspend fun trickplaySheet(mediaAssetId: Long, sheetIndex: Int): ByteArray {
        val response = read("GET", TvV1PlaybackExtrasRoutes.trickplaySheet(mediaAssetId, sheetIndex), limit = 12 * 1024 * 1024)
        require(response.mediaType == "image/jpeg") { "Unexpected trickplay sheet content type." }
        return response.body
    }

    override suspend fun tracks(mediaAssetId: Long): List<TvV1PlaybackTrack> =
        JSONArray(read("GET", TvV1PlaybackExtrasRoutes.tracks(mediaAssetId)).text())
            .toTvExtrasList { tvPlaybackTrack() }

    override suspend fun subtitleVtt(mediaAssetId: Long, key: String): String {
        val response = read("GET", TvV1PlaybackExtrasRoutes.subtitle(mediaAssetId, key), limit = 12 * 1024 * 1024)
        require(response.mediaType == "text/vtt") { "Unexpected subtitle content type." }
        return response.text()
    }

    override suspend fun preferences(): TvV1PlaybackPreferences =
        JSONObject(read("GET", TvV1PlaybackExtrasRoutes.Preferences).text()).tvPlaybackPreferences()

    override suspend fun savePreferences(update: TvV1PlaybackPreferencesUpdate): Boolean =
        JSONObject(read("PUT", TvV1PlaybackExtrasRoutes.Preferences, update.toJson()).text())
            .getBoolean("success")

    override suspend fun saveSpeed(speed: BigDecimal): Boolean {
        require(speed >= BigDecimal("0.25") && speed <= BigDecimal("4") && speed.scale() <= 2)
        return JSONObject(
            read("PUT", TvV1PlaybackExtrasRoutes.Speed, JSONObject().put("speed", speed)).text(),
        ).getBoolean("success")
    }

    private data class Answer(val body: ByteArray, val mediaType: String?) {
        fun text(): String = body.toString(Charsets.UTF_8)
    }

    /**
     * A stale 401 cannot log out a newer profile/rotation, and an old successful response
     * cannot roll the token back. Retry a stale unauthorized response once with the new token.
     * Do not follow redirects: the Authorization header must never reach another origin.
     */
    private suspend fun read(
        method: String,
        path: String,
        json: JSONObject? = null,
        limit: Int = 4 * 1024 * 1024,
    ): Answer = withContext(Dispatchers.IO) {
        runInterruptible {
            check(method == "GET" || method == "PUT")
            var answered: Answer? = null
            for (attempt in 0..1) {
                val sent = tokens.current
                check(!sent.isNullOrBlank()) { "TV is not paired with a Jularr server." }
                val conn = (serverOrigin.resolve(path).toURL().openConnection() as HttpURLConnection).apply {
                    requestMethod = method
                    instanceFollowRedirects = false
                    connectTimeout = 15_000
                    readTimeout = 30_000
                    setRequestProperty("Authorization", "Bearer $sent")
                    setRequestProperty("Accept", if (path.endsWith("/trickplay") || path.contains("/trickplay/")) "image/jpeg, application/json" else "application/json, text/vtt")
                    if (json != null) {
                        doOutput = true
                        setRequestProperty("Content-Type", "application/json")
                    }
                }
                try {
                    json?.let { payload ->
                        conn.outputStream.use { it.write(payload.toString().toByteArray(Charsets.UTF_8)) }
                    }
                    val status = conn.responseCode
                    val bytes = (if (status in 200..299) conn.inputStream else conn.errorStream)
                        ?.use { it.readBounded(limit) } ?: ByteArray(0)
                    val replaced = synchronized(tokens) {
                        val newer = tokens.current != sent
                        if (!newer) {
                            if (status == HttpURLConnection.HTTP_UNAUTHORIZED) {
                                tokens.clear()
                            } else if (status in 200..299) {
                                tokens.update(conn.getHeaderField(AppApiRoutes.DeviceTokenHeader))
                            }
                        }
                        newer
                    }
                    if (status == HttpURLConnection.HTTP_UNAUTHORIZED && replaced && attempt == 0 && tokens.current != null) {
                        continue
                    }
                    if (status !in 200..299) throw appApiException(status, bytes.toString(Charsets.UTF_8))
                    answered = Answer(bytes, conn.contentType?.substringBefore(';')?.trim()?.lowercase())
                    break
                } finally {
                    conn.disconnect()
                }
            }
            checkNotNull(answered) { "Playback extras request did not complete." }
        }
    }

    private fun InputStream.readBounded(limit: Int): ByteArray {
        val result = ByteArrayOutputStream()
        val buffer = ByteArray(8192)
        while (true) {
            val count = read(buffer)
            if (count == -1) break
            if (count == 0) continue
            check(result.size().toLong() + count <= limit) { "Playback extras response exceeds size limit." }
            result.write(buffer, 0, count)
        }
        return result.toByteArray()
    }
}
