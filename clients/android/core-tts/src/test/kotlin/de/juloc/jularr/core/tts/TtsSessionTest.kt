package de.juloc.jularr.core.tts

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

private class FakeProvider : TtsProvider {
    override val id = "fake"
    override val displayName = "Fake"
    override val kind = SpeechProviderKind.DEVICE
    override val capabilities = emptySet<SpeechProviderCapability>()

    var stopCount = 0
    var speakCalls = mutableListOf<String>()
    private var pendingListener: TtsProviderListener? = null
    private var pendingKey: String? = null

    override fun isAvailable() = true
    override suspend fun availableVoices() = emptyList<SpeechVoiceDescriptor>()

    override fun speak(item: SpeechItem, resolution: SpeechResolution, listener: TtsProviderListener) {
        speakCalls.add(item.key)
        pendingListener = listener
        pendingKey = item.key
        listener.onStart(item.key)
    }

    /** Test hook: simulate the engine finishing the current utterance. */
    fun completeCurrent() {
        val listener = pendingListener
        val key = pendingKey
        if (listener != null && key != null) {
            listener.onDone(key)
        }
    }

    override fun stop() {
        stopCount++
        pendingListener = null
        pendingKey = null
    }

    override fun shutdown() = Unit
}

private val resolution = SpeechResolution(
    providerId = "fake",
    voiceId = null,
    language = "ja-JP",
    rate = 1.0,
    pitch = 1.0,
    volume = 1.0,
    usesProviderDefaultVoice = true,
    reason = "platform-default",
)

class TtsSessionTest {
    @Test
    fun speaksItemsInOrderAndAdvancesOnDone() {
        val events = mutableListOf<TtsEvent>()
        val session = TtsSession { events.add(it) }
        val provider = FakeProvider()

        session.speakSequence(
            provider,
            resolution,
            listOf(SpeechItem("a", "Hello"), SpeechItem("b", "World")),
        )

        assertEquals(listOf("a"), provider.speakCalls)
        provider.completeCurrent()
        assertEquals(listOf("a", "b"), provider.speakCalls)
        provider.completeCurrent()

        assertTrue(events.contains(TtsEvent.ItemStarted("a")))
        assertTrue(events.contains(TtsEvent.ItemEnded("a")))
        assertTrue(events.contains(TtsEvent.ItemEnded("b")))
        assertEquals(TtsEvent.QueueFinished, events.last())
    }

    @Test
    fun stopCancelsTheWholeQueueNotJustTheCurrentItem() {
        val events = mutableListOf<TtsEvent>()
        val session = TtsSession { events.add(it) }
        val provider = FakeProvider()

        session.speakSequence(
            provider,
            resolution,
            listOf(SpeechItem("a", "Hello"), SpeechItem("b", "World"), SpeechItem("c", "!")),
        )
        session.stop()

        assertEquals(1, provider.stopCount)
        assertFalse(session.isSpeaking)
        assertEquals(TtsEvent.Stopped, events.last())

        // Completing the (now-cancelled) first utterance must not resurrect the queue.
        provider.completeCurrent()
        assertEquals(
            "Stop must prevent any further items from speaking.",
            listOf("a"),
            provider.speakCalls,
        )
    }

    @Test
    fun pauseStopsTheEngineAndResumeRestartsTheCurrentItemFromScratch() {
        val events = mutableListOf<TtsEvent>()
        val session = TtsSession { events.add(it) }
        val provider = FakeProvider()

        session.speakSequence(provider, resolution, listOf(SpeechItem("a", "Hello")))
        session.pause()

        assertTrue(session.isPaused)
        assertEquals("Pause stops the underlying utterance.", 1, provider.stopCount)

        session.resume()

        assertFalse(session.isPaused)
        assertEquals(
            "Resume restarts the current item from its start.",
            listOf("a", "a"),
            provider.speakCalls,
        )
    }

    @Test
    fun emptyQueueFinishesImmediately() {
        val events = mutableListOf<TtsEvent>()
        val session = TtsSession { events.add(it) }
        val provider = FakeProvider()

        session.speakSequence(provider, resolution, emptyList())

        assertEquals(listOf(TtsEvent.QueueFinished), events)
        assertTrue(provider.speakCalls.isEmpty())
    }
}
