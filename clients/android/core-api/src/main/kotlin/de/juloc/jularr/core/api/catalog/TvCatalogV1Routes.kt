package de.juloc.jularr.core.api.catalog

import java.net.URLEncoder
import java.nio.charset.StandardCharsets

/** Catalog endpoints verified against the service attributes in clean-start. */
object TvCatalogV1Routes {
    private const val base = "/api/v1"
    const val Library = "$base/library"
    const val Continue = "$base/continue"
    const val Watchlist = "$base/watchlist"
    const val HomeLayout = "$base/home/layout"
    const val DiscoverScopes = "$base/discover/scopes"
    const val DiscoverShelves = "$base/discover/shelves"
    const val DiscoverFeed = "$base/discover/feed"
    const val DiscoverSearch = "$base/discover/search"

    fun work(workId: Long): String = "$base/works/${positiveId(workId)}"
    fun seasons(workId: Long): String = "${work(workId)}/seasons"
    fun episodes(workId: Long, seasonNumber: Int): String {
        require(seasonNumber >= 0) { "Season number cannot be negative." }
        return "${seasons(workId)}/$seasonNumber/episodes"
    }
    fun availability(workId: Long, page: Int, pageSize: Int): String =
        "${work(workId)}/availability${pageQuery(page, pageSize)}"
    fun watchlistItem(workId: Long): String = "$Watchlist/${positiveId(workId)}"

    fun library(page: Int, pageSize: Int, query: String? = null, sortTypeId: Int? = null): String =
        Library + pageQuery(page, pageSize) + optionalQuery("query", query) +
            optionalNumber("librarySortTypeId", sortTypeId)

    fun watchlist(page: Int, pageSize: Int, sortTypeId: Int? = null): String =
        Watchlist + pageQuery(page, pageSize) + optionalNumber("librarySortTypeId", sortTypeId)

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
