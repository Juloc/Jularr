package de.juloc.jularr.core.api.catalog

import org.json.JSONObject

internal fun JSONObject.discoverCatalogEntry(): TvDiscoverCatalogEntryV1 = TvDiscoverCatalogEntryV1(
    title = getString("title"),
    releaseYear = nullableInt("releaseYear"),
    overview = nullableString("overview"),
    posterUrl = nullableString("posterUrl"),
    backdropUrl = nullableString("backdropUrl"),
    trailerKey = nullableString("trailerKey"),
    workId = nullableLong("workId"),
    author = nullableString("author"),
)
