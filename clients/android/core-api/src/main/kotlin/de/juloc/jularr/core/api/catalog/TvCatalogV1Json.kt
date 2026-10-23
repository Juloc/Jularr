package de.juloc.jularr.core.api.catalog

import org.json.JSONArray
import org.json.JSONObject
import java.math.BigDecimal

/*
 * Wire formats verified against clean-start:
 * PageResult<T> -> {"items":[...],"page":1,"pageSize":48}
 * BaseDataList<T> -> a bare JSON array.
 * Backend uses ASP.NET camel-case JSON names, numeric enum IDs and nullable fields.
 * Required fields use get* so a server contract change is visible, not silently hidden.
 */
internal fun JSONObject.nullableString(key: String): String? =
    if (isNull(key)) null else getString(key)

internal fun JSONObject.nullableLong(key: String): Long? =
    if (isNull(key)) null else getLong(key)

internal fun JSONObject.nullableInt(key: String): Int? =
    if (isNull(key)) null else getInt(key)

internal fun JSONObject.nullableDouble(key: String): Double? =
    if (isNull(key)) null else getDouble(key)

internal fun JSONObject.nullableDecimal(key: String): BigDecimal? =
    if (isNull(key)) null else BigDecimal(get(key).toString())

internal fun JSONObject.stringArray(key: String): List<String> =
    getJSONArray(key).read { getString(it) }

internal fun JSONObject.intArray(key: String): List<Int> =
    getJSONArray(key).read { getInt(it) }

internal inline fun <T> JSONArray.read(parse: JSONArray.(Int) -> T): List<T> =
    (0 until length()).map { parse(it) }

internal inline fun <T> JSONArray.objects(parse: JSONObject.() -> T): List<T> =
    read { getJSONObject(it).parse() }

internal inline fun <T> JSONObject.page(parse: JSONObject.() -> T): TvPageV1<T> =
    TvPageV1(getJSONArray("items").objects(parse), getInt("page"), getInt("pageSize"))

internal fun JSONObject.collection(): TvCollectionV1 = TvCollectionV1(
    id = getLong("id"),
    name = getString("name"),
    isSmart = getBoolean("isSmart"),
    workCount = nullableInt("workCount"),
    posterUrls = stringArray("posterUrls"),
    version = getLong("version"),
)

internal fun JSONObject.workCard(): TvWorkCardV1 = TvWorkCardV1(
    id = getLong("id"),
    mediaTypeId = getInt("mediaTypeId"),
    mediaSubtypeId = nullableInt("mediaSubtypeId"),
    title = getString("title"),
    releaseYear = nullableInt("releaseYear"),
    posterUrl = nullableString("posterUrl"),
    rating = nullableDouble("rating"),
    mediaStateTypeId = getInt("mediaStateTypeId"),
    isWatchlisted = getBoolean("isWatchlisted"),
    availabilityStateId = getInt("availabilityStateId"),
    availableUnits = getInt("availableUnits"),
    monitoredUnits = getInt("monitoredUnits"),
    audioLanguages = stringArray("audioLanguages"),
    subtitleLanguages = stringArray("subtitleLanguages"),
    progressStateId = getInt("progressStateId"),
    progressPercent = getInt("progressPercent"),
)

internal fun JSONObject.continueItem(): TvContinueItemV1 = TvContinueItemV1(
    workId = getLong("workId"),
    mediaAssetId = getLong("mediaAssetId"),
    mediaAssetTypeId = getInt("mediaAssetTypeId"),
    mediaTypeId = getInt("mediaTypeId"),
    mediaSubtypeId = nullableInt("mediaSubtypeId"),
    title = getString("title"),
    releaseYear = nullableInt("releaseYear"),
    posterUrl = nullableString("posterUrl"),
    workUnitTypeId = nullableInt("workUnitTypeId"),
    unitNumber = nullableDecimal("unitNumber"),
    parentUnitNumber = nullableDecimal("parentUnitNumber"),
    positionMs = getLong("positionMs"),
    durationMs = nullableLong("durationMs"),
    progression = nullableDecimal("progression"),
)

internal fun JSONObject.workDetails(): TvWorkDetailsV1 = TvWorkDetailsV1(
    id = getLong("id"),
    mediaTypeId = getInt("mediaTypeId"),
    mediaSubtypeId = nullableInt("mediaSubtypeId"),
    canonicalTitle = getString("canonicalTitle"),
    isWatchlisted = getBoolean("isWatchlisted"),
    releaseYear = nullableInt("releaseYear"),
    overview = nullableString("overview"),
    posterUrl = nullableString("posterUrl"),
    backdropUrl = nullableString("backdropUrl"),
    plexUrl = nullableString("plexUrl"),
    platforms = stringArray("platforms"),
    franchiseId = nullableLong("franchiseId"),
    franchiseName = nullableString("franchiseName"),
    isRequestable = getBoolean("isRequestable"),
    requestStatusTypeId = nullableInt("requestStatusTypeId"),
    runtimeMinutes = nullableInt("runtimeMinutes"),
    genres = nullableString("genres"),
    trailerKey = nullableString("trailerKey"),
    studio = nullableString("studio"),
    availabilityStateId = getInt("availabilityStateId"),
    availableUnits = getInt("availableUnits"),
    monitoredUnits = getInt("monitoredUnits"),
    progressStateId = getInt("progressStateId"),
    progressPercent = getInt("progressPercent"),
    rating = nullableDouble("rating"),
    ratingSource = nullableString("ratingSource"),
    originalTitle = nullableString("originalTitle"),
    releasedOn = nullableString("releasedOn"),
    originalLanguage = nullableString("originalLanguage"),
    franchisePosition = nullableDecimal("franchisePosition"),
)

