package de.juloc.jularr.core.model

/**
 * Server-issued description of one episode that may be kept on the device
 * (`GET /episodes/{id}/offline-download`). The server stays the owner of
 * media identity; the client only verifies its local copy against it.
 */
data class OfflineDownloadDescriptor(
    val apiVersion: Int,
    val issuedAtUtc: String,
    val episode: PlayerEpisode,
    val media: OfflineMedia,
    val audioTracks: List<MediaTrack>,
    val subtitleTracks: List<MediaTrack>,
    val defaultAudioTrackId: String?,
    val defaultSubtitleTrackId: String?,
    val learningCues: CueResponse,
    val progress: EpisodeProgress,
)

data class OfflineMedia(
    val mediaFileId: String,
    val fileName: String,
    val contentType: String,
    val sizeBytes: Long,
    val durationMs: Long?,
    val videoCodec: String?,
    val pixelFormat: String?,
    val audioCodec: String?,
    val contentUrl: String,
    val eTag: String,
    val fingerprint: String,
    val fingerprintAlgorithm: String,
)

/** Descriptor plus the exact JSON it was parsed from, so it can be stored and re-read offline. */
data class OfflineDownloadPackage(
    val descriptor: OfflineDownloadDescriptor,
    val json: String,
)

/** One offline checkpoint sent to `POST /offline/progress`. */
data class OfflineProgressItem(
    val episodeId: String,
    val positionMs: Long,
    val durationMs: Long?,
    val completed: Boolean,
)

/**
 * Server decision for one offline checkpoint. Every outcome is final: the
 * client drops the queued checkpoint and adopts [progress] as its local copy.
 */
data class OfflineProgressResult(
    val episodeId: String,
    val outcome: String,
    val progress: EpisodeProgress?,
)
