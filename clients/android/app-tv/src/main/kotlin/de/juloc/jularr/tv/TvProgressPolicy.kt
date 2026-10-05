package de.juloc.jularr.tv

enum class TvProgressEvent {
    HEARTBEAT,
    PAUSE,
    SEEK,
    BACKGROUND,
    CLOSE,
    ENDED,
}

data class TvProgressWrite(
    val positionMs: Long,
    val durationMs: Long?,
    val completed: Boolean,
)

/**
 * Decides when playback progress is written and whether the write declares completion. The server never infers
 * completion from a position, so a seek or scrub that lands at or beyond the threshold stays a resume point.
 * Completion is declared only when playback itself crossed the threshold (consecutive heartbeats moving forward
 * in small steps from below it) or when playback ended; a seek resets the crossing, so after a seek past the
 * threshold only [TvProgressEvent.ENDED] completes.
 */
class TvProgressPolicy(
    private val heartbeatIntervalMs: Long = 5_000,
    private val completionThreshold: Double = 0.95,
    private val naturalStepMs: Long = 5_000,
) {
    private var lastPersistedAtMs: Long? = null
    private var lastHeartbeatPositionMs: Long? = null
    private var crossedByPlayback = false

    init {
        require(heartbeatIntervalMs > 0)
        require(completionThreshold in 0.5..1.0)
        require(naturalStepMs > 0)
    }

    fun evaluate(
        event: TvProgressEvent,
        nowMs: Long,
        positionMs: Long,
        durationMs: Long?,
    ): TvProgressWrite? {
        val normalizedPosition = positionMs.coerceAtLeast(0)
        val normalizedDuration = durationMs?.takeIf { it > 0 }

        when (event) {
            TvProgressEvent.HEARTBEAT -> trackPlayback(normalizedPosition, normalizedDuration)
            TvProgressEvent.SEEK -> {
                lastHeartbeatPositionMs = null
                crossedByPlayback = false
            }
            else -> Unit
        }

        val immediate = event != TvProgressEvent.HEARTBEAT
        val due = lastPersistedAtMs?.let {
            nowMs - it >= heartbeatIntervalMs
        } ?: true

        if (!immediate && !due) {
            return null
        }

        lastPersistedAtMs = nowMs

        return TvProgressWrite(
            positionMs = normalizedPosition,
            durationMs = normalizedDuration,
            completed = event == TvProgressEvent.ENDED || crossedByPlayback,
        )
    }

    private fun trackPlayback(positionMs: Long, durationMs: Long?) {
        val previous = lastHeartbeatPositionMs
        lastHeartbeatPositionMs = positionMs
        if (previous == null || durationMs == null) {
            return
        }

        val step = positionMs - previous
        if (step < 0 || step > naturalStepMs) {
            crossedByPlayback = false
            return
        }

        val thresholdMs = durationMs.toDouble() * completionThreshold
        if (previous < thresholdMs && positionMs >= thresholdMs) {
            crossedByPlayback = true
        }
    }
}
