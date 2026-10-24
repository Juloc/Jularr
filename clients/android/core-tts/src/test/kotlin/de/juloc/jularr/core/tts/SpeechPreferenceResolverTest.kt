package de.juloc.jularr.core.tts

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * Mirrors `Jularr.Tests.SpeechPreferenceResolverTests` (server, C#) case-for-case: the
 * Android and server resolvers must agree given the same inputs (docs/TTS.md).
 */
class SpeechPreferenceResolverTest {

    @Test
    fun explicitCompatibleVoiceWins() {
        val result = SpeechPreferenceResolver.resolve(
            SpeechPreferences(providerId = "device", voiceId = "ja-selected", language = "ja-JP", rate = 1.2),
            providers(),
            listOf(
                SpeechVoiceDescriptor("device", "ja-default", "Japanese Default", "ja-JP", isDefault = true),
                SpeechVoiceDescriptor("device", "ja-selected", "Japanese Selected", "ja-JP"),
            ),
        )

        assertNotNull(result)
        assertEquals("device", result!!.providerId)
        assertEquals("ja-selected", result.voiceId)
        assertEquals("selected-provider", result.reason)
        assertEquals(1.2, result.rate, 0.0)
    }

    @Test
    fun exactLanguageBeatsBaseLanguage() {
        val result = SpeechPreferenceResolver.resolve(
            SpeechPreferences(language = "de-DE"),
            providers(),
            listOf(
                SpeechVoiceDescriptor("offline", "de-at", "Deutsch AT", "de-AT", isDefault = true),
                SpeechVoiceDescriptor("offline", "de-de", "Deutsch DE", "de-DE"),
            ),
        )

        assertNotNull(result)
        assertEquals("offline", result!!.providerId)
        assertEquals("de-de", result.voiceId)
        assertEquals("offline-neural-fallback", result.reason)
    }

    @Test
    fun providerFallbackOrderIsOfflineThenDeviceThenCloud() {
        val result = SpeechPreferenceResolver.resolve(
            SpeechPreferences(language = "id-ID"),
            providers(),
            listOf(
                SpeechVoiceDescriptor("cloud", "id-cloud", "Cloud Indonesian", "id-ID"),
                SpeechVoiceDescriptor("device", "id-device", "Device Indonesian", "id-ID"),
                SpeechVoiceDescriptor("offline", "id-offline", "Offline Indonesian", "id-ID"),
            ),
        )

        assertNotNull(result)
        assertEquals("offline", result!!.providerId)
        assertEquals("id-offline", result.voiceId)
    }

    @Test
    fun unavailableSelectedProviderFallsBackDeterministically() {
        val providers = providers().map { if (it.id == "offline") it.copy(isAvailable = false) else it }

        val result = SpeechPreferenceResolver.resolve(
            SpeechPreferences(providerId = "offline", voiceId = "missing", language = "ja-JP"),
            providers,
            listOf(SpeechVoiceDescriptor("device", "ja-device", "Device Japanese", "ja")),
        )

        assertNotNull(result)
        assertEquals("device", result!!.providerId)
        assertEquals("ja-device", result.voiceId)
        assertEquals("device-fallback", result.reason)
    }

    @Test
    fun deviceProviderUsesPlatformDefaultInsteadOfWrongLanguageVoice() {
        val result = SpeechPreferenceResolver.resolve(
            SpeechPreferences(language = "ja-JP"),
            providers(offlineAvailable = false),
            listOf(
                SpeechVoiceDescriptor("device", "de-device", "German", "de-DE", isDefault = true),
                SpeechVoiceDescriptor("cloud", "ja-cloud", "Japanese Cloud", "ja-JP"),
            ),
        )

        assertNotNull(result)
        assertEquals("device", result!!.providerId)
        assertNull(result.voiceId)
        assertTrue(result.usesProviderDefaultVoice)
    }

    @Test
    fun languageAndProsodyAreNormalized() {
        assertEquals("ja-JP", SpeechPreferenceResolver.normalizeLanguageTag("JA_jp"))
        assertEquals("zh-Hant-TW", SpeechPreferenceResolver.normalizeLanguageTag("ZH_hant_tw"))
        assertEquals("und", SpeechPreferenceResolver.normalizeLanguageTag("bad tag!"))

        val result = SpeechPreferenceResolver.resolve(
            SpeechPreferences(language = "de_de", rate = 100.0, pitch = -2.0, volume = Double.NaN),
            providers(offlineAvailable = false),
            emptyList(),
        )

        assertNotNull(result)
        assertEquals("de-DE", result!!.language)
        assertEquals(2.5, result.rate, 0.0)
        assertEquals(.5, result.pitch, 0.0)
        assertEquals(1.0, result.volume, 0.0)
    }

    private fun providers(offlineAvailable: Boolean = true) = listOf(
        SpeechProviderDescriptor(
            "offline",
            "Offline neural",
            SpeechProviderKind.OFFLINE_NEURAL,
            offlineAvailable,
            setOf(
                SpeechProviderCapability.PAUSE_RESUME,
                SpeechProviderCapability.BOUNDARY_EVENTS,
                SpeechProviderCapability.GUARANTEED_OFFLINE,
            ),
        ),
        SpeechProviderDescriptor(
            "device",
            "Device",
            SpeechProviderKind.DEVICE,
            true,
            setOf(
                SpeechProviderCapability.PAUSE_RESUME,
                SpeechProviderCapability.BOUNDARY_EVENTS,
                SpeechProviderCapability.PLATFORM_DEFAULT_VOICE,
            ),
        ),
        SpeechProviderDescriptor(
            "cloud",
            "Cloud",
            SpeechProviderKind.CLOUD,
            true,
            setOf(SpeechProviderCapability.PAUSE_RESUME, SpeechProviderCapability.BOUNDARY_EVENTS),
        ),
    )
}
