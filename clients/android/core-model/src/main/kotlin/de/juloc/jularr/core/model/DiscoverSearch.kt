package de.juloc.jularr.core.model

/**
 * One title the instance suggests while the viewer types: the scope it opens in, its title and
 * its cover. A suggestion without a work is a title the instance would have to ask for.
 */
data class DiscoverSuggestionV1(
    val scopeSlug: String,
    val namespace: String,
    val externalId: String,
    val title: String,
    val releaseYear: Int?,
    val posterUrl: String?,
    val workId: Long?,
)

/**
 * One shelf of the Discover of the instance: the ranked list of a feed (trending, top, new,
 * upcoming) in one scope. The items are the titles of the providers, the catalog items the
 * books, the music and the games.
 */
data class DiscoverShelfV1(
    val scopeSlug: String,
    val discoverFeedTypeId: Int,
    val items: List<DiscoverItemV1>,
    val catalogItems: List<DiscoverCatalogItemV1>,
) {
    companion object {
        const val FeedTrending = 1
        const val FeedTop = 2
        const val FeedNew = 3
        const val FeedUpcoming = 4
    }
}

/** One title of a catalog of the instance: a book, a manga, a track or a game. */
data class DiscoverCatalogItemV1(
    val namespace: String,
    val externalId: String,
    val title: String,
    val releaseYear: Int?,
    val author: String?,
    val workId: Long?,
    val posterUrl: String?,
)

/**
 * The details of a title of the instance: its name, its year, its text, its covers and what
 * the profile may do with it.
 */
data class WorkDetailsV1(
    val id: Long,
    val mediaTypeId: Int,
    val mediaSubtypeId: Int?,
    val canonicalTitle: String,
    val isWatchlisted: Boolean,
    val releaseYear: Int?,
    val overview: String?,
    val posterUrl: String?,
    val backdropUrl: String?,
    val plexUrl: String?,
    val platforms: List<String>,
    val franchiseName: String?,
    val isRequestable: Boolean,
    val requestStatusTypeId: Int?,
    val runtimeMinutes: Int?,
    val genres: String?,
    val trailerKey: String?,
    val studio: String?,
)

/**
 * One title a search answers with: the work of the instance, its title, its cover and the
 * state the profile sees it in.
 */
data class DiscoverItemV1(
    val mediaTypeId: Int,
    val tmdbId: Int?,
    val workId: Long?,
    val title: String,
    val releaseYear: Int?,
    val overview: String?,
    val posterUrl: String?,
    val backdropUrl: String?,
    val mediaStateTypeId: Int,
    val plexUrl: String?,
)
