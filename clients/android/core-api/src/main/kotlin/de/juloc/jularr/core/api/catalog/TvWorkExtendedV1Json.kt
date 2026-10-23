package de.juloc.jularr.core.api.catalog

import org.json.JSONObject

internal fun JSONObject.workEdition(): TvWorkEditionV1 = TvWorkEditionV1(
    id = getLong("id"),
    name = nullableString("name"),
    title = nullableString("title"),
    languageCode = nullableString("languageCode"),
    editionFormatId = nullableInt("editionFormatId"),
    publisher = nullableString("publisher"),
    releaseYear = nullableInt("releaseYear"),
    fileCount = getLong("fileCount"),
    isAvailable = getBoolean("isAvailable"),
)

internal fun JSONObject.workCredit(): TvWorkCreditV1 = TvWorkCreditV1(
    personId = getLong("personId"),
    name = getString("name"),
    personRoleId = getInt("personRoleId"),
    credit = nullableString("credit"),
    imageUrl = nullableString("imageUrl"),
)

internal fun JSONObject.person(): TvPersonV1 = TvPersonV1(
    id = getLong("id"),
    name = getString("name"),
    imageUrl = nullableString("imageUrl"),
)
