package de.juloc.jularr.core.tts

/**
 * Offline-neural TTS provider contract (sherpa-onnx today; docs/TTS.md Phase 3). A real
 * binding lives in the optional `core-tts-sherpa` module, which depends on a manually
 * downloaded sherpa-onnx AAR (it is not published to Maven Central as of this writing —
 * see k2-fsa/sherpa-onnx#3981) and is only included in the Gradle build when the
 * `jularrNeuralTtsEnabled` Gradle property is set. See docs/TTS.md for the manual step.
 *
 * [UnavailableNeuralTtsProvider] is the always-compiled default: it reports the offline
 * provider as unavailable so the rest of the app (settings screen, resolver, model
 * manager) keeps working with zero models installed and no neural runtime present.
 */
interface NeuralTtsProvider : TtsProvider {
    /** The installed model this provider currently speaks with, if any. */
    val activeModelId: String?

    /** Switches to a different installed model (see `TtsModelManager`). */
    fun activateModel(modelId: String, modelDirectory: java.io.File): Boolean
}

class UnavailableNeuralTtsProvider : NeuralTtsProvider {
    override val id: String = ProviderId
    override val displayName: String = "Offline neural"
    override val kind: SpeechProviderKind = SpeechProviderKind.OFFLINE_NEURAL
    override val capabilities: Set<SpeechProviderCapability> = emptySet()
    override val activeModelId: String? = null

    override fun isAvailable(): Boolean = false

    override suspend fun availableVoices(): List<SpeechVoiceDescriptor> = emptyList()

    override fun speak(item: SpeechItem, resolution: SpeechResolution, listener: TtsProviderListener) {
        listener.onError(item.key, "No offline-neural TTS runtime is available in this build.")
    }

    override fun stop() = Unit

    override fun shutdown() = Unit

    override fun activateModel(modelId: String, modelDirectory: java.io.File): Boolean = false

    companion object {
        const val ProviderId = "offline-neural"
    }
}
