package de.juloc.jularr.core.tts

/** One item in a spoken queue: a caller-defined key (sentence/cue/word id) plus its text. */
data class SpeechItem(val key: String, val text: String)

/** Lifecycle and boundary callbacks for one queued [SpeechItem]. */
interface TtsProviderListener {
    fun onStart(key: String)

    /** Word/range boundary, when the provider reports one. Offsets are into the item's text. */
    fun onRangeStart(key: String, rangeStart: Int, rangeEnd: Int) {}

    fun onDone(key: String)
    fun onError(key: String, message: String)
}

/**
 * One TTS engine binding: system/device (Android `TextToSpeech`) or offline-neural
 * (sherpa-onnx). Mirrors the provider-neutral shape of the web `DeviceSpeechProvider` /
 * server `SpeechProviderDescriptor`.
 */
interface TtsProvider {
    val id: String
    val displayName: String
    val kind: SpeechProviderKind
    val capabilities: Set<SpeechProviderCapability>

    fun isAvailable(): Boolean

    suspend fun availableVoices(): List<SpeechVoiceDescriptor>

    /**
     * Speaks exactly one item using an already-resolved [SpeechResolution]. Implementations
     * must call exactly one of [TtsProviderListener.onDone] / [TtsProviderListener.onError]
     * per call, after any [TtsProviderListener.onStart] calls.
     */
    fun speak(item: SpeechItem, resolution: SpeechResolution, listener: TtsProviderListener)

    /** Cancels whatever this provider is currently speaking or queuing internally. */
    fun stop()

    /** Releases engine resources. The provider is not used again after this call. */
    fun shutdown()
}
