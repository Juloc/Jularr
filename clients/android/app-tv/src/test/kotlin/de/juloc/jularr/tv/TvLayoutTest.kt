package de.juloc.jularr.tv

import androidx.compose.ui.unit.dp
import org.junit.Assert.assertEquals
import org.junit.Test

/**
 * The sizes of the television are fixed by the layout specification: the cards and the hero
 * show 340 pixels of a 1080p screen, the rail and the top row grow with the same factor.
 */
class TvLayoutTest {
    @Test
    fun theCardsAndTheHeroShow340Pixels() {
        assertEquals(170.dp, TvLayout.CardWidth)
        assertEquals(170.dp, TvLayout.HeroHeight)
        assertEquals(255.dp, TvLayout.CardHeight)
    }

    @Test
    fun theTopRowAndTheRailEntriesGrowWithTheFactor() {
        assertEquals(46.dp, TvLayout.TopRowHeight)
        assertEquals(32.dp, TvLayout.RailItemHeight)
        assertEquals(160.dp, TvLayout.RailWidth)
    }

    @Test
    fun aDesktopSizeBecomesLargerOnTheTelevision() {
        assertEquals(10.15f, TvLayout.dp(14f).value, 0.01f)
        assertEquals(46.4f, TvLayout.dp(64f).value, 0.01f)
    }
}
