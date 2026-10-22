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
    /**
     * Use an audio track returned by tracks() to start a new real backend session with
     * its audioStreamIndex. The existing session must be ended by its owner.
     * Does not switch a Media3 stream in place or invent an unavailable track.
     */
    suspend fun startWithAudioTrack(
        mediaAssetId: Long,
        capabilities: de.juloc.jularr.core.api.playback.TvPlaybackV1Capabilities,
        audioTrack: TvV1PlaybackTrack,
        quality: Int? = null,
    ): de.juloc.jularr.core.api.playback.TvPlaybackV1Plan
    /** Only use a supported subtitle track key returned by tracks(); returns literal WebVTT text. */
    suspend fun subtitleVtt(mediaAssetId: Long, key: String): String
    suspend fun preferences(): TvV1PlaybackPreferences
    /** Saves all editable profile settings, not read-only deviceQuality or profileLanguage. */
    suspend fun savePreferences(update: TvV1PlaybackPreferencesUpdate): Boolean
    /** Saves only playback speed, without overwriting other preference fields. */
    suspend fun saveSpeed(speed: java.math.BigDecimal): Boolean
}
