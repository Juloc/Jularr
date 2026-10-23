package de.juloc.jularr.tv

import de.juloc.jularr.core.api.playbackextras.TvV1PlaybackPreferences
import de.juloc.jularr.core.api.playbackextras.TvV1PlaybackPreferencesUpdate
import de.juloc.jularr.core.api.playbackextras.TvV1SubtitleModes
import de.juloc.jularr.core.model.ClientPlaybackPreferences
import de.juloc.jularr.core.model.ClientPlaybackPreferencesUpdate
import java.math.BigDecimal
import java.math.RoundingMode

/**
 * Original TV settings fields are only a subset of clean-start profile preferences.
 * Round-trip every unrelated server value unchanged, never replace with invented defaults.
 */
internal object TvV1PlaybackPreferencesBridge {
    fun display(value: TvV1PlaybackPreferences): ClientPlaybackPreferences =
        ClientPlaybackPreferences(
            autoplayNext = value.autoplayNext,
            preferredAudioLanguage = value.audioLanguage,
            preferredSubtitleLanguage =
                if (value.subtitleModeTypeId == TvV1SubtitleModes.OFF) "off" else value.subtitleLanguage,
            defaultPlaybackSpeed = value.speed.toDouble(),
        )

    fun update(
        current: TvV1PlaybackPreferences,
        change: ClientPlaybackPreferencesUpdate,
    ): TvV1PlaybackPreferencesUpdate {
        val subtitleInput = change.preferredSubtitleLanguage
        val newMode = when (subtitleInput?.lowercase()) {
            "off" -> TvV1SubtitleModes.OFF
            null -> current.subtitleModeTypeId
            else -> if (current.subtitleModeTypeId == TvV1SubtitleModes.OFF) TvV1SubtitleModes.ALWAYS
                    else current.subtitleModeTypeId
        }
        val newSubtitle = when (subtitleInput?.lowercase()) {
            null -> current.subtitleLanguage
            "off", "" -> null
            else -> subtitleInput
        }
        return current.editable().copy(
            autoplayNext = change.autoplayNext ?: current.autoplayNext,
            audioLanguage = change.preferredAudioLanguage?.takeUnless(String::isBlank)
                ?: if (change.preferredAudioLanguage != null) null else current.audioLanguage,
            subtitleLanguage = newSubtitle,
            subtitleModeTypeId = newMode,
            speed = change.defaultPlaybackSpeed?.let {
                BigDecimal.valueOf(it).setScale(2, RoundingMode.HALF_UP)
            } ?: current.speed,
        )
    }
}
