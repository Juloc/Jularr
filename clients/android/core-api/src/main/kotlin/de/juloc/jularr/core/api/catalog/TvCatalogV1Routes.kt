package de.juloc.jularr.core.api.catalog

import java.net.URLEncoder
import java.nio.charset.StandardCharsets

/** Catalog endpoints verified against the service attributes in clean-start. */
object TvCatalogV1Routes {
    private const val base = "/api/v1"
    const val Library = "$base/library"
    const val Continue = "$base/continue"
    const val Watchlist = "$base/watchlist"
    const val WatchlistCount = "$base/watchlist/count"
    const val Collections = "$base/collections"
    const val HomeLayout = "$base/home/layout"
    const val DiscoverScopes = "$base/discover/scopes"
    const val DiscoverShelves = "$base/discover/shelves"
    const val DiscoverFeed = "$base/discover/feed"
    const val DiscoverSearch = "$base/discover/search"
    const val DiscoverCatalogSearch = "$base/discover/catalog/search"
    const val DiscoverCatalogFeed = "$base/discover/catalog/feed"
    const val DiscoverSuggestions = "$base/discover/suggestions"
    const val DiscoverCatalogEntry = "$base/discover/catalog/entry"
    const val RequestLocales = "$base/localization/locales"
    const val DiscoverCatalogRequest = "$base/discover/catalog/requests"

    fun discoverTmdbRequest(mediaTypeId: Int, tmdbId: Int): String =
        discoverTmdbItem(mediaTypeId, tmdbId) + "/requests"
 

    /** TMDB search result only; catalog results with other namespaces use catalog/entry. */
    fun discoverTmdbItem(mediaTypeId: Int, tmdbId: Int): String {
        require(mediaTypeId == 1 || mediaTypeId == 2) { "TMDB details only support movies or series." }
        require(tmdbId > 0) { "TMDB id must be positive." }
        return "$base/discover/$mediaTypeId/$tmdbId"
    }

    fun work(workId: Long): String = "$base/works/${positiveId(workId)}"
    fun seasons(workId: Long): String = "${work(workId)}/seasons"
    fun editions(workId: Long): String = "${work(workId)}/editions"
    fun credits(workId: Long): String = "${work(workId)}/credits"
    fun person(personId: Long): String = "$base/people/${positiveId(personId)}"
    fun personWorks(personId: Long, page: Int, pageSize: Int): String =
        "${person(personId)}/works${pageQuery(page, pageSize)}"
    fun episodes(workId: Long, seasonNumber: Int): String {
        require(seasonNumber >= 0) { "Season number cannot be negative." }
        return "${seasons(workId)}/$seasonNumber/episodes"
    }
    fun availability(workId: Long, page: Int, pageSize: Int): String =
        "${work(workId)}/availability${pageQuery(page, pageSize)}"
    fun watchlistItem(workId: Long): String = "$Watchlist/${positiveId(workId)}"

    /** Reading collections requires Library + Collections and profile.collections. */
    fun collectionWorks(collectionId: Long, page: Int, pageSize: Int): String =
        "$Collections/${positiveId(collectionId)}/works" + pageQuery(page, pageSize) +
            // The backend requires subtype 1 explicitly for Anime; a mediaType
            // filter alone matches only works without a subtype.
            "&mediaTypeIds=1&mediaTypeIds=2&mediaSubtypeIds=1"

    fun library(page: Int, pageSize: Int, query: String? = null, sortTypeId: Int? = null): String =
        Library + pageQuery(page, pageSize) + optionalQuery("query", query) +
            optionalNumber("librarySortTypeId", sortTypeId)

    fun watchlist(page: Int, pageSize: Int, sortTypeId: Int? = null): String =
        Watchlist + pageQuery(page, pageSize) + optionalNumber("librarySortTypeId", sortTypeId)

    fun libraryFiltered(
        page: Int, pageSize: Int, filters: TvCatalogV1Filters,
        query: String? = null, sortTypeId: Int? = null,
    ): String = library(page, pageSize, query, sortTypeId) + filterQuery(filters, includeProgress = false)

    fun watchlistFiltered(
        page: Int, pageSize: Int, filters: TvCatalogV1Filters, sortTypeId: Int? = null,
    ): String = watchlist(page, pageSize, sortTypeId) + filterQuery(filters, includeProgress = true)

    private fun filterQuery(filters: TvCatalogV1Filters, includeProgress: Boolean): String {
        require(includeProgress || filters.progressStateId == null) {
            "The library endpoint does not support progressStateId. Use watchlist filtering instead."
        }
        return buildString {
        filters.mediaTypeIds.forEach { append("&mediaTypeIds=").append(it) }
        filters.mediaSubtypeIds.forEach { append("&mediaSubtypeIds=").append(it) }
        filters.releaseYearFrom?.let { append("&releaseYearFrom=").append(it) }
        filters.releaseYearTo?.let { append("&releaseYearTo=").append(it) }
        if (includeProgress) filters.progressStateId?.let { append("&progressStateId=").append(it) }
        }
    }

    private fun positiveId(id: Long): Long {
        require(id > 0) { "A server WorkId must be positive." }
        return id
    }

    private fun pageQuery(page: Int, pageSize: Int): String {
        require(page > 0 && pageSize in 1..100 && (page.toLong() - 1) * pageSize <= 100_000L) {
            "Paging exceeds clean-start PageRequest limits."
        }
        return "?page=$page&pageSize=$pageSize"
    }

    private fun optionalQuery(key: String, value: String?): String =
        value?.takeIf { it.isNotBlank() }?.let {
            "&$key=" + URLEncoder.encode(it, StandardCharsets.UTF_8.name())
        } ?: ""

    private fun optionalNumber(key: String, value: Int?): String =
        value?.let {
            require(it > 0) { "A sort enum id must be positive." }
            "&$key=$it"
        } ?: ""
}
