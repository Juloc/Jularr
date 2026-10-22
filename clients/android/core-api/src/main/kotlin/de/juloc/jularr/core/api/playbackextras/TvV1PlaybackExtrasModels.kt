package de.juloc.jularr.core.api.playbackextras

import java.math.BigDecimal

/** Numeric values match clean-start MediaMarkerKind: Intro=1, Credits=2, Recap=3, Preview=4. */
object TvV1MarkerKinds {
    const val INTRO = 1
    const val CREDITS = 2
    const val RECAP = 3
    const val PREVIEW = 4
}

data class TvV1PlaybackMarker(
    val mediaMarkerKindTypeId: Int,
    val startMs: Long,
    val endMs: Long,
) {
    init {
        require(mediaMarkerKindTypeId in 1..4)
        require(startMs >= 0 && endMs > startMs)
    }
    fun contains(positionMs: Long): Boolean = positionMs >= startMs && positionMs < endMs
}

/** Chapter indices and nullable titles come from MediaChapters, never generated from timestamps. */
data class TvV1PlaybackChapter(
    val chapterIndex: Int,
    val startMs: Long,
    val endMs: Long,
    val title: String?,
) {
    init {
        require(chapterIndex >= 0)
        require(startMs >= 0 && endMs > startMs)
    }
}

/** Metadata only; a JPEG sheet is fetched separately with the same authenticated device token. */
data class TvV1Trickplay(
    val intervalSeconds: Int,
    val frames: Int,
    val columns: Int,
    val rows: Int,
    val tileWidth: Int,
    val tileHeight: Int,
) {
    init {
        require(intervalSeconds > 0 && frames > 0 && columns > 0 && rows > 0 && tileWidth > 0 && tileHeight > 0)
        require(columns.toLong() * rows <= Int.MAX_VALUE)
    }
    val framesPerSheet: Int get() = columns * rows
    val sheetCount: Int get() = (frames - 1) / framesPerSheet + 1

    /** The original TV player can crop this tile from the returned JPEG sheet. */
    fun previewAt(positionMs: Long): TvV1TrickplayTile {
        require(positionMs >= 0)
        val frame = minOf(positionMs / (intervalSeconds.toLong() * 1000), (frames - 1).toLong()).toInt()
        val tileOnSheet = frame % framesPerSheet
        return TvV1TrickplayTile(
            sheetIndex = frame / framesPerSheet,
            frameIndex = frame,
            x = (tileOnSheet % columns) * tileWidth,
            y = (tileOnSheet / columns) * tileHeight,
            width = tileWidth,
            height = tileHeight,
        )
    }
}

data class TvV1TrickplayTile(
    val sheetIndex: Int, val frameIndex: Int,
    val x: Int, val y: Int, val width: Int, val height: Int,
)

/** Type 2 is audio, 3 is subtitle. Keys identify a stream (sN) or sidecar file (fN). */
data class TvV1PlaybackTrack(
    val key: String,
    val mediaTrackTypeId: Int,
    val language: String?,
    val title: String?,
    val channels: Int?,
    val isDefault: Boolean,
    val isForced: Boolean,
    val isSupported: Boolean,
    val isSelected: Boolean,
) {
    init {
        require(mediaTrackTypeId == 2 || mediaTrackTypeId == 3)
        require(Regex("^[sf][0-9]+$").matches(key))
        require(channels == null || channels > 0)
    }
    val isExternalSidecar: Boolean get() = key.startsWith("f")
    val isAudio: Boolean get() = mediaTrackTypeId == 2
    val isSubtitle: Boolean get() = mediaTrackTypeId == 3
}

/** Profile playback settings; deviceQuality is read-only and therefore excluded from the save DTO. */
data class TvV1PlaybackPreferences(
    val audioLanguage: String?,
    val subtitleLanguage: String?,
    val subtitleModeTypeId: Int,
    val speed: BigDecimal,
    val autoplayNext: Boolean,
    val introSkipModeTypeId: Int,
    val outroSkipModeTypeId: Int,
    val autoplayPreviews: Boolean,
    val profileLanguage: String,
    val deviceQuality: Int,
) {
    fun editable(): TvV1PlaybackPreferencesUpdate = TvV1PlaybackPreferencesUpdate(
        audioLanguage, subtitleLanguage, subtitleModeTypeId, speed,
        autoplayNext, introSkipModeTypeId, outroSkipModeTypeId, autoplayPreviews,
    )
}

data class TvV1PlaybackPreferencesUpdate(
    val audioLanguage: String?,
    val subtitleLanguage: String?,
    val subtitleModeTypeId: Int,
    val speed: BigDecimal,
    val autoplayNext: Boolean,
    val introSkipModeTypeId: Int,
    val outroSkipModeTypeId: Int,
    val autoplayPreviews: Boolean,
) {
    init {
        require(subtitleModeTypeId in 1..4)
        require(introSkipModeTypeId in 1..3 && outroSkipModeTypeId in 1..3)
        require(speed >= BigDecimal("0.25") && speed <= BigDecimal("4.00") && speed.scale() <= 2)
        require(listOfNotNull(audioLanguage, subtitleLanguage).all { Regex("^[a-z]{2}$").matches(it) })
    }
}

/** Matches server enum values; Off=1, Button=2, Automatic=3. */
object TvV1SkipModes {
    const val OFF = 1
    const val BUTTON = 2
    const val AUTOMATIC = 3
}

/** Off=1, ForcedOnly=2, Always=3, WhenForeign=4. */
object TvV1SubtitleModes {
    const val OFF = 1
    const val FORCED_ONLY = 2
    const val ALWAYS = 3
    const val WHEN_FOREIGN = 4
}
