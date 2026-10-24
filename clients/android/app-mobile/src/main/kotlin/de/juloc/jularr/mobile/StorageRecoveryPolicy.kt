package de.juloc.jularr.mobile

import de.juloc.jularr.core.model.MediaAvailability

data class PreservedPlayback(
    val positionMs: Long,
    val shouldPlay: Boolean,
)

object StorageRecoveryPolicy {
    const val MaxAutomaticRetryMs = 60_000L
    val RetryDelaysMs = longArrayOf(0, 1_000, 2_000, 4_000, 5_000)

    fun shouldAutomaticallyRetry(availability: MediaAvailability): Boolean =
        availability.retryable &&
            availability.state != "file_missing" &&
            availability.state != "source_unreachable"

    fun canOfferWake(availability: MediaAvailability): Boolean =
        availability.canWake &&
            !availability.wakeUrl.isNullOrBlank() &&
            !availability.rootId.isNullOrBlank() &&
            availability.state in setOf("source_offline", "source_starting", "unknown")

    fun delayForAttempt(attempt: Int): Long =
        RetryDelaysMs[attempt.coerceIn(0, RetryDelaysMs.lastIndex)]
}
