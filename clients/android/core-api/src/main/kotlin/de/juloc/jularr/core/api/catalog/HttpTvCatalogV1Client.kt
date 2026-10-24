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

    override suspend fun libraryFiltered(
        page: Int, pageSize: Int, filters: TvCatalogV1Filters, query: String?, sortTypeId: Int?,
    ): TvPageV1<TvWorkCardV1> =
        readObject(TvCatalogV1Routes.libraryFiltered(page, pageSize, filters, query, sortTypeId)).page { workCard() }

    override suspend fun continueWatching(): List<TvContinueItemV1> =
        readArray(TvCatalogV1Routes.Continue).objects { continueItem() }

    override suspend fun watchlist(page: Int, pageSize: Int, sortTypeId: Int?): TvPageV1<TvWorkCardV1> =
        readObject(TvCatalogV1Routes.watchlist(page, pageSize, sortTypeId)).page { workCard() }

    override suspend fun watchlistFiltered(
        page: Int, pageSize: Int, filters: TvCatalogV1Filters, sortTypeId: Int?,
    ): TvPageV1<TvWorkCardV1> =
        readObject(TvCatalogV1Routes.watchlistFiltered(page, pageSize, filters, sortTypeId)).page { workCard() }

    override suspend fun watchlistCount(): Int =
        readObject(TvCatalogV1Routes.WatchlistCount).getInt("total")

    override suspend fun collections(): List<TvCollectionV1> =
        readArray(TvCatalogV1Routes.Collections).objects { collection() }

    override suspend fun collectionWorks(
        collectionId: Long, page: Int, pageSize: Int,
    ): TvPageV1<TvWorkCardV1> =
        readObject(TvCatalogV1Routes.collectionWorks(collectionId, page, pageSize))
            .page { workCard() }

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

    override suspend fun discoverCatalogSearch(
        mediaTypeId: Int, query: String, mediaSubtypeId: Int?,
    ): List<TvDiscoverCatalogItemV1> {
        require(mediaTypeId > 0 && query.isNotBlank())
        require(mediaSubtypeId == null || mediaSubtypeId > 0)
        return readArray(
            TvCatalogV1Routes.DiscoverCatalogSearch,
            "POST",
            JSONObject().put("mediaTypeId", mediaTypeId).put("query", query).apply {
                if (mediaSubtypeId != null) put("mediaSubtypeId", mediaSubtypeId)
            },
        ).objects { discoverCatalogItem() }
    }

    override suspend fun discoverCatalogFeed(
        mediaTypeId: Int, feedTypeId: Int, mediaSubtypeId: Int?,
    ): List<TvDiscoverCatalogItemV1> {
        require(mediaTypeId > 0 && feedTypeId > 0)
        require(mediaSubtypeId == null || mediaSubtypeId > 0)
        return readArray(
            TvCatalogV1Routes.DiscoverCatalogFeed,
            "POST",
            JSONObject().put("mediaTypeId", mediaTypeId).put("discoverFeedTypeId", feedTypeId).apply {
                if (mediaSubtypeId != null) put("mediaSubtypeId", mediaSubtypeId)
            },
        ).objects { discoverCatalogItem() }
    }

    override suspend fun discoverSuggestions(query: String, scopeSlug: String?): List<TvDiscoverSuggestionV1> {
        require(query.isNotBlank())
        return readArray(
            TvCatalogV1Routes.DiscoverSuggestions,
            "POST",
            JSONObject().put("query", query).apply {
                if (scopeSlug != null) put("scopeSlug", scopeSlug)
            },
        ).objects { discoverSuggestion() }
    }

    override suspend fun discoverTmdbItem(mediaTypeId: Int, tmdbId: Int): TvDiscoverItemV1 =
        readObject(TvCatalogV1Routes.discoverTmdbItem(mediaTypeId, tmdbId), "POST", JSONObject())
            .discoverItem()

    override suspend fun discoverCatalogEntry(
        mediaTypeId: Int, namespace: String, externalId: String, mediaSubtypeId: Int?,
    ): TvDiscoverCatalogEntryV1 {
        require(mediaTypeId > 0 && namespace.isNotBlank() && externalId.isNotBlank())
        require(mediaSubtypeId == null || mediaSubtypeId > 0)
        return readObject(
            TvCatalogV1Routes.DiscoverCatalogEntry,
            "POST",
            JSONObject().put("mediaTypeId", mediaTypeId).put("namespace", namespace)
                .put("externalId", externalId).apply {
                    if (mediaSubtypeId != null) put("mediaSubtypeId", mediaSubtypeId)
                },
        ).discoverCatalogEntry()
    }

    override suspend fun requestLocales(): List<TvRequestLocaleV1> =
        readArray(TvCatalogV1Routes.RequestLocales).objects {
            TvRequestLocaleV1(getLong("id"), getString("locale"), getString("name"))
        }

    override suspend fun requestDiscoverTmdb(mediaTypeId: Int, tmdbId: Int, localeId: Long): Long {
        require(localeId > 0)
        return readObject(
            TvCatalogV1Routes.discoverTmdbRequest(mediaTypeId, tmdbId),
            "POST",
            JSONObject().put("uiLocaleId", localeId),
        ).getLong("id")
    }

    override suspend fun requestDiscoverCatalog(
        mediaTypeId: Int, namespace: String, externalId: String, mediaSubtypeId: Int?, localeId: Long,
    ): Long {
        require(mediaTypeId > 0 && namespace.isNotBlank() && externalId.isNotBlank() && localeId > 0)
        return readObject(
            TvCatalogV1Routes.DiscoverCatalogRequest,
            "POST",
            JSONObject().put("mediaTypeId", mediaTypeId)
                .put("namespace", namespace)
                .put("externalId", externalId)
                .put("uiLocaleId", localeId)
                .apply { if (mediaSubtypeId != null) put("mediaSubtypeId", mediaSubtypeId) },
        ).getLong("id")
    }

    override suspend fun work(workId: Long): TvWorkDetailsV1 =
        readObject(TvCatalogV1Routes.work(workId)).workDetails()

    override suspend fun editions(workId: Long): List<TvWorkEditionV1> =
        readArray(TvCatalogV1Routes.editions(workId)).objects { workEdition() }

    override suspend fun credits(workId: Long): List<TvWorkCreditV1> =
        readArray(TvCatalogV1Routes.credits(workId)).objects { workCredit() }

    override suspend fun person(personId: Long): TvPersonV1 =
        readObject(TvCatalogV1Routes.person(personId)).person()

    override suspend fun personWorks(personId: Long, page: Int, pageSize: Int): TvPageV1<TvWorkCardV1> =
        readObject(TvCatalogV1Routes.personWorks(personId, page, pageSize)).page { workCard() }

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
            var answer: String? = null
            for (attempt in 0..1) {
                val requestDeviceToken = deviceTokens.current
                val connection = (URL(originUri.toString() + route).openConnection() as HttpURLConnection).apply {
                    requestMethod = method
                    connectTimeout = 15_000
                    readTimeout = 30_000
                    instanceFollowRedirects = false
                    setRequestProperty("Accept", "application/json")
                    requestDeviceToken?.let { setRequestProperty("Authorization", "Bearer $it") }
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
                    val obsolete = synchronized(deviceTokens) {
                        if (deviceTokens.current != requestDeviceToken) true
                        else {
                            if (status == HttpURLConnection.HTTP_UNAUTHORIZED) deviceTokens.clear()
                            else if (status in 200..299) deviceTokens.update(connection.getHeaderField(AppApiRoutes.DeviceTokenHeader))
                            false
                        }
                    }
                    // A response started under an earlier profile is stale even when
                    // HTTP 200 succeeds. Do not expose its catalog or favourites to
                    // another viewer. Only GET is safely retried; PUT/POST must never
                    // replay after an uncertain profile/account switch.
                    if (obsolete) {
                        if (method == "GET" && attempt == 0 && deviceTokens.current != null) {
                            continue
                        }
                        throw IllegalStateException(
                            "The account/profile changed during the catalog request; refresh the screen.",
                        )
                    }
                    if (status !in 200..299) throw appApiException(status, payload)
                    answer = payload
                    break
                } finally {
                    connection.disconnect()
                }
            }
            checkNotNull(answer) { "Catalog request did not complete." }
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
