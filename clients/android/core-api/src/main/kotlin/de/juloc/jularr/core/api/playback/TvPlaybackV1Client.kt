package de.juloc.jularr.core.api.playback

/** clean-start playback APIs use MediaAssetIds, not Work or WorkUnit IDs. */
data class TvPlaybackV1Plan(
    val playbackSessionId: String,
    val playbackModeTypeId: Int,
    val streamUrl: String,
    val resumePositionMs: Long,
    val durationMs: Long?,
    val audioStreamIndex: Int?,
    val quality: Int,
    val isReduced: Boolean,
)

data class TvPlaybackV1Capabilities(
    val containers: List<String>,
    val videoCodecs: List<String>,
    val audioCodecs: List<String>,
)

interface TvPlaybackV1Client {
    suspend fun start(
        mediaAssetId: Long,
        capabilities: TvPlaybackV1Capabilities,
        audioStreamIndex: Int? = null,
    ): TvPlaybackV1Plan
    suspend fun update(
        playbackSessionId: String,
        positionMs: Long,
        durationMs: Long?,
        isEnded: Boolean,
        isCompleted: Boolean,
    )
}
