package de.juloc.jularr.core.api.playbackextras

/**
 * Independent read/write adapter for the original Android TV player's existing panels.
 * Pass the same DeviceTokenStore instance used by TV pairing and the playback session client.
 * No TV UI, session ownership or Android bitmap dependencies are introduced.
 */
interface TvV1PlaybackExtrasClient {
    suspend fun markers(mediaAssetId: Long): List<TvV1PlaybackMarker>
    suspend fun chapters(mediaAssetId: Long): List<TvV1PlaybackChapter>
    /** Missing trickplay is 404, not an invented sprite. */
    suspend fun trickplay(mediaAssetId: Long): TvV1Trickplay
    /** Raw authenticated JPEG sprite sheet, to decode/crop using TvV1TrickplayTile. */
    suspend fun trickplaySheet(mediaAssetId: Long, sheetIndex: Int): ByteArray
    suspend fun tracks(mediaAssetId: Long): List<TvV1PlaybackTrack>
    /** Only use a supported subtitle track key returned by tracks(); returns literal WebVTT text. */
    suspend fun subtitleVtt(mediaAssetId: Long, key: String): String
    suspend fun preferences(): TvV1PlaybackPreferences
    /** Saves all editable profile settings, not read-only deviceQuality or profileLanguage. */
    suspend fun savePreferences(update: TvV1PlaybackPreferencesUpdate): Boolean
    /** Saves only playback speed, without overwriting other preference fields. */
    suspend fun saveSpeed(speed: java.math.BigDecimal): Boolean
}
