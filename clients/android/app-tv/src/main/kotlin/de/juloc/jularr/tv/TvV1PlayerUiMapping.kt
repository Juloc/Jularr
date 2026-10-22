package de.juloc.jularr.tv

import de.juloc.jularr.core.api.playbackextras.TvV1MarkerKinds
import de.juloc.jularr.core.api.playbackextras.TvV1PlaybackMarker
import de.juloc.jularr.core.api.playbackextras.TvV1PlaybackPreferences
import de.juloc.jularr.core.api.playbackextras.TvV1PlaybackTrack
import de.juloc.jularr.core.api.playbackextras.TvV1SkipModes
import de.juloc.jularr.core.api.playbackextras.TvV1Trickplay
import de.juloc.jularr.core.model.ClientMediaSegment
import de.juloc.jularr.core.model.ClientTrickplayDescriptor
import de.juloc.jularr.core.model.MediaTrack

/** Actual V1 server markers into the original player's Skip controls. */
internal object TvV1PlayerUiMapping {
    fun skipSegments(
        markers: List<TvV1PlaybackMarker>,
        preferences: TvV1PlaybackPreferences?,
    ): List<ClientMediaSegment> = markers.map { marker ->
        val skipMode = modeFor(marker.mediaMarkerKindTypeId, preferences)
        ClientMediaSegment(
            kind = when (marker.mediaMarkerKindTypeId) {
                TvV1MarkerKinds.INTRO -> "intro"
                TvV1MarkerKinds.CREDITS -> "credits"
                TvV1MarkerKinds.RECAP -> "recap"
                TvV1MarkerKinds.PREVIEW -> "preview"
                else -> error("Unknown server marker kind")
            },
            startMs = marker.startMs,
            endMs = marker.endMs,
            canSkip = skipMode != TvV1SkipModes.OFF,
        )
    }

    fun automaticSkipTarget(
        positionMs: Long,
        markers: List<TvV1PlaybackMarker>,
        preferences: TvV1PlaybackPreferences?,
    ): Long? = markers.firstOrNull {
        modeFor(it.mediaMarkerKindTypeId, preferences) == TvV1SkipModes.AUTOMATIC &&
            it.contains(positionMs)
    }?.endMs

    /**
     * These authenticated URLs follow the server's documented 1-based JPEG sheet endpoint.
     * They are generated ONLY after the server has returned actual extracted geometry.
     */
    fun trickplay(mediaAssetId: Long, metadata: TvV1Trickplay): ClientTrickplayDescriptor {
        require(mediaAssetId > 0L)
        return ClientTrickplayDescriptor(
            state = "ready",
            intervalMs = Math.multiplyExact(metadata.intervalSeconds, 1000),
            tileWidth = metadata.tileWidth,
            tileHeight = metadata.tileHeight,
            columns = metadata.columns,
            rows = metadata.rows,
            thumbnailCount = metadata.frames,
            spriteUrls = (1..metadata.sheetCount).map { sheet ->
                "/api/v1/playback/assets/$mediaAssetId/trickplay/$sheet"
            },
        )
    }

    /**
     * Legacy UI's streamIndex is meaningful only for embedded 'sN' tracks.
     * Sidecar 'fN' tracks carry the real key as id, and -1 means no stream index.
     */
    fun trackForDisplay(track: TvV1PlaybackTrack): MediaTrack = MediaTrack(
        id = track.key,
        streamIndex = if (track.key.startsWith("s")) {
            track.key.drop(1).toInt()
        } else -1,
        kind = if (track.isAudio) "audio" else "subtitle",
        codec = null, // V1 track endpoint does not provide codec metadata.
        language = track.language,
        title = track.title,
        isDefault = track.isDefault,
        isForced = track.isForced,
        isText = track.isSubtitle && track.isSupported,
    )

    private fun modeFor(kind: Int, p: TvV1PlaybackPreferences?): Int =
        when (kind) {
            TvV1MarkerKinds.INTRO, TvV1MarkerKinds.RECAP -> p?.introSkipModeTypeId ?: TvV1SkipModes.BUTTON
            TvV1MarkerKinds.CREDITS, TvV1MarkerKinds.PREVIEW -> p?.outroSkipModeTypeId ?: TvV1SkipModes.BUTTON
            else -> TvV1SkipModes.OFF
        }
}
