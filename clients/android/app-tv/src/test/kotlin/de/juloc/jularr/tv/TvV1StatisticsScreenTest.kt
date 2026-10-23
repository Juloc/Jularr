package de.juloc.jularr.tv

import org.junit.Assert.assertEquals
import org.junit.Test

class TvV1StatisticsScreenTest {
    @Test fun watchDurationUsesAggregatedMsNotInventedActivityEvents() {
        assertEquals("0 Min.", tvV1WatchDuration(0))
        assertEquals("2 Std. 1 Min.", tvV1WatchDuration(7_260_000))
        assertEquals("8 Min.", tvV1WatchDuration(512_000))
    }

    @Test fun statisticsScreenReturnsToSettingsWithoutDestroyingHistory() {
        val settings = TvV1Navigation.open(TvV1NavigationState(), TvV1CatalogPage.Settings)
        val stats = TvV1Navigation.open(settings, TvV1CatalogPage.Statistics)
        val back = TvV1Navigation.back(stats)
        assertEquals(TvV1CatalogPage.Settings, back.page)
    }
}
