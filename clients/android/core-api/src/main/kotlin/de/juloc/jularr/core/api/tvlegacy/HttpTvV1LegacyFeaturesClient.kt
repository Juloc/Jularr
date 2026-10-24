package de.juloc.jularr.core.api.tvlegacy

import de.juloc.jularr.core.api.AppApiRoutes
import de.juloc.jularr.core.api.DeviceTokenStore
import de.juloc.jularr.core.api.appApiException
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.runInterruptible
import kotlinx.coroutines.withContext
import org.json.JSONObject
import java.io.ByteArrayOutputStream
import java.io.InputStream
import java.net.HttpURLConnection
import java.net.URI

/**
 * Only two real /api/v1 GET endpoints. Auth is supplied by the same DeviceTokenStore
 * owned by TV pairing; this adapter neither persists nor invents credentials.
 *
 * An in-flight response must not overwrite or invalidate a newer profile/session token.
 * Redirects are not followed so bearer tokens cannot leak to untrusted hosts.
 */
class HttpTvV1LegacyFeaturesClient(
    origin: String,
    private val tokens: DeviceTokenStore,
) : TvV1LegacyFeaturesClient {
    private val serverOrigin: URI = URI(origin.trim().trimEnd('/')).also { uri ->
        require(uri.scheme == "http" || uri.scheme == "https") { "A HTTP(S) server origin is required." }
        require(!uri.host.isNullOrBlank() && uri.rawUserInfo == null && uri.rawQuery == null && uri.rawFragment == null)
        require(uri.rawPath.isNullOrBlank() || uri.rawPath == "/") { "Only server origin is allowed." }
    }

    override suspend fun statistics(days: Int): TvV1ProfileStatistics =
        JSONObject(get(TvV1LegacyFeaturesRoutes.statistics(days))).tvProfileStatistics()

    override suspend fun playbackAsset(mediaAssetId: Long): TvV1PlaybackAssetInfo =
        JSONObject(get(TvV1LegacyFeaturesRoutes.playbackAsset(mediaAssetId))).tvPlaybackAssetInfo()

    private suspend fun get(path: String): String = withContext(Dispatchers.IO) {
        runInterruptible {
            var body: String? = null
            val url = serverOrigin.resolve(path).toURL()
            for (attempt in 0..1) {
                val sentToken = tokens.current
                check(!sentToken.isNullOrBlank()) { "The TV device must be paired." }
                val connection = (url.openConnection() as HttpURLConnection).apply {
                    requestMethod = "GET"
                    connectTimeout = 15_000
                    readTimeout = 30_000
                    instanceFollowRedirects = false
                    setRequestProperty("Accept", "application/json")
                    setRequestProperty("Authorization", "Bearer $sentToken")
                }
                try {
                    val status = connection.responseCode
                    val bytes = (if (status in 200..299) connection.inputStream else connection.errorStream)
                        ?.use { it.readLimited(1024 * 1024) } ?: ByteArray(0)
                    val superseded = synchronized(tokens) {
                        val oldRequest = tokens.current != sentToken
                        if (!oldRequest) {
                            if (status == HttpURLConnection.HTTP_UNAUTHORIZED) tokens.clear()
                            else if (status in 200..299) {
                                tokens.update(connection.getHeaderField(AppApiRoutes.DeviceTokenHeader))
                            }
                        }
                        oldRequest
                    }
                    if (status == HttpURLConnection.HTTP_UNAUTHORIZED && superseded &&
                        attempt == 0 && tokens.current != null
                    ) continue
                    if (status !in 200..299) throw appApiException(status, bytes.toString(Charsets.UTF_8))
                    body = bytes.toString(Charsets.UTF_8)
                    break
                } finally {
                    connection.disconnect()
                }
            }
            checkNotNull(body) { "The V1 response was not completed." }
        }
    }

    private fun InputStream.readLimited(maximum: Int): ByteArray {
        val bytes = ByteArrayOutputStream()
        val buf = ByteArray(8192)
        while (true) {
            val count = read(buf)
            if (count == -1) break
            if (count == 0) continue
            check(bytes.size().toLong() + count <= maximum) { "Oversized V1 response." }
            bytes.write(buf, 0, count)
        }
        return bytes.toByteArray()
    }
}
