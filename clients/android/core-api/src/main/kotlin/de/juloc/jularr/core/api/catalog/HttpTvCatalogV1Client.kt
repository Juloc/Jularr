package de.juloc.jularr.core.api.catalog

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
import java.net.URL

/**
 * Independent client for the catalog and Work endpoints of clean-start.
 * Do not use this class with cookie-login. Supply the SAME DeviceTokenStore as the
 * pairing/profile-switch client, including the app's Keystore-backed persistence callback.
 *
 * No server-side data is synthesized: bad/missing required fields throw, and absent
 * Work/MediaAsset mappings remain null.
 */
class HttpTvCatalogV1Client(
    origin: String,
    private val deviceTokens: DeviceTokenStore,
) : TvCatalogV1Client {
    private val originUri: URI = normalizeOrigin(origin)

    override suspend fun library(page: Int, pageSize: Int, query: String?, sortTypeId: Int?): TvPageV1<TvWorkCardV1> =
        readObject(TvCatalogV1Routes.library(page, pageSize, query, sortTypeId)).page { workCard() }

    override suspend fun continueWatching(): List<TvContinueItemV1> =
        readArray(TvCatalogV1Routes.Continue).objects { continueItem() }

    override suspend fun watchlist(page: Int, pageSize: Int, sortTypeId: Int?): TvPageV1<TvWorkCardV1> =
        readObject(TvCatalogV1Routes.watchlist(page, pageSize, sortTypeId)).page { workCard() }

    override suspend fun setWatchlisted(workId: Long, isWatchlisted: Boolean): Boolean =
        readObject(TvCatalogV1Routes.watchlistItem(workId), "PUT", JSONObject().put("isWatchlisted", isWatchlisted))
            .getBoolean("success")

    override suspend fun homeLayout(): List<TvHomeShelfV1> =
        readArray(TvCatalogV1Routes.HomeLayout).objects { homeShelf() }

    override suspend fun discoverScopes(): List<TvDiscoverScopeV1> =
        readArray(TvCatalogV1Routes.DiscoverScopes).objects { discoverScope() }

    override suspend fun discoverShelves(scopeSlug: String?): List<TvDiscoverShelfV1> =
        readArray(
            TvCatalogV1Routes.DiscoverShelves,
            "POST",
            JSONObject().apply { if (scopeSlug != null) put("scopeSlug", scopeSlug) },
        ).objects { discoverShelf() }

    override suspend fun discoverFeed(mediaTypeId: Int, feedTypeId: Int, page: Int): List<TvDiscoverItemV1> {
        require(mediaTypeId > 0 && feedTypeId > 0 && page > 0)
        return readArray(
            TvCatalogV1Routes.DiscoverFeed,
            "POST",
            JSONObject().put("mediaTypeId", mediaTypeId).put("discoverFeedTypeId", feedTypeId).put("page", page),
        ).objects { discoverItem() }
    }

    override suspend fun discoverSearch(query: String, mediaTypeId: Int?, page: Int): List<TvDiscoverItemV1> {
        require(query.isNotBlank() && page > 0)
        require(mediaTypeId == null || mediaTypeId > 0)
        return readArray(
            TvCatalogV1Routes.DiscoverSearch,
            "POST",
            JSONObject().put("query", query).put("page", page).apply {
                if (mediaTypeId != null) put("mediaTypeId", mediaTypeId)
            },
        ).objects { discoverItem() }
    }

    override suspend fun work(workId: Long): TvWorkDetailsV1 =
        readObject(TvCatalogV1Routes.work(workId)).workDetails()

    override suspend fun seasons(workId: Long): List<TvWorkSeasonV1> =
        readArray(TvCatalogV1Routes.seasons(workId)).objects { workSeason() }

    override suspend fun episodes(workId: Long, seasonNumber: Int): List<TvWorkEpisodeV1> =
        readArray(TvCatalogV1Routes.episodes(workId, seasonNumber)).objects { workEpisode() }

    override suspend fun availability(workId: Long, page: Int, pageSize: Int): TvPageV1<TvWorkAvailabilityV1> =
        readObject(TvCatalogV1Routes.availability(workId, page, pageSize)).page { workAvailability() }

    private suspend fun readArray(route: String, method: String = "GET", body: JSONObject? = null): JSONArray =
        JSONArray(request(method, route, body))

    private suspend fun readObject(route: String, method: String = "GET", body: JSONObject? = null): JSONObject =
        JSONObject(request(method, route, body))

    private suspend fun request(method: String, route: String, body: JSONObject?): String = withContext(Dispatchers.IO) {
        runInterruptible {
            val connection = (URL(originUri.toString() + route).openConnection() as HttpURLConnection).apply {
                requestMethod = method
                connectTimeout = 15_000
                readTimeout = 30_000
                setRequestProperty("Accept", "application/json")
                deviceTokens.current?.let { setRequestProperty("Authorization", "Bearer $it") }
                if (body != null) {
                    doOutput = true
                    setRequestProperty("Content-Type", "application/json")
                }
            }
            try {
                body?.let { json ->
                    connection.outputStream.use { it.write(json.toString().toByteArray(Charsets.UTF_8)) }
                }
                val status = connection.responseCode
                val payload = (if (status in 200..299) connection.inputStream else connection.errorStream)
                    ?.bufferedReader(Charsets.UTF_8)?.use { it.readText() }.orEmpty()
                if (status == HttpURLConnection.HTTP_UNAUTHORIZED) {
                    deviceTokens.clear()
                } else if (status in 200..299) {
                    deviceTokens.update(connection.getHeaderField(AppApiRoutes.DeviceTokenHeader))
                }
                if (status !in 200..299) throw appApiException(status, payload)
                payload
            } finally {
                connection.disconnect()
            }
        }
    }

    private companion object {
        fun normalizeOrigin(origin: String): URI {
            val uri = URI(origin.trim().trimEnd('/'))
            require(uri.scheme == "http" || uri.scheme == "https")
            require(!uri.host.isNullOrEmpty() && uri.rawUserInfo == null && uri.rawQuery == null && uri.rawFragment == null)
            require(uri.rawPath.isNullOrEmpty() || uri.rawPath == "/")
            return uri
        }
    }
}
