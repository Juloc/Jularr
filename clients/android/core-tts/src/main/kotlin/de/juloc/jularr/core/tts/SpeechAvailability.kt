package de.juloc.jularr.core.tts

data class SpeechAvailability(
    val resolution: SpeechResolution?,
    val unavailableReason: String?,
) {
    val isAvailable: Boolean get() = resolution != null

    companion object {
        const val NO_PROVIDER = "no-provider"
        const val PROVIDER_UNAVAILABLE = "provider-unavailable"
        const val NO_VOICE_FOR_LANGUAGE = "no-voice-for-language"
    }
}

/**
 * Same deterministic order as [SpeechPreferenceResolver.resolve] but always explains why
 * nothing could be resolved, mirroring the server's `SpeechAvailabilityResolver` so the
 * mobile settings screen can show a useful state.
 */
object SpeechAvailabilityResolver {
    fun explain(
        preferences: SpeechPreferences,
        providers: Collection<SpeechProviderDescriptor>,
        voices: Collection<SpeechVoiceDescriptor>,
    ): SpeechAvailability {
        val resolution = SpeechPreferenceResolver.resolve(preferences, providers, voices)
        if (resolution != null) {
            return SpeechAvailability(resolution, null)
        }

        if (providers.isEmpty()) {
            return SpeechAvailability(null, SpeechAvailability.NO_PROVIDER)
        }

        return if (providers.any { it.isAvailable }) {
            SpeechAvailability(null, SpeechAvailability.NO_VOICE_FOR_LANGUAGE)
        } else {
            SpeechAvailability(null, SpeechAvailability.PROVIDER_UNAVAILABLE)
        }
    }
}
