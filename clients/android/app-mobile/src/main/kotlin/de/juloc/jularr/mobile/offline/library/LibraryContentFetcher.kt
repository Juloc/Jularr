package de.juloc.jularr.mobile.offline.library

import java.io.IOException
import java.net.HttpURLConnection
import java.net.URL

/**
 * Minimal raw-bytes `GET`, used for content-addressed cover/illustration
 * assets. Unlike episode media (`OfflineMediaDownloader`, #341) these files
 * are bounded-size (covers/illustrations, not video), so there is no
 * Range/resume machinery here: a failed fetch is simply retried whole on the
 * next book refresh.
 */
object LibraryContentFetcher {
    fun get(
        url: URL,
        headers: Map<String, String>,
        openConnection: (URL) -> HttpURLConnection = { it.openConnection() as HttpURLConnection },
    ): ByteArray? {
        val connection = openConnection(url).apply {
            requestMethod = "GET"
            connectTimeout = 15_000
            readTimeout = 30_000
            instanceFollowRedirects = false
            useCaches = false
            setRequestProperty("X-Jularr-Client", "android")
            headers.forEach { (name, value) ->
                if (name.isNotBlank() && value.isNotBlank()) {
                    setRequestProperty(name, value)
                }
            }
        }

        return try {
            val status = connection.responseCode
            if (status !in 200..299) {
                null
            } else {
                connection.inputStream.use { it.readBytes() }
            }
        } catch (_: IOException) {
            null
        } finally {
            connection.disconnect()
        }
    }
}
