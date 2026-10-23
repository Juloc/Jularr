package de.juloc.jularr.core.api.playbackextras

import org.json.JSONArray
import org.json.JSONObject
import java.math.BigDecimal

/** clean-start serializes BaseDataList<T> as an unwrapped JSON array. */
internal inline fun <T> JSONArray.toTvExtrasList(read: JSONObject.() -> T): List<T> =
    (0 until length()).map { getJSONObject(it).read() }

internal fun JSONObject.tvPlaybackMarker(): TvV1PlaybackMarker = TvV1PlaybackMarker(
    mediaMarkerKindTypeId = getInt("mediaMarkerKindTypeId"),
    startMs = getLong("startMs"),
    endMs = getLong("endMs"),
)

internal fun JSONObject.tvPlaybackChapter(): TvV1PlaybackChapter = TvV1PlaybackChapter(
    chapterIndex = getInt("chapterIndex"),
    startMs = getLong("startMs"),
    endMs = getLong("endMs"),
    title = if (isNull("title")) null else getString("title"),
)

internal fun JSONObject.tvTrickplay(): TvV1Trickplay = TvV1Trickplay(
    intervalSeconds = getInt("intervalSeconds"),
    frames = getInt("frames"),
    columns = getInt("columns"),
    rows = getInt("rows"),
    tileWidth = getInt("tileWidth"),
    tileHeight = getInt("tileHeight"),
)

internal fun JSONObject.tvPlaybackTrack(): TvV1PlaybackTrack = TvV1PlaybackTrack(
    key = getString("key"),
    mediaTrackTypeId = getInt("mediaTrackTypeId"),
    language = if (isNull("language")) null else getString("language"),
    title = if (isNull("title")) null else getString("title"),
    channels = if (isNull("channels")) null else getInt("channels"),
    isDefault = getBoolean("isDefault"),
    isForced = getBoolean("isForced"),
    isSupported = getBoolean("isSupported"),
    isSelected = getBoolean("isSelected"),
)

internal fun JSONObject.tvPlaybackPreferences(): TvV1PlaybackPreferences = TvV1PlaybackPreferences(
    audioLanguage = if (isNull("audioLanguage")) null else getString("audioLanguage"),
    subtitleLanguage = if (isNull("subtitleLanguage")) null else getString("subtitleLanguage"),
    subtitleModeTypeId = getInt("subtitleModeTypeId"),
    speed = BigDecimal(get("speed").toString()),
    autoplayNext = getBoolean("autoplayNext"),
    introSkipModeTypeId = getInt("introSkipModeTypeId"),
    outroSkipModeTypeId = getInt("outroSkipModeTypeId"),
    autoplayPreviews = getBoolean("autoplayPreviews"),
    profileLanguage = getString("profileLanguage"),
    deviceQuality = getInt("deviceQuality"),
)

internal fun TvV1PlaybackPreferencesUpdate.toJson(): JSONObject = JSONObject()
    .put("audioLanguage", audioLanguage ?: JSONObject.NULL)
    .put("subtitleLanguage", subtitleLanguage ?: JSONObject.NULL)
    .put("subtitleModeTypeId", subtitleModeTypeId)
    .put("speed", speed)
    .put("autoplayNext", autoplayNext)
    .put("introSkipModeTypeId", introSkipModeTypeId)
    .put("outroSkipModeTypeId", outroSkipModeTypeId)
    .put("autoplayPreviews", autoplayPreviews)
