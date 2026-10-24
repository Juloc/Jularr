package de.juloc.jularr.core.tts

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class SpeechAvailabilityResolverTest {
    private val device = SpeechProviderDescriptor(
        "device",
        "Device",
        SpeechProviderKind.DEVICE,
        isAvailable = true,
        capabilities = setOf(SpeechProviderCapability.PLATFORM_DEFAULT_VOICE),
    )
    private val voices = listOf(SpeechVoiceDescriptor("device", "de-voice", "Deutsch", "de-DE"))

    @Test
    fun noProviderIsReportedWhenNoneAreConfigured() {
        val result = SpeechAvailabilityResolver.explain(SpeechPreferences(language = "de"), emptyList(), voices)
        assertFalse(result.isAvailable)
        assertEquals(SpeechAvailability.NO_PROVIDER, result.unavailableReason)
    }

    @Test
    fun providerUnavailableWhenTheOnlyProviderIsDown() {
        val result = SpeechAvailabilityResolver.explain(
            SpeechPreferences(language = "de"),
            listOf(device.copy(isAvailable = false)),
            voices,
        )
        assertFalse(result.isAvailable)
        assertEquals(SpeechAvailability.PROVIDER_UNAVAILABLE, result.unavailableReason)
    }

    @Test
    fun noVoiceForLanguageWhenAvailableProviderHasNoCompatibleVoiceOrDefault() {
        val result = SpeechAvailabilityResolver.explain(
            SpeechPreferences(language = "ko"),
            listOf(device.copy(capabilities = emptySet())),
            voices,
        )
        assertFalse(result.isAvailable)
        assertEquals(SpeechAvailability.NO_VOICE_FOR_LANGUAGE, result.unavailableReason)
    }

    @Test
    fun availableWhenAResolutionExists() {
        val result = SpeechAvailabilityResolver.explain(SpeechPreferences(language = "de-DE"), listOf(device), voices)
        assertTrue(result.isAvailable)
        assertEquals("de-voice", result.resolution!!.voiceId)
    }
}
