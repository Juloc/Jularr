package de.juloc.jularr.tv

import de.juloc.jularr.core.model.CueResponse
import de.juloc.jularr.core.model.SubtitleCue

object TvCueTimeline {
    fun currentCue(
        window: CueResponse,
        positionMs: Long,
    ): SubtitleCue? {
        if (positionMs < 0) return null
        return window.cues.firstOrNull { cue ->
            positionMs >= cue.startMs && positionMs < cue.endMs
        }
    }

    fun shouldRefresh(
        window: CueResponse,
        positionMs: Long,
        refreshBeforeEndMs: Int = 10_000,
    ): Boolean {
        require(refreshBeforeEndMs >= 0)
        val from = window.fromMs ?: return true
        val to = window.toMs ?: return true
        if (positionMs < from) return true

        val refreshAt = (to - refreshBeforeEndMs).coerceAtLeast(from)
        return positionMs >= refreshAt
    }
}
