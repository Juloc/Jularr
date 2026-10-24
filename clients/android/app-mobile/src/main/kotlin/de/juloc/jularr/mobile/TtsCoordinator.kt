package de.juloc.jularr.mobile

import android.content.Context
import de.juloc.jularr.core.api.JularrClientApi
import de.juloc.jularr.core.model.TtsPreferences
import de.juloc.jularr.core.tts.SpeechItem
import de.juloc.jularr.core.tts.SpeechPreferenceResolver
import de.juloc.jularr.core.tts.SpeechPreferences
import de.juloc.jularr.core.tts.SpeechProviderDescriptor
import de.juloc.jularr.core.tts.SpeechProviderKind
import de.juloc.jularr.core.tts.SpeechVoiceDescriptor
import de.juloc.jularr.core.tts.SystemTtsProvider
import de.juloc.jularr.core.tts.TtsEvent
import de.juloc.jularr.core.tts.TtsSession
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch
import java.io.Closeable

data class TtsUiState(
    val isSpeaking: Boolean = false,
    val isPaused: Boolean = false,
    val speakingKey: String? = null,
    val error: String? = null,
)

/**
 * Bridges `core-tts` to one native surface (the player's learning sheet today). Resolves
 * a speech request with the shared [SpeechPreferenceResolver] against the device
 * provider's discovered voices and the profile's synced [TtsPreferences], then drives a
 * [TtsSession]. Spoken text is only ever handed to the local TTS engine: never persisted,
 * never sent to the server (docs/TTS.md).
 */
class TtsCoordinator(
    context: Context,
    private val api: JularrClientApi,
) : Closeable {
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main.immediate)
    private val systemProvider = SystemTtsProvider(context)
    private val session = TtsSession { event -> handleEvent(event) }

    private val _state = MutableStateFlow(TtsUiState())
    val state: StateFlow<TtsUiState> = _state.asStateFlow()

    @Volatile private var preferences: TtsPreferences? = null
    @Volatile private var deviceVoices: List<SpeechVoiceDescriptor> = emptyList()

    init {
        scope.launch {
            systemProvider.initialize()
            deviceVoices = runCatching { systemProvider.availableVoices() }.getOrDefault(emptyList())
        }
        refreshPreferences()
    }

    fun refreshPreferences() {
        scope.launch {
            preferences = runCatching { api.getTtsPreferences() }.getOrNull()
        }
    }

    /** Speaks one piece of text (a subtitle line or a single looked-up word). */
    fun speak(key: String, text: String, language: String) {
        if (text.isBlank()) {
            return
        }

        val prefs = preferences
        val normalizedLanguage = SpeechPreferenceResolver.normalizeLanguageTag(language)
        val voiceId = prefs?.voiceIds?.get(normalizedLanguage)
            ?: prefs?.voiceIds?.get(SpeechPreferenceResolver.baseLanguage(normalizedLanguage))

        val resolved = SpeechPreferenceResolver.resolve(
            SpeechPreferences(
                providerId = prefs?.providerId,
                voiceId = voiceId,
                language = language,
                rate = prefs?.rate ?: 1.0,
                pitch = prefs?.pitch ?: 1.0,
                volume = prefs?.volume ?: 1.0,
            ),
            providers(),
            deviceVoices,
        )

        if (resolved == null) {
            _state.update { it.copy(error = "No speech provider is available on this device.") }
            return
        }

        session.speakSequence(systemProvider, resolved, listOf(SpeechItem(key, text)))
    }

    fun stop() = session.stop()

    fun pauseOrResume() {
        val current = _state.value
        when {
            current.isPaused -> {
                session.resume()
                _state.update { it.copy(isPaused = false) }
            }
            current.isSpeaking -> {
                session.pause()
                _state.update { it.copy(isPaused = true) }
            }
        }
    }

    fun clearError() {
        _state.update { it.copy(error = null) }
    }

    private fun providers(): List<SpeechProviderDescriptor> = listOf(
        SpeechProviderDescriptor(
            id = SystemTtsProvider.ProviderId,
            displayName = "Device",
            kind = SpeechProviderKind.DEVICE,
            isAvailable = systemProvider.isAvailable(),
            capabilities = systemProvider.capabilities,
        ),
    )

    private fun handleEvent(event: TtsEvent) {
        _state.update { current ->
            when (event) {
                is TtsEvent.ItemStarted -> current.copy(
                    isSpeaking = true,
                    isPaused = false,
                    speakingKey = event.key,
                    error = null,
                )
                is TtsEvent.Boundary -> current
                is TtsEvent.ItemEnded -> current.copy(isSpeaking = false, speakingKey = null)
                is TtsEvent.ItemFailed -> current.copy(
                    isSpeaking = false,
                    speakingKey = null,
                    error = event.message,
                )
                TtsEvent.QueueFinished -> current.copy(isSpeaking = false, isPaused = false, speakingKey = null)
                TtsEvent.Stopped -> current.copy(isSpeaking = false, isPaused = false, speakingKey = null)
            }
        }
    }

    override fun close() {
        session.stop()
        systemProvider.shutdown()
        scope.cancel()
    }
}
