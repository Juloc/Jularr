package de.juloc.jularr.mobile

import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class LearningSheetPolicyTest {
    @Test
    fun closesBackToPlayingOnlyWhenItWasPlayingBeforeOpen() {
        assertTrue(
            LearningSheetPolicy.shouldResumeOnClose(
                LearningSheetPolicy.open(isCurrentlyPlaying = true),
            ),
        )
        assertFalse(
            LearningSheetPolicy.shouldResumeOnClose(
                LearningSheetPolicy.open(isCurrentlyPlaying = false),
            ),
        )
    }

    @Test
    fun explicitPauseInsideSheetPreventsAutomaticResume() {
        val state = LearningSheetPolicy.userExplicitlyPaused(
            LearningSheetPolicy.open(isCurrentlyPlaying = true),
        )
        assertFalse(LearningSheetPolicy.shouldResumeOnClose(state))
    }
}
