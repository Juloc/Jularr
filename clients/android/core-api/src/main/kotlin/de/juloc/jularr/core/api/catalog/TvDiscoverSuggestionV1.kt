package de.juloc.jularr.core.api.catalog

/** Discovery suggestions can identify external catalog entries without a local Work. */
data class TvDiscoverSuggestionV1(
    val scopeSlug: String,
    val namespace: String,
    val externalId: String,
    val title: String,
    val releaseYear: Int?,
    val posterUrl: String?,
    val workId: Long?,
)
