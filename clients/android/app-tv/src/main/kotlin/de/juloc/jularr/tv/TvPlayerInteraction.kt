package de.juloc.jularr.tv

import de.juloc.jularr.core.design.PlayerSeekSteps

enum class TvLearningLayer {
    CLOSED,
    SENTENCE,
    WORD,
}

data class TvPlayerUiState(
    val controlsVisible: Boolean = false,
    val learningLayer: TvLearningLayer = TvLearningLayer.CLOSED,
    val focusedWordIndex: Int = 0,
    val resumeAfterLearning: Boolean = false,
)

sealed interface TvPlayerEffect {
    data object TogglePlayback : TvPlayerEffect
    data class SeekBy(val deltaMs: Long) : TvPlayerEffect
    data object PausePlayback : TvPlayerEffect
    data object ResumePlayback : TvPlayerEffect
    data object ExitPlayer : TvPlayerEffect
    data object OpenOnPhone : TvPlayerEffect
}

data class TvPlayerTransition(
    val state: TvPlayerUiState,
    val effects: List<TvPlayerEffect> = emptyList(),
)

object TvPlayerInteraction {
    fun mediaPlayPause(state: TvPlayerUiState): TvPlayerTransition =
        TvPlayerTransition(state, listOf(TvPlayerEffect.TogglePlayback))

    fun autoHide(
        state: TvPlayerUiState,
        isPlaying: Boolean,
        companionVisible: Boolean = false,
    ): TvPlayerTransition =
        if (isPlaying &&
            !companionVisible &&
            state.controlsVisible &&
            state.learningLayer == TvLearningLayer.CLOSED
        ) {
            TvPlayerTransition(state.copy(controlsVisible = false))
        } else {
            TvPlayerTransition(state)
        }

    fun ok(
        state: TvPlayerUiState,
        wordCount: Int,
    ): TvPlayerTransition =
        when (state.learningLayer) {
            TvLearningLayer.WORD -> TvPlayerTransition(state)
            TvLearningLayer.SENTENCE -> {
                if (wordCount <= 0) {
                    TvPlayerTransition(state)
                } else {
                    TvPlayerTransition(
                        state.copy(
                            learningLayer = TvLearningLayer.WORD,
                            focusedWordIndex = state.focusedWordIndex.coerceIn(0, wordCount - 1),
                        ),
                    )
                }
            }

            TvLearningLayer.CLOSED -> {
                if (!state.controlsVisible) {
                    TvPlayerTransition(state.copy(controlsVisible = true))
                } else {
                    TvPlayerTransition(state)
                }
            }
        }

    fun left(
        state: TvPlayerUiState,
        wordCount: Int,
        seek: PlayerSeekSteps,
    ): TvPlayerTransition =
        when (state.learningLayer) {
            TvLearningLayer.WORD,
            TvLearningLayer.SENTENCE,
            -> TvPlayerTransition(
                state.copy(
                    focusedWordIndex = moveWord(
                        current = state.focusedWordIndex,
                        delta = -1,
                        wordCount = wordCount,
                    ),
                ),
            )

            TvLearningLayer.CLOSED -> {
                if (state.controlsVisible) {
                    TvPlayerTransition(state)
                } else {
                    TvPlayerTransition(state, listOf(TvPlayerEffect.SeekBy(-seek.backMs)))
                }
            }
        }

    fun right(
        state: TvPlayerUiState,
        wordCount: Int,
        seek: PlayerSeekSteps,
    ): TvPlayerTransition =
        when (state.learningLayer) {
            TvLearningLayer.WORD,
            TvLearningLayer.SENTENCE,
            -> TvPlayerTransition(
                state.copy(
                    focusedWordIndex = moveWord(
                        current = state.focusedWordIndex,
                        delta = 1,
                        wordCount = wordCount,
                    ),
                ),
            )

            TvLearningLayer.CLOSED -> {
                if (state.controlsVisible) {
                    TvPlayerTransition(state)
                } else {
                    TvPlayerTransition(state, listOf(TvPlayerEffect.SeekBy(seek.forwardMs)))
                }
            }
        }

    fun learnCurrentLine(
        state: TvPlayerUiState,
        isPlaying: Boolean,
        wordCount: Int,
    ): TvPlayerTransition {
        val next = state.copy(
            controlsVisible = false,
            learningLayer = TvLearningLayer.SENTENCE,
            focusedWordIndex = if (wordCount > 0) 0 else state.focusedWordIndex,
            resumeAfterLearning = isPlaying,
        )
        return TvPlayerTransition(
            next,
            effects = if (isPlaying) listOf(TvPlayerEffect.PausePlayback) else emptyList(),
        )
    }

    fun back(state: TvPlayerUiState): TvPlayerTransition =
        when (state.learningLayer) {
            TvLearningLayer.WORD ->
                TvPlayerTransition(state.copy(learningLayer = TvLearningLayer.SENTENCE))

            TvLearningLayer.SENTENCE -> {
                val shouldResume = state.resumeAfterLearning
                TvPlayerTransition(
                    state.copy(
                        learningLayer = TvLearningLayer.CLOSED,
                        resumeAfterLearning = false,
                    ),
                    effects = if (shouldResume) {
                        listOf(TvPlayerEffect.ResumePlayback)
                    } else {
                        emptyList()
                    },
                )
            }

            TvLearningLayer.CLOSED -> {
                if (state.controlsVisible) {
                    TvPlayerTransition(state.copy(controlsVisible = false))
                } else {
                    TvPlayerTransition(state, listOf(TvPlayerEffect.ExitPlayer))
                }
            }
        }

    fun openOnPhone(state: TvPlayerUiState): TvPlayerTransition =
        TvPlayerTransition(state, listOf(TvPlayerEffect.OpenOnPhone))

    private fun moveWord(
        current: Int,
        delta: Int,
        wordCount: Int,
    ): Int {
        if (wordCount <= 0) {
            return 0
        }

        return (current + delta).coerceIn(0, wordCount - 1)
    }
}
