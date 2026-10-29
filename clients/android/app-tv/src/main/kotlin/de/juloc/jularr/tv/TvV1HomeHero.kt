package de.juloc.jularr.tv

import de.juloc.jularr.core.api.catalog.TvDiscoverCatalogItemV1
import de.juloc.jularr.core.api.catalog.TvDiscoverItemV1

/**
 * One featured card from the actual ranked clean-start Discover results.
 * Every card opens its Work by workId.
 */
internal data class TvV1HomeHero(
    val key: String,
    val title: String,
    val releaseYear: Int?,
    val subtitle: String,
    val imageUrl: String?,
    val overview: String?,
    val discoverItem: TvDiscoverItemV1? = null,
    val catalogFeed: TvV1HomeVideoFeed? = null,
    val catalogItem: TvDiscoverCatalogItemV1? = null,
)

/** Limit and interleave categories, rather than selecting eight items from one shelf. */
internal fun tvV1HomeHeroCandidates(
    feeds: List<TvV1HomeVideoFeed>,
    maxItems: Int = 8,
): List<TvV1HomeHero> {
    require(maxItems in 1..16)
    val ranked = feeds.sortedWith(
        compareBy<TvV1HomeVideoFeed> {
            when (it.feedTypeId) {
                1 -> 0 // trending
                3 -> 1 // new
                2 -> 2 // top
                4 -> 3 // upcoming
                else -> 4
            }
        }.thenBy { it.mediaTypeId }.thenBy { it.scopeSlug },
    )
    val lists = ranked.map { feed ->
        if (feed.isCatalog) feed.catalogItems.mapNotNull { item ->
            if (item.title.isBlank() || item.posterUrl.isNullOrBlank()) return@mapNotNull null
            val identity = "catalog:${item.namespace}:${item.externalId}"
            TvV1HomeHero(
                key = identity, title = item.title, releaseYear = item.releaseYear,
                subtitle = tvV1HomeFeedTitle(feed),
                imageUrl = item.posterUrl, overview = null,
                catalogFeed = feed, catalogItem = item,
            )
        } else feed.items.mapNotNull { item ->
            if (item.title.isBlank() ||
                (item.backdropUrl.isNullOrBlank() && item.posterUrl.isNullOrBlank())
            ) return@mapNotNull null
            val identity = "work:${item.workId}"
            TvV1HomeHero(
                key = identity, title = item.title, releaseYear = item.releaseYear,
                subtitle = tvV1HomeFeedTitle(feed),
                imageUrl = item.backdropUrl ?: item.posterUrl,
                overview = item.overview, discoverItem = item,
            )
        }
    }
    val result = mutableListOf<TvV1HomeHero>()
    val used = mutableSetOf<String>()
    val maxDepth = lists.maxOfOrNull { it.size } ?: 0
    for (position in 0 until maxDepth) {
        for (list in lists) {
            val candidate = list.getOrNull(position) ?: continue
            if (used.add(candidate.key)) result.add(candidate)
            if (result.size >= maxItems) return result
        }
    }
    return result
}
