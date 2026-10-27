package de.juloc.jularr.core.tts

import kotlin.math.round

/**
 * Deterministic voice/provider resolution, ported field-for-field and case-for-case from
 * `Jularr.Web.Features.Speech.SpeechPreferenceResolver` (docs/TTS.md). Keep this in sync
 * with the C# resolver: the two must always agree given the same inputs.
 */
object SpeechPreferenceResolver {

    fun resolve(
        preferences: SpeechPreferences,
        providers: Collection<SpeechProviderDescriptor>,
        voices: Collection<SpeechVoiceDescriptor>,
    ): SpeechResolution? {
        val language = normalizeLanguageTag(preferences.language)
        val requestedProvider = normalizeId(preferences.providerId)
        val requestedVoice = normalizeId(preferences.voiceId)
        val available = providers
            .filter { it.isAvailable }
            .sortedWith(compareBy({ providerRank(it.kind) }, { it.id }))

        if (available.isEmpty()) {
            return null
        }

        if (requestedProvider != null) {
            val explicitProvider = available.firstOrNull {
                it.id.equals(requestedProvider, ignoreCase = true)
            }

            if (explicitProvider != null) {
                val explicitResolution = resolveWithinProvider(
                    explicitProvider,
                    voices,
                    requestedVoice,
                    language,
                    preferences,
                )

                if (explicitResolution != null) {
                    return explicitResolution.copy(reason = "selected-provider")
                }
            }
        } else if (requestedVoice != null) {
            // A voice chosen without a provider ("automatic") still wins when an
            // available provider offers it for this language.
            for (provider in available) {
                val selected = voices.firstOrNull {
                    it.providerId.equals(provider.id, ignoreCase = true) &&
                        it.voiceId.equals(requestedVoice, ignoreCase = true)
                }

                if (selected != null && isLanguageCompatible(selected.language, language)) {
                    return build(provider, selected, language, preferences, false, "selected-voice")
                }
            }
        }

        for (provider in available) {
            val resolution = resolveWithinProvider(
                provider,
                voices,
                if (requestedProvider != null && provider.id.equals(requestedProvider, ignoreCase = true)) {
                    requestedVoice
                } else {
                    null
                },
                language,
                preferences,
            )

            if (resolution != null) {
                return resolution.copy(reason = providerFallbackReason(provider.kind))
            }
        }

        return null
    }

    fun normalizeLanguageTag(value: String?): String {
        val raw = value?.trim()?.replace('_', '-')
        if (raw.isNullOrBlank()) {
            return "und"
        }

        val parts = raw.split('-').filter { it.isNotEmpty() }.toMutableList()
        if (parts.isEmpty() ||
            parts.any { part -> part.length !in 1..8 || part.any { !it.isLetterOrDigit() } }
        ) {
            return "und"
        }

        parts[0] = parts[0].lowercase()

        for (i in 1 until parts.size) {
            val part = parts[i]
            parts[i] = when {
                part.length == 4 && part.all { it.isLetter() } ->
                    part[0].uppercaseChar() + part.substring(1).lowercase()
                (part.length == 2 && part.all { it.isLetter() }) ||
                    (part.length == 3 && part.all { it.isDigit() }) -> part.uppercase()
                else -> part.lowercase()
            }
        }

        val normalized = parts.joinToString("-")
        return if (normalized.length <= 64) normalized else "und"
    }

    fun baseLanguage(value: String?): String {
        val normalized = normalizeLanguageTag(value)
        val separator = normalized.indexOf('-')
        return if (separator < 0) normalized else normalized.substring(0, separator)
    }

    fun normalizeRate(value: Double): Double =
        roundTo2((if (value.isFinite()) value else 1.0).coerceIn(.5, 2.5))

    fun normalizePitch(value: Double): Double =
        roundTo2((if (value.isFinite()) value else 1.0).coerceIn(.5, 2.0))

    fun normalizeVolume(value: Double): Double =
        roundTo2((if (value.isFinite()) value else 1.0).coerceIn(0.0, 1.0))

