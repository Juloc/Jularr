package de.juloc.jularr.tv

enum class TvProgressEvent {
    HEARTBEAT,
    PAUSE,
    SEEK,
    BACKGROUND,
    CLOSE,
}

data class TvProgressWrite(
    val positionMs: Long,
    val durationMs: Long?,
    val completed: Boolean,
)

/**
 * Decides when playback progress is written and whether the write declares completion. The server never infers
 * completion from a position, so a seek or scrub past the threshold stays a resume point: completion is declared
 * only while the position was reached by playback itself (a heartbeat arrives only while playing, a seek ends it).
 */
class TvProgressPolicy(
    private val heartbeatIntervalMs: Long = 5_000,
    private val completionThreshold: Double = 0.95,
) {
    private var lastPersistedAtMs: Long? = null
    private var reachedByPlayback = false

    init {
        require(heartbeatIntervalMs > 0)
        require(completionThreshold in 0.5..1.0)
    }

    fun evaluate(
        event: TvProgressEvent,
        nowMs: Long,
        positionMs: Long,
        durationMs: Long?,
    ): TvProgressWrite? {
        when (event) {
            TvProgressEvent.HEARTBEAT -> reachedByPlayback = true
            TvProgressEvent.SEEK -> reachedByPlayback = false
            else -> Unit
        }

        val normalizedPosition = positionMs.coerceAtLeast(0)
        val normalizedDuration = durationMs?.takeIf { it > 0 }
        val immediate = event != TvProgressEvent.HEARTBEAT
        val due = lastPersistedAtMs?.let {
            nowMs - it >= heartbeatIntervalMs
        } ?: true

        if (!immediate && !due) {
            return null
        }

        lastPersistedAtMs = nowMs
        val completed = reachedByPlayback && normalizedDuration?.let {
            normalizedPosition.toDouble() / it.toDouble() >= completionThreshold
        } == true

        return TvProgressWrite(
            positionMs = normalizedPosition,
            durationMs = normalizedDuration,
            completed = completed,
        )
    }
}
