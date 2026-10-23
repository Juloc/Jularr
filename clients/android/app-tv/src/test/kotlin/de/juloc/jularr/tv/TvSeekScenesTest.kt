package de.juloc.jularr.tv

import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

class TvSeekScenesTest {
    @Test
    fun scenePositionsRemainWithinAvailableSpriteTiles() {
        assertEquals(emptyList<Int>(), tvSeekSceneIndices(0))
        assertEquals(emptyList<Int>(), tvSeekSceneIndices(-1))
        assertEquals(listOf(0), tvSeekSceneIndices(1))
        assertEquals(listOf(0, 1, 2), tvSeekSceneIndices(3))

        val samples = tvSeekSceneIndices(720)
        assertEquals(8, samples.size)
        assertEquals(0, samples.first())
        assertEquals(719, samples.last())
        assertTrue(samples.zipWithNext().all { (first, next) -> next > first })
    }
}
