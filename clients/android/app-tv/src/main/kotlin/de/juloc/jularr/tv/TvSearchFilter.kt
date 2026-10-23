package de.juloc.jularr.tv

import de.juloc.jularr.core.model.AnimeSummary

/**
 * Home's search field opens Search, which browses the already-loaded library (#522: "reuse
 * the existing search screen"). The v1 client API has no dedicated search endpoint
 * (docs/ANDROID_CLIENTS.md §4), so this filters the library the app already fetched
 * instead of inventing a new server round trip for a query that is, today, just a
 * substring match over titles the client already has in memory.
 */
object TvSearchFilter {
    fun matches(
        library: List<AnimeSummary>,
        query: String,
        category: TvContentFilter = TvContentFilter.ALL,
    ): List<AnimeSummary> {
        val needle = query.trim()
        return library.filter { anime ->
            val isMovie = anime.format.equals("MOVIE", ignoreCase = true)
            val matchesType = when (category) {
                TvContentFilter.ALL, TvContentFilter.ANIME -> true
                TvContentFilter.SERIES -> !isMovie
                TvContentFilter.MOVIES -> isMovie
            }
            matchesType && (
                needle.isBlank() ||
                    anime.title.contains(needle, ignoreCase = true) ||
                    anime.localTitle.contains(needle, ignoreCase = true) ||
                    anime.nativeTitle?.contains(needle, ignoreCase = true) == true
                )
        }
    }
}
