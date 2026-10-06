package de.juloc.jularr.core.design

import java.io.File
import org.json.JSONObject
import org.junit.Assert.assertEquals
import org.junit.Assert.assertThrows
import org.junit.Test

class PlayerSeekStepsTest {
    @Test
    fun canonicalTokensSeekTenSecondsBackAndThirtyForward() {
        // The module's working directory is clients/android/core-design; the tokens live at the repository root.
        val tokens = JSONObject(File("../../../design/player/player-tokens.json").readText())

        val steps = PlayerSeekSteps.fromTokens(tokens)

        assertEquals(10, steps.backSeconds)
        assertEquals(30, steps.forwardSeconds)
        assertEquals(10_000L, steps.backMs)
        assertEquals(30_000L, steps.forwardMs)
    }

    @Test
    fun missingOrNonPositiveIncrementsAreRejectedInsteadOfDefaulted() {
        assertThrows(Exception::class.java) {
            PlayerSeekSteps.fromTokens(JSONObject("""{"playback":{"seekBackSeconds":10}}"""))
        }
        assertThrows(IllegalArgumentException::class.java) {
            PlayerSeekSteps.fromTokens(JSONObject("""{"playback":{"seekBackSeconds":10,"seekForwardSeconds":0}}"""))
        }
    }
}
