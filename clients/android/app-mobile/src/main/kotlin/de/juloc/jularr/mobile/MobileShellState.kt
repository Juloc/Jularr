package de.juloc.jularr.mobile

import de.juloc.jularr.core.api.ApiCompatibility
import de.juloc.jularr.core.api.ClientApiCompatibility
import de.juloc.jularr.core.api.ClientApiRoutes
import de.juloc.jularr.core.model.ClientCapabilities

data class MobileShellState(
    val webUrl: String,
    val activeEpisodeId: String? = null,
) {
    fun openNativeEpisode(episodeId: String): MobileShellState =
        copy(activeEpisodeId = episodeId)

    fun closeNativePlayer(): MobileShellState =
        copy(activeEpisodeId = null)
}

sealed interface MobileCompatibilityState {
    data object Compatible : MobileCompatibilityState
    data class UpdateRequired(val detail: String) : MobileCompatibilityState
}

object MobileCompatibilityGate {
    fun evaluate(capabilities: ClientCapabilities): MobileCompatibilityState =
        when (val compatibility = ClientApiCompatibility.evaluate(capabilities)) {
            ApiCompatibility.Compatible -> MobileCompatibilityState.Compatible
            is ApiCompatibility.ClientTooOld -> MobileCompatibilityState.UpdateRequired(
                "This Jularr server requires client API ${compatibility.minimumSupportedApiVersion}.",
            )
            is ApiCompatibility.ServerTooOld -> MobileCompatibilityState.UpdateRequired(
                "This app requires Jularr client API ${ClientApiRoutes.ApiVersion}, but the server exposes ${compatibility.serverApiVersion}.",
            )
        }
}

object PlaybackPositionPolicy {
    fun absolutePosition(
        playerPositionMs: Long,
        sourceOffsetMs: Long,
    ): Long = (sourceOffsetMs + playerPositionMs).coerceAtLeast(0)

    fun preserve(
        playerPositionMs: Long,
        sourceOffsetMs: Long,
        shouldPlay: Boolean,
    ): PreservedPlayback = PreservedPlayback(
        positionMs = absolutePosition(playerPositionMs, sourceOffsetMs),
        shouldPlay = shouldPlay,
    )
}
