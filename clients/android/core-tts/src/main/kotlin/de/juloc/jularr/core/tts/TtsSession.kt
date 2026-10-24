package de.juloc.jularr.core.tts

/** Lifecycle/boundary events for a [TtsSession], key-tagged like the web `speakSequence`. */
sealed interface TtsEvent {
    data class ItemStarted(val key: String) : TtsEvent
    data class Boundary(val key: String, val rangeStart: Int, val rangeEnd: Int) : TtsEvent
    data class ItemEnded(val key: String) : TtsEvent
    data class ItemFailed(val key: String, val message: String) : TtsEvent
    data object QueueFinished : TtsEvent
    data object Stopped : TtsEvent
}

fun interface TtsEventListener {
    fun onEvent(event: TtsEvent)
}

/**
 * Drives one resolved [TtsProvider] through a queue of [SpeechItem]s, one utterance at a
 * time (mirrors `wwwroot/js/tts.js` `speakSequence`: only the current item is ever handed
 * to the platform, so Stop and pause/resume stay deterministic regardless of how many
 * items are queued).
 *
 * - `stop()` cancels the whole queue and the underlying provider utterance.
 * - `pause()` / `resume()` are emulated by stopping and restarting the current utterance
 *   from its beginning, because neither Android `TextToSpeech` nor most neural engines
 *   support resuming a partially spoken utterance (same rationale as the web Reader).
 *
 * All entry points (including the provider callbacks) are synchronized: a real engine like
 * Android `TextToSpeech` delivers `UtteranceProgressListener` callbacks on its own thread,
 * not necessarily the caller's thread.
 */
class TtsSession(private val listener: TtsEventListener = TtsEventListener {}) {
    private var provider: TtsProvider? = null
    private var resolution: SpeechResolution? = null
    private var queue: List<SpeechItem> = emptyList()
    private var currentIndex: Int = -1
    private var paused: Boolean = false
    private var stopped: Boolean = true

    // Bumped every time a new utterance attempt starts (including a pause/resume restart
    // of the same item), so a callback from an utterance we already asked the provider to
    // cancel can never be mistaken for the current one, even if the provider's stop() does
    // not synchronously guarantee no further callbacks.
    private var generation: Int = 0

    val isSpeaking: Boolean get() = !stopped && !paused && currentIndex in queue.indices
    val isPaused: Boolean get() = paused

    @Synchronized
    fun speakSequence(
        provider: TtsProvider,
        resolution: SpeechResolution,
        items: List<SpeechItem>,
    ) {
        stopInternal(emitStopped = false)
        if (items.isEmpty()) {
            listener.onEvent(TtsEvent.QueueFinished)
            return
        }

        this.provider = provider
        this.resolution = resolution
        queue = items
        currentIndex = 0
        paused = false
        stopped = false
        speakCurrent()
    }

    @Synchronized
    fun stop() {
        stopInternal(emitStopped = true)
    }

    @Synchronized
    fun pause() {
        if (stopped || paused || currentIndex !in queue.indices) {
            return
        }

        paused = true
        provider?.stop()
    }

    @Synchronized
    fun resume() {
        if (stopped || !paused) {
            return
        }

        paused = false
        if (currentIndex in queue.indices) {
            speakCurrent()
        }
    }

    private fun stopInternal(emitStopped: Boolean) {
        val wasActive = !stopped
        provider?.stop()
        provider = null
        resolution = null
        queue = emptyList()
        currentIndex = -1
        paused = false
        stopped = true
        if (emitStopped && wasActive) {
            listener.onEvent(TtsEvent.Stopped)
        }
    }

    private fun speakCurrent() {
        val activeProvider = provider ?: return
        val activeResolution = resolution ?: return
        val index = currentIndex
        if (index !in queue.indices) {
            return
        }

        generation += 1
        val expectedGeneration = generation
        val item = queue[index]

        activeProvider.speak(
            item,
            activeResolution,
            object : TtsProviderListener {
                override fun onStart(key: String) = synchronized(this@TtsSession) {
                    if (isCurrent(index, key, expectedGeneration)) {
                        listener.onEvent(TtsEvent.ItemStarted(key))
                    }
                }

                override fun onRangeStart(key: String, rangeStart: Int, rangeEnd: Int) =
                    synchronized(this@TtsSession) {
                        if (isCurrent(index, key, expectedGeneration)) {
                            listener.onEvent(TtsEvent.Boundary(key, rangeStart, rangeEnd))
                        }
                    }

                override fun onDone(key: String) = synchronized(this@TtsSession) {
                    if (!isCurrent(index, key, expectedGeneration)) {
                        return@synchronized
                    }

                    listener.onEvent(TtsEvent.ItemEnded(key))
                    advance()
                }

                override fun onError(key: String, message: String) = synchronized(this@TtsSession) {
                    if (!isCurrent(index, key, expectedGeneration)) {
                        return@synchronized
                    }

                    listener.onEvent(TtsEvent.ItemFailed(key, message))
                    advance()
                }
            },
        )
    }

    private fun isCurrent(index: Int, key: String, expectedGeneration: Int): Boolean =
        !stopped && !paused && generation == expectedGeneration &&
            currentIndex == index && queue.getOrNull(index)?.key == key

    @Synchronized
    private fun advance() {
        if (stopped || paused) {
            return
        }

        val nextIndex = currentIndex + 1
        if (nextIndex !in queue.indices) {
            stopInternal(emitStopped = false)
            listener.onEvent(TtsEvent.QueueFinished)
            return
        }

        currentIndex = nextIndex
        speakCurrent()
    }
}