internal fun JSONObject.workSeason(): TvWorkSeasonV1 = TvWorkSeasonV1(
    id = getLong("id"),
    seasonNumber = getInt("seasonNumber"),
    title = nullableString("title"),
    imageUrl = nullableString("imageUrl"),
    releaseYear = nullableInt("releaseYear"),
    episodeCount = getInt("episodeCount"),
    availableCount = getInt("availableCount"),
    watchedCount = getInt("watchedCount"),
)

internal fun JSONObject.workEpisode(): TvWorkEpisodeV1 = TvWorkEpisodeV1(
    id = getLong("id"),
    seasonNumber = getInt("seasonNumber"),
    number = BigDecimal(get("number").toString()),
    title = nullableString("title"),
    overview = nullableString("overview"),
    imageUrl = nullableString("imageUrl"),
    runtimeMinutes = nullableInt("runtimeMinutes"),
    releasedOn = nullableString("releasedOn"),
    mediaAssetId = nullableLong("mediaAssetId"),
    resumePositionMs = nullableLong("resumePositionMs"),
    durationMs = nullableLong("durationMs"),
    isCompleted = getBoolean("isCompleted"),
    mediaStateTypeId = getInt("mediaStateTypeId"),
    isUpcoming = getBoolean("isUpcoming"),
    groupName = nullableString("groupName"),
)

internal fun JSONObject.workAvailability(): TvWorkAvailabilityV1 = TvWorkAvailabilityV1(
    id = getLong("id"),
    workVersionId = getLong("workVersionId"),
    workEditionId = getLong("workEditionId"),
    workUnitId = nullableLong("workUnitId"),
    mediaAssetTypeId = getInt("mediaAssetTypeId"),
    workUnitTypeId = nullableInt("workUnitTypeId"),
    unitNumber = nullableDecimal("unitNumber"),
    parentUnitNumber = nullableDecimal("parentUnitNumber"),
    fileCount = getLong("fileCount"),
    sizeBytes = getLong("sizeBytes"),
    durationMs = nullableLong("durationMs"),
    isAvailable = getBoolean("isAvailable"),
    height = nullableInt("height"),
    audioLanguages = stringArray("audioLanguages"),
    subtitleLanguages = stringArray("subtitleLanguages"),
    resumePositionMs = nullableLong("resumePositionMs"),
    isCompleted = getBoolean("isCompleted"),
    isEmulated = getBoolean("isEmulated"),
    groupName = nullableString("groupName"),
    unitTitle = nullableString("unitTitle"),
    hasProgress = getBoolean("hasProgress"),
    platformName = nullableString("platformName"),
    unitImageUrl = nullableString("unitImageUrl"),
    discNumber = nullableInt("discNumber"),
)

internal fun JSONObject.homeShelf(): TvHomeShelfV1 =
    TvHomeShelfV1(getInt("homeShelfTypeId"), getBoolean("isHidden"))

internal fun JSONObject.discoverScope(): TvDiscoverScopeV1 = TvDiscoverScopeV1(
    slug = getString("slug"),
    mediaTypeId = getInt("mediaTypeId"),
    mediaSubtypeId = nullableInt("mediaSubtypeId"),
    isCatalog = getBoolean("isCatalog"),
    feeds = intArray("feeds"),
)

internal fun JSONObject.discoverItem(): TvDiscoverItemV1 = TvDiscoverItemV1(
    mediaTypeId = getInt("mediaTypeId"),
    tmdbId = nullableInt("tmdbId"),
    workId = nullableLong("workId"),
    title = getString("title"),
    releaseYear = nullableInt("releaseYear"),
    overview = nullableString("overview"),
    posterUrl = nullableString("posterUrl"),
    backdropUrl = nullableString("backdropUrl"),
    mediaStateTypeId = getInt("mediaStateTypeId"),
    plexUrl = nullableString("plexUrl"),
    rating = nullableDouble("rating"),
    trailerKey = nullableString("trailerKey"),
)

internal fun JSONObject.discoverCatalogItem(): TvDiscoverCatalogItemV1 = TvDiscoverCatalogItemV1(
    namespace = getString("namespace"),
    externalId = getString("externalId"),
    title = getString("title"),
    releaseYear = nullableInt("releaseYear"),
    author = nullableString("author"),
    workId = nullableLong("workId"),
    posterUrl = nullableString("posterUrl"),
    rating = nullableDouble("rating"),
    mediaStateTypeId = nullableInt("mediaStateTypeId"),
)

internal fun JSONObject.discoverShelf(): TvDiscoverShelfV1 = TvDiscoverShelfV1(
    scopeSlug = getString("scopeSlug"),
    discoverFeedTypeId = getInt("discoverFeedTypeId"),
    items = getJSONArray("items").objects { discoverItem() },
    catalogItems = getJSONArray("catalogItems").objects { discoverCatalogItem() },
)
