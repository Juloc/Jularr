package de.juloc.jularr.mobile.offline

import de.juloc.jularr.core.model.CompatibilityFallback
import de.juloc.jularr.core.model.MediaAvailability
import de.juloc.jularr.core.model.OfflineDownloadDescriptor
import de.juloc.jularr.core.model.PlaybackOption
import de.juloc.jularr.core.model.PlayerBootstrap
import de.juloc.jularr.core.model.PlayerMedia
import de.juloc.jularr.core.model.SubtitleCue
import de.juloc.jularr.core.model.TermDetail

/** Maps a stored offline descriptor onto the player's existing models. */
object OfflinePlayback {
    /**
     * Player bootstrap for playback without the server: titles, audio/subtitle
     * track metadata and defaults from the descriptor, no server fallback.
     */
    fun bootstrap(descriptor: OfflineDownloadDescriptor): PlayerBootstrap {
        val media = descriptor.media
        val local = PlaybackOption(availability = "ready", message = "Downloaded", usesLiveStream = false)
        return PlayerBootstrap(
            apiVersion = descriptor.apiVersion,
            episode = descriptor.episode,
            media = PlayerMedia(
                mediaFileId = media.mediaFileId,
                fileName = media.fileName,
                contentType = media.contentType,
                sizeBytes = media.sizeBytes,
                durationMs = media.durationMs,
                videoCodec = media.videoCodec,
                pixelFormat = media.pixelFormat,
                audioCodec = media.audioCodec,
                directContentUrl = media.contentUrl,
                supportsRangeRequests = true,
                device = local,
                server = PlaybackOption(availability = "unsupported", message = "Offline", usesLiveStream = false),
                availability = MediaAvailability(
                    state = "available",
                    retryable = false,
                    retryAfterMs = 0,
                    canWake = false,
                    rootId = null,
                    availabilityUrl = "",
                    wakeUrl = null,
                ),
            ),
            audioTracks = descriptor.audioTracks,
            subtitleTracks = descriptor.subtitleTracks,
            learningSubtitles = emptyList(),
            activeLearningSubtitleTrackId = descriptor.learningCues.trackId,
            defaultAudioTrackId = descriptor.defaultAudioTrackId,
            defaultSubtitleTrackId = descriptor.defaultSubtitleTrackId,
            fallback = CompatibilityFallback(
                available = false,
                kind = null,
                seekableWithinStream = false,
                canRestartAtPosition = false,
                url = null,
            ),
        )
    }

    /** Term details from the stored cue tokens (reading, meaning and state at download time). */
    fun termFromCues(cues: List<SubtitleCue>, termId: String): TermDetail? =
        cues.asSequence()
            .flatMap { it.tokens.asSequence() }
            .firstOrNull { it.termId == termId }
            ?.let { token ->
                TermDetail(
                    id = termId,
                    canonical = token.canonical ?: token.surface,
                    reading = token.reading,
                    meaning = token.meaning,
                    state = token.state,
                )
            }
}
