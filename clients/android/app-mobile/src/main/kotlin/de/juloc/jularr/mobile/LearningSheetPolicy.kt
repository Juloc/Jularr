package de.juloc.jularr.mobile

data class LearningPlaybackState(
    val wasPlayingBeforeOpen: Boolean,
    val userPausedInsideSheet: Boolean = false,
)

object LearningSheetPolicy {
    fun open(isCurrentlyPlaying: Boolean): LearningPlaybackState =
        LearningPlaybackState(wasPlayingBeforeOpen = isCurrentlyPlaying)

    fun userExplicitlyPaused(state: LearningPlaybackState): LearningPlaybackState =
        state.copy(userPausedInsideSheet = true)

    fun shouldResumeOnClose(state: LearningPlaybackState): Boolean =
        state.wasPlayingBeforeOpen && !state.userPausedInsideSheet
}
