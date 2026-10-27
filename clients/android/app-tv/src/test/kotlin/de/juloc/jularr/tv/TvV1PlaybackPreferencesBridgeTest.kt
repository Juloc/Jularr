package de.juloc.jularr.tv

import de.juloc.jularr.core.api.playbackextras.TvV1PlaybackPreferences
import de.juloc.jularr.core.model.ClientPlaybackPreferencesUpdate
import org.junit.Assert.*
import org.junit.Test
import java.math.BigDecimal

class TvV1PlaybackPreferencesBridgeTest {
    private fun preferences() = TvV1PlaybackPreferences(
        audioLanguage = "ja", subtitleLanguage = "de", subtitleModeTypeId = 3,
        speed = BigDecimal("1.25"), autoplayNext = true, introSkipModeTypeId = 3,
        outroSkipModeTypeId = 2, autoplayPreviews = false, profileLanguage = "de", deviceQuality = 4,
    )

    @Test fun changesInOldSettingsKeepNewServerFieldsUnchanged() {
        val next = TvV1PlaybackPreferencesBridge.update(
            preferences(), ClientPlaybackPreferencesUpdate(autoplayNext = false),
        )
        assertFalse(next.autoplayNext)
        assertEquals(3, next.introSkipModeTypeId)
        assertEquals(2, next.outroSkipModeTypeId)
        assertFalse(next.autoplayPreviews)
        assertEquals("ja", next.audioLanguage)
        assertEquals("de", next.subtitleLanguage)
        assertEquals(BigDecimal("1.25"), next.speed)
    }

    @Test fun subtitleOffIsAnExplicitModeNotAnInvalidLanguage() {
        val off = TvV1PlaybackPreferencesBridge.update(
            preferences(), ClientPlaybackPreferencesUpdate(preferredSubtitleLanguage = "off"),
        )
        assertEquals(1, off.subtitleModeTypeId)
        assertNull(off.subtitleLanguage)
        assertEquals("off", TvV1PlaybackPreferencesBridge.display(
            preferences().copy(subtitleModeTypeId = 1),
        ).preferredSubtitleLanguage)
    }

    @Test fun languageAndPlaybackSpeedConvertWithoutTouchingSkipChoices() {
        val next = TvV1PlaybackPreferencesBridge.update(
            preferences(), ClientPlaybackPreferencesUpdate(
                preferredAudioLanguage = "en", defaultPlaybackSpeed = 1.5,
            ),
        )
        assertEquals("en", next.audioLanguage)
        assertEquals(BigDecimal("1.50"), next.speed)
        assertEquals(3, next.introSkipModeTypeId)
        assertEquals(2, next.outroSkipModeTypeId)
    }
}
