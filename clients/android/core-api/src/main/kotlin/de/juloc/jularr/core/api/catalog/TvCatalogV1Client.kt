package de.juloc.jularr.core.api.catalog

/**
 * Read-only catalog is intentionally separate from the parallel agent's AppApiClient and TV UI.
 * Caller provides the same DeviceTokenStore used by pairing, so profile/token rotation is shared.
 */
interface TvCatalogV1Client {
    suspend fun library(page: Int = 1, pageSize: Int = 48, query: String? = null, sortTypeId: Int? = null): TvPageV1<TvWorkCardV1>
    suspend fun continueWatching(): List<TvContinueItemV1>
    suspend fun watchlist(page: Int = 1, pageSize: Int = 25, sortTypeId: Int? = null): TvPageV1<TvWorkCardV1>
    suspend fun setWatchlisted(workId: Long, isWatchlisted: Boolean): Boolean
    suspend fun homeLayout(): List<TvHomeShelfV1>
    suspend fun discoverScopes(): List<TvDiscoverScopeV1>
    suspend fun discoverShelves(scopeSlug: String? = null): List<TvDiscoverShelfV1>
    suspend fun discoverFeed(mediaTypeId: Int, feedTypeId: Int, page: Int = 1): List<TvDiscoverItemV1>
    suspend fun discoverSearch(query: String, mediaTypeId: Int? = null, page: Int = 1): List<TvDiscoverItemV1>
    suspend fun discoverCatalogSearch(mediaTypeId: Int, query: String, mediaSubtypeId: Int? = null): List<TvDiscoverCatalogItemV1>
    suspend fun discoverCatalogFeed(mediaTypeId: Int, feedTypeId: Int, mediaSubtypeId: Int? = null): List<TvDiscoverCatalogItemV1>
    suspend fun discoverSuggestions(query: String, scopeSlug: String? = null): List<TvDiscoverSuggestionV1>
    /** Read provider item details without pretending a TMDB/external id is a local WorkId. */
    suspend fun discoverTmdbItem(mediaTypeId: Int, tmdbId: Int): TvDiscoverItemV1
    suspend fun discoverCatalogEntry(mediaTypeId: Int, namespace: String, externalId: String, mediaSubtypeId: Int? = null): TvDiscoverCatalogEntryV1
    suspend fun work(workId: Long): TvWorkDetailsV1
    suspend fun seasons(workId: Long): List<TvWorkSeasonV1>
    suspend fun episodes(workId: Long, seasonNumber: Int): List<TvWorkEpisodeV1>
    suspend fun availability(workId: Long, page: Int = 1, pageSize: Int = 25): TvPageV1<TvWorkAvailabilityV1>
}
