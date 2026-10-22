package de.juloc.jularr.core.api.catalog

import org.json.JSONObject

internal fun JSONObject.discoverSuggestion(): TvDiscoverSuggestionV1 = TvDiscoverSuggestionV1(
    scopeSlug = getString("scopeSlug"),
    namespace = getString("namespace"),
    externalId = getString("externalId"),
    title = getString("title"),
    releaseYear = nullableInt("releaseYear"),
    posterUrl = nullableString("posterUrl"),
    workId = nullableLong("workId"),
)
