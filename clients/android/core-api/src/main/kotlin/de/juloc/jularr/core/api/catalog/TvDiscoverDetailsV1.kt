package de.juloc.jularr.core.api.catalog

/**
 * Detailed catalog entry supplied by /api/v1/discover/catalog/entry.
 * A provider-only item may legitimately have no local WorkId.
 */
data class TvDiscoverCatalogEntryV1(
    val title: String,
    val releaseYear: Int?,
    val overview: String?,
    val posterUrl: String?,
    val backdropUrl: String?,
    val trailerKey: String?,
    val workId: Long?,
    val author: String?,
)
