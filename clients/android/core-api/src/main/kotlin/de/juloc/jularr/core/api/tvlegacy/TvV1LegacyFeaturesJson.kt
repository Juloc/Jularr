package de.juloc.jularr.core.api.tvlegacy

import org.json.JSONArray
import org.json.JSONObject
import java.math.BigDecimal

internal inline fun <T> JSONArray.tvItems(parse: JSONObject.() -> T): List<T> =
    (0 until length()).map { getJSONObject(it).parse() }

internal fun JSONObject.tvProfileStatistics(): TvV1ProfileStatistics = TvV1ProfileStatistics(
    watchedMs = getLong("watchedMs"),
    sessions = getLong("sessions"),
    gamePlayedMs = getLong("gamePlayedMs"),
    completed = getJSONArray("completed").tvItems {
        TvV1CompletedByMediaType(getInt("mediaTypeId"), getLong("count"))
    },
    topWorks = getJSONArray("topWorks").tvItems {
        TvV1TopWorkTime(getLong("workId"), getString("title"), getLong("durationMs"))
    },
)

private fun JSONObject.optionalLong(name: String): Long? =
    if (isNull(name)) null else getLong(name)

private fun JSONObject.optionalInt(name: String): Int? =
    if (isNull(name)) null else getInt(name)

private fun JSONObject.optionalDecimal(name: String): BigDecimal? =
    if (isNull(name)) null else BigDecimal(get(name).toString())

private fun JSONObject.optionalString(name: String): String? =
    if (isNull(name)) null else getString(name)

internal fun JSONObject.tvPlaybackAssetInfo(): TvV1PlaybackAssetInfo = TvV1PlaybackAssetInfo(
    mediaAssetId = getLong("mediaAssetId"),
    workId = getLong("workId"),
    canonicalTitle = getString("canonicalTitle"),
    mediaAssetTypeId = getInt("mediaAssetTypeId"),
    workUnitTypeId = optionalInt("workUnitTypeId"),
    unitNumber = optionalDecimal("unitNumber"),
    parentUnitNumber = optionalDecimal("parentUnitNumber"),
    unitTitle = optionalString("unitTitle"),
    resumePositionMs = optionalLong("resumePositionMs"),
    previousMediaAssetId = optionalLong("previousMediaAssetId"),
    nextMediaAssetId = optionalLong("nextMediaAssetId"),
)