    private fun roundTo2(value: Double): Double = round(value * 100) / 100

    private fun resolveWithinProvider(
        provider: SpeechProviderDescriptor,
        allVoices: Collection<SpeechVoiceDescriptor>,
        requestedVoice: String?,
        language: String,
        preferences: SpeechPreferences,
    ): SpeechResolution? {
        val providerVoices = allVoices.filter { it.providerId.equals(provider.id, ignoreCase = true) }

        if (requestedVoice != null) {
            val selected = providerVoices.firstOrNull {
                it.voiceId.equals(requestedVoice, ignoreCase = true)
            }

            if (selected != null && isLanguageCompatible(selected.language, language)) {
                return build(provider, selected, language, preferences, false, "selected-voice")
            }
        }

        val exact = providerVoices
            .filter { languageRank(it.language, language) == 0 }
            .sortedWith(
                compareByDescending<SpeechVoiceDescriptor> { it.isDefault }
                    .thenBy(String.CASE_INSENSITIVE_ORDER) { it.displayName }
                    .thenBy { it.voiceId },
            )
            .firstOrNull()

        if (exact != null) {
            return build(provider, exact, language, preferences, false, "exact-language")
        }

        val baseMatch = providerVoices
            .filter { languageRank(it.language, language) == 1 }
            .sortedWith(
                compareByDescending<SpeechVoiceDescriptor> { it.isDefault }
                    .thenBy(String.CASE_INSENSITIVE_ORDER) { it.displayName }
                    .thenBy { it.voiceId },
            )
            .firstOrNull()

        if (baseMatch != null) {
            return build(provider, baseMatch, language, preferences, false, "base-language")
        }

        if (provider.capabilities.contains(SpeechProviderCapability.PLATFORM_DEFAULT_VOICE)) {
            return build(provider, null, language, preferences, true, "platform-default")
        }

        return null
    }

    private fun build(
        provider: SpeechProviderDescriptor,
        voice: SpeechVoiceDescriptor?,
        language: String,
        preferences: SpeechPreferences,
        usesProviderDefaultVoice: Boolean,
        reason: String,
    ) = SpeechResolution(
        providerId = provider.id,
        voiceId = voice?.voiceId,
        language = language,
        rate = normalizeRate(preferences.rate),
        pitch = normalizePitch(preferences.pitch),
        volume = normalizeVolume(preferences.volume),
        usesProviderDefaultVoice = usesProviderDefaultVoice,
        reason = reason,
    )

    private fun isLanguageCompatible(voiceLanguage: String, requestedLanguage: String): Boolean =
        languageRank(voiceLanguage, requestedLanguage) <= 1 ||
            requestedLanguage.equals("und", ignoreCase = true)

    private fun languageRank(voiceLanguage: String, requestedLanguage: String): Int {
        val voice = normalizeLanguageTag(voiceLanguage)
        val requested = normalizeLanguageTag(requestedLanguage)

        if (requested.equals("und", ignoreCase = true)) {
            return 0
        }

        if (voice.equals(requested, ignoreCase = true)) {
            return 0
        }

        return if (baseLanguage(voice).equals(baseLanguage(requested), ignoreCase = true)) 1 else 2
    }

    private fun providerRank(kind: SpeechProviderKind): Int = when (kind) {
        SpeechProviderKind.OFFLINE_NEURAL -> 0
        SpeechProviderKind.DEVICE -> 1
        SpeechProviderKind.CLOUD -> 2
    }

    private fun providerFallbackReason(kind: SpeechProviderKind): String = when (kind) {
        SpeechProviderKind.OFFLINE_NEURAL -> "offline-neural-fallback"
        SpeechProviderKind.DEVICE -> "device-fallback"
        SpeechProviderKind.CLOUD -> "cloud-fallback"
    }

    private fun normalizeId(value: String?): String? {
        val normalized = value?.trim()
        return if (normalized.isNullOrBlank()) null else normalized
    }
}
