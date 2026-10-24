package de.juloc.jularr.core.api.catalog

/**
 * Server-side Library/Watchlist filtering.
 * The backend accepts type and subtype enum IDs as repeated query parameters;
 * an empty selection means all profile-visible categories.
 * A selected media type does not implicitly include its named subtypes.
 */
data class TvCatalogV1Filters(
    val mediaTypeIds: List<Int> = emptyList(),
    val mediaSubtypeIds: List<Int> = emptyList(),
    val releaseYearFrom: Int? = null,
    val releaseYearTo: Int? = null,
    val progressStateId: Int? = null,
) {
    init {
        require(mediaTypeIds.all { it > 0 } && mediaSubtypeIds.all { it > 0 }) {
            "Media type and subtype IDs must be positive."
        }
        require(releaseYearFrom == null || releaseYearFrom > 0)
        require(releaseYearTo == null || releaseYearTo > 0)
        require(releaseYearFrom == null || releaseYearTo == null || releaseYearFrom <= releaseYearTo)
        require(progressStateId == null || progressStateId in 1..3)
    }
}
