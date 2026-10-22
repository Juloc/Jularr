package de.juloc.jularr.tv

import de.juloc.jularr.core.model.MediaAvailability

enum class TvStorageAction {
    PLAY,
    RETRY,
    WAKE,
    STOP,
}

data class TvStorageDecision(
    val state: String,
    val message: String,
    val primaryAction: TvStorageAction,
    val canWake: Boolean,
    val retryAfterMs: Int,
)

object TvStorageRecoveryPolicy {
    const val MaximumAutomaticRetryMs = 60_000L

    fun decide(
        availability: MediaAvailability,
        elapsedMs: Long,
    ): TvStorageDecision {
        if (availability.isAvailable) {
            return TvStorageDecision(
                state = availability.state,
                message = "Media storage is available.",
                primaryAction = TvStorageAction.PLAY,
                canWake = false,
                retryAfterMs = 0,
            )
        }

        if (availability.state == "file_missing") {
            return TvStorageDecision(
                state = availability.state,
                message = "The media file is missing from the available library storage.",
                primaryAction = TvStorageAction.STOP,
                canWake = false,
                retryAfterMs = 0,
            )
        }

        val canRetry = availability.retryable &&
            elapsedMs < MaximumAutomaticRetryMs

        val message = when (availability.state) {
            "source_starting" -> "Media storage is starting."
            "source_offline" -> "Media storage is offline."
            "source_unreachable" -> "Media storage cannot be reached."
            else -> "Media storage is not available."
        }

        return TvStorageDecision(
            state = availability.state,
            message = message,
            primaryAction = if (canRetry) TvStorageAction.RETRY else TvStorageAction.STOP,
            canWake = availability.canWake && availability.wakeUrl != null,
            retryAfterMs = if (canRetry) {
                availability.retryAfterMs.coerceAtLeast(250)
            } else {
                0
            },
        )
    }

    fun wakeDecision(
        availability: MediaAvailability,
    ): TvStorageDecision {
        require(availability.canWake && availability.wakeUrl != null) {
            "Wake-on-LAN is not available for this profile or media root."
        }

        return TvStorageDecision(
            state = "source_starting",
            message = "Wake-on-LAN requested. Waiting for media storage.",
            primaryAction = TvStorageAction.RETRY,
            canWake = false,
            retryAfterMs = 1_000,
        )
    }
}
