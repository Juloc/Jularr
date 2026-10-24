package de.juloc.jularr.core.tts

import android.content.Context
import android.os.Bundle
import android.speech.tts.TextToSpeech
import android.speech.tts.UtteranceProgressListener
import java.util.Locale
import kotlin.coroutines.resume
import kotlin.coroutines.suspendCoroutine

/**
 * Device/system TTS provider backed by Android `TextToSpeech`. Device voices may be
 * vendor/network-backed (a common OEM engine streams some voices from the network); only
 * an Jularr-managed offline-neural model is "guaranteed offline" (docs/TTS.md).
 *
 * - Voice discovery maps [Voice.getLocale] to a BCP-47 tag via [Locale.toLanguageTag].
 * - Stop cancels the whole underlying utterance queue ([TextToSpeech.stop]).
 * - Pause/resume are handled one level up by [TtsSession] (re-speak the current item from
 *   its start), because `TextToSpeech` has no reliable mid-utterance pause.
 */
class SystemTtsProvider(context: Context) : TtsProvider {
    override val id: String = ProviderId
    override val displayName: String = "Device"
    override val kind: SpeechProviderKind = SpeechProviderKind.DEVICE
    override val capabilities: Set<SpeechProviderCapability> = setOf(
        SpeechProviderCapability.PAUSE_RESUME,
        SpeechProviderCapability.BOUNDARY_EVENTS,
        SpeechProviderCapability.PLATFORM_DEFAULT_VOICE,
    )

    private val appContext = context.applicationContext
    private var engine: TextToSpeech? = null
    private var initialized = false
    private var activeListener: TtsProviderListener? = null

    private val progressListener = object : UtteranceProgressListener() {
        override fun onStart(utteranceId: String?) {
            utteranceId?.let { activeListener?.onStart(it) }
        }

        override fun onDone(utteranceId: String?) {
            utteranceId?.let { activeListener?.onDone(it) }
        }

        @Suppress("OVERRIDE_DEPRECATION")
        override fun onError(utteranceId: String?) {
            utteranceId?.let { activeListener?.onError(it, "Speech synthesis failed.") }
        }

        override fun onError(utteranceId: String?, errorCode: Int) {
            utteranceId?.let { activeListener?.onError(it, "Speech synthesis failed ($errorCode).") }
        }

        override fun onRangeStart(utteranceId: String?, start: Int, end: Int, frame: Int) {
            utteranceId?.let { activeListener?.onRangeStart(it, start, end) }
        }
    }

    /** Must be awaited before [isAvailable] or [availableVoices] report real state. */
    suspend fun initialize(): Boolean {
        if (initialized) {
            return engine != null
        }

        val result = suspendCoroutine<TextToSpeech?> { continuation ->
            val instance = arrayOfNulls<TextToSpeech>(1)
            instance[0] = TextToSpeech(appContext) { status ->
                continuation.resume(if (status == TextToSpeech.SUCCESS) instance[0] else null)
            }
        }

        initialized = true
        engine = result
        engine?.setOnUtteranceProgressListener(progressListener)
        return engine != null
    }

    override fun isAvailable(): Boolean = initialized && engine != null

    override suspend fun availableVoices(): List<SpeechVoiceDescriptor> {
        if (!initialize()) {
            return emptyList()
        }

        val activeEngine = engine ?: return emptyList()
        val defaultVoice = runCatching { activeEngine.defaultVoice }.getOrNull()
        val voices = runCatching { activeEngine.voices }.getOrNull().orEmpty()

        return voices
            .filter { voice ->
                !voice.features.orEmpty().contains(TextToSpeech.Engine.KEY_FEATURE_NOT_INSTALLED)
            }
            .mapNotNull { voice ->
                val languageTag = voice.locale?.toLanguageTag()?.takeIf { it != "und" } ?: return@mapNotNull null
                SpeechVoiceDescriptor(
                    providerId = id,
                    voiceId = voice.name,
                    displayName = voice.name,
                    language = SpeechPreferenceResolver.normalizeLanguageTag(languageTag),
                    isDefault = defaultVoice?.name == voice.name,
                    // Metadata only: not a universal privacy guarantee (docs/TTS.md).
                    isLocal = !voice.isNetworkConnectionRequired,
                )
            }
    }

    override fun speak(item: SpeechItem, resolution: SpeechResolution, listener: TtsProviderListener) {
        val activeEngine = engine
        if (activeEngine == null || !initialized) {
            listener.onError(item.key, "The device speech engine is not ready.")
            return
        }

        activeListener = listener
        activeEngine.language = Locale.forLanguageTag(resolution.language)
        activeEngine.setSpeechRate(resolution.rate.toFloat())
        activeEngine.setPitch(resolution.pitch.toFloat())

        if (resolution.voiceId != null) {
            val voice = activeEngine.voices?.firstOrNull { it.name == resolution.voiceId }
            if (voice != null) {
                activeEngine.voice = voice
            }
        }

        val params = Bundle().apply {
            putFloat(TextToSpeech.Engine.KEY_PARAM_VOLUME, resolution.volume.toFloat())
        }

        val speakResult = activeEngine.speak(item.text, TextToSpeech.QUEUE_FLUSH, params, item.key)
        if (speakResult != TextToSpeech.SUCCESS) {
            listener.onError(item.key, "The device speech engine rejected the request.")
        }
    }

    override fun stop() {
        engine?.stop()
    }

    override fun shutdown() {
        engine?.shutdown()
        engine = null
        initialized = false
        activeListener = null
    }

    companion object {
        const val ProviderId = "device"
    }
}
