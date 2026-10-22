package de.juloc.jularr.core.player

import de.juloc.jularr.core.model.ClientCapabilities
import de.juloc.jularr.core.model.PlayerBootstrap

enum class PlaybackTransport {
    DIRECT,
    LIVE_MP4_FALLBACK,
    HLS_FALLBACK,
}

data class DevicePlaybackSupport(
    val directContainerAndCodecSupported: Boolean,
)

class StorageUnavailableException(
    val state: String,
    val retryable: Boolean,
    val retryAfterMs: Int,
) : IllegalStateException("Media storage is not currently available: $state")

object PlaybackSelector {
    fun select(
        capabilities: ClientCapabilities,
        bootstrap: PlayerBootstrap,
        device: DevicePlaybackSupport,
    ): PlaybackTransport {
        val media = bootstrap.media
            ?: throw IllegalStateException("This episode does not have playable media.")

        if (!media.availability.isAvailable) {
            throw StorageUnavailableException(
                state = media.availability.state,
                retryable = media.availability.retryable,
                retryAfterMs = media.availability.retryAfterMs,
            )
        }

        if (capabilities.features.directPlayback &&
            capabilities.features.httpRangeRequests &&
            media.supportsRangeRequests &&
            media.device.availability == "ready" &&
            device.directContainerAndCodecSupported
        ) {
            return PlaybackTransport.DIRECT
        }

        val fallback = bootstrap.fallback
        if (fallback.available && fallback.url != null) {
            if (fallback.kind == "hls" && capabilities.features.hlsFallback) {
                return PlaybackTransport.HLS_FALLBACK
            }

            if (capabilities.features.liveMp4Fallback) {
                return PlaybackTransport.LIVE_MP4_FALLBACK
            }
        }

        throw IllegalStateException("No supported playback transport is available.")
    }
}
