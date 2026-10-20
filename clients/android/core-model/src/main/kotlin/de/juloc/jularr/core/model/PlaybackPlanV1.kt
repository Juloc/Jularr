package de.juloc.jularr.core.model

/**
 * The plan of a playback: the session the instance opened, the stream to read and where the
 * viewer continues.
 */
data class PlaybackPlanV1(
    val playbackSessionId: String,
    val playbackModeTypeId: Int,
    val streamUrl: String,
    val resumePositionMs: Long,
    val durationMs: Long?,
)
