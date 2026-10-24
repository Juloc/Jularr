package de.juloc.jularr.core.tts

/**
 * Provider-neutral TTS contract. This is the Android mirror of the server contract in
 * docs/TTS.md / `Jularr.Web.Features.Speech.SpeechModels`: same semantics and field
 * names, ported rather than reused because device voices and platform TTS engines only
 * exist on the client.
 */
enum class SpeechProviderKind {
    OFFLINE_NEURAL,
    DEVICE,
    CLOUD,
}

enum class SpeechProviderCapability {
    PAUSE_RESUME,
    BOUNDARY_EVENTS,
    PLATFORM_DEFAULT_VOICE,
    GUARANTEED_OFFLINE,
}

data class SpeechProviderDescriptor(
    val id: String,
    val displayName: String,
    val kind: SpeechProviderKind,
    val isAvailable: Boolean,
    val capabilities: Set<SpeechProviderCapability> = emptySet(),
)

data class SpeechVoiceDescriptor(
    val providerId: String,
    val voiceId: String,
    val displayName: String,
    val language: String,
    val isDefault: Boolean = false,
    val isLocal: Boolean = false,
)

data class SpeechPreferences(
    val providerId: String? = null,
    val voiceId: String? = null,
    val language: String? = null,
    val rate: Double = 1.0,
    val pitch: Double = 1.0,
    val volume: Double = 1.0,
)

data class SpeechResolution(
    val providerId: String,
    val voiceId: String?,
    val language: String,
    val rate: Double,
    val pitch: Double,
    val volume: Double,
    val usesProviderDefaultVoice: Boolean,
    val reason: String,
)
