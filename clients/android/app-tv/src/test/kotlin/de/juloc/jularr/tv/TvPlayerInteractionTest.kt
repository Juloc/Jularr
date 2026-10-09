package de.juloc.jularr.tv

import de.juloc.jularr.core.design.PlayerSeekSteps
import de.juloc.jularr.core.model.ClientMediaSegment
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

class TvPlayerInteractionTest {
    private val seek = PlayerSeekSteps(backSeconds = 10, forwardSeconds = 30)

    @Test
    fun hiddenControlsUseLeftRightForTheAsymmetricSeekSteps() {
        val left = TvPlayerInteraction.left(TvPlayerUiState(), wordCount = 0, seek = seek)
        val right = TvPlayerInteraction.right(TvPlayerUiState(), wordCount = 0, seek = seek)

        assertEquals(listOf(TvPlayerEffect.SeekBy(-10_000)), left.effects)
        assertEquals(listOf(TvPlayerEffect.SeekBy(30_000)), right.effects)
    }

    @Test
    fun okShowsControlsAndThenLetsFocusedControlHandleActivation() {
        val shown = TvPlayerInteraction.ok(TvPlayerUiState(), wordCount = 0)
        val visible = TvPlayerInteraction.ok(shown.state, wordCount = 0)

        assertTrue(shown.state.controlsVisible)
        assertTrue(visible.state.controlsVisible)
        assertEquals(emptyList<TvPlayerEffect>(), visible.effects)
    }

    @Test
    fun dedicatedMediaKeysDoNotToggleTheOppositeState() {
        val state = TvPlayerUiState(controlsVisible = true)
        assertEquals(listOf(TvPlayerEffect.PlayPlayback), TvPlayerInteraction.mediaPlay(state).effects)
        assertEquals(listOf(TvPlayerEffect.PauseMediaPlayback), TvPlayerInteraction.mediaPause(state).effects)
        assertEquals(listOf(TvPlayerEffect.TogglePlayback), TvPlayerInteraction.mediaPlayPause(state).effects)
    }

    @Test
    fun skipAppearsOnlyInsideServerApprovedMarker() {
        val windows = listOf(
            ClientMediaSegment("intro", startMs = 60_000, endMs = 90_000, canSkip = false),
            ClientMediaSegment("recap", startMs = 100_000, endMs = 120_000, canSkip = true),
        )

        assertEquals(null, TvPlayerInteraction.activeSkipSegment(windows, 75_000))
        assertEquals(null, TvPlayerInteraction.activeSkipSegment(windows, 99_999))
        assertEquals("recap", TvPlayerInteraction.activeSkipSegment(windows, 100_000)?.kind)
        assertEquals(null, TvPlayerInteraction.activeSkipSegment(windows, 120_000))
    }

    @Test
    fun enteringLearningPausesOnlyWhenPlaybackWasRunning() {
        val playing = TvPlayerInteraction.learnCurrentLine(
            TvPlayerUiState(controlsVisible = true),
            isPlaying = true,
            wordCount = 3,
        )
        val paused = TvPlayerInteraction.learnCurrentLine(
            TvPlayerUiState(controlsVisible = true),
            isPlaying = false,
            wordCount = 3,
        )

        assertEquals(TvLearningLayer.SENTENCE, playing.state.learningLayer)
        assertEquals(listOf(TvPlayerEffect.PausePlayback), playing.effects)
        assertEquals(emptyList<TvPlayerEffect>(), paused.effects)
    }

    @Test
    fun leavingLearningResumesOnlyWhenItEnteredFromPlayingState() {
        val fromPlaying = TvPlayerInteraction.back(
            TvPlayerUiState(
                learningLayer = TvLearningLayer.SENTENCE,
                resumeAfterLearning = true,
            ),
        )
        val fromPaused = TvPlayerInteraction.back(
            TvPlayerUiState(
                learningLayer = TvLearningLayer.SENTENCE,
                resumeAfterLearning = false,
            ),
        )

        assertEquals(listOf(TvPlayerEffect.ResumePlayback), fromPlaying.effects)
        assertEquals(emptyList<TvPlayerEffect>(), fromPaused.effects)
    }

    @Test
    fun wordFocusIsBoundedAndBackClosesOneLearningLevelAtATime() {
        var state = TvPlayerUiState(
            learningLayer = TvLearningLayer.SENTENCE,
            focusedWordIndex = 0,
        )
        state = TvPlayerInteraction.left(state, wordCount = 2, seek = seek).state
        assertEquals(0, state.focusedWordIndex)

        state = TvPlayerInteraction.right(state, wordCount = 2, seek = seek).state
        state = TvPlayerInteraction.right(state, wordCount = 2, seek = seek).state
        assertEquals(1, state.focusedWordIndex)

        state = TvPlayerInteraction.ok(state, wordCount = 2).state
        assertEquals(TvLearningLayer.WORD, state.learningLayer)

        state = TvPlayerInteraction.back(state).state
        assertEquals(TvLearningLayer.SENTENCE, state.learningLayer)
    }

    @Test
    fun autoHideOnlyClosesVisibleControlsWhilePlaying() {
        val playing = TvPlayerInteraction.autoHide(
            TvPlayerUiState(controlsVisible = true),
            isPlaying = true,
        )
        val paused = TvPlayerInteraction.autoHide(
            TvPlayerUiState(controlsVisible = true),
            isPlaying = false,
        )
        val learning = TvPlayerInteraction.autoHide(
            TvPlayerUiState(
                controlsVisible = true,
                learningLayer = TvLearningLayer.SENTENCE,
            ),
            isPlaying = true,
        )

        val companion = TvPlayerInteraction.autoHide(
            TvPlayerUiState(controlsVisible = true),
            isPlaying = true,
            companionVisible = true,
        )

        assertEquals(false, playing.state.controlsVisible)
        assertTrue(paused.state.controlsVisible)
        assertTrue(learning.state.controlsVisible)
        assertTrue(companion.state.controlsVisible)
    }

    @Test
    fun backHidesControlsBeforeLeavingPlayer() {
        val hide = TvPlayerInteraction.back(TvPlayerUiState(controlsVisible = true))
        val exit = TvPlayerInteraction.back(hide.state)

        assertEquals(false, hide.state.controlsVisible)
        assertEquals(listOf(TvPlayerEffect.ExitPlayer), exit.effects)
    }
}
