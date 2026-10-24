package de.juloc.jularr.core.tts.sherpa

import android.media.AudioAttributes
import android.media.AudioFormat
import android.media.AudioTrack
import com.k2fsa.sherpa.onnx.GeneratedAudio
import com.k2fsa.sherpa.onnx.OfflineTts
import com.k2fsa.sherpa.onnx.OfflineTtsConfig
import com.k2fsa.sherpa.onnx.OfflineTtsModelConfig
import com.k2fsa.sherpa.onnx.OfflineTtsVitsModelConfig
import de.juloc.jularr.core.tts.NeuralTtsProvider
import de.juloc.jularr.core.tts.SpeechItem
import de.juloc.jularr.core.tts.SpeechProviderCapability
import de.juloc.jularr.core.tts.SpeechProviderKind
import de.juloc.jularr.core.tts.SpeechResolution
import de.juloc.jularr.core.tts.SpeechVoiceDescriptor
import de.juloc.jularr.core.tts.TtsProviderListener
import java.io.File
import java.util.concurrent.atomic.AtomicBoolean

/**
 * Offline-neural TTS via sherpa-onnx (docs/TTS.md, Phase 3). This module is only built
 * when explicitly opted into (see core-tts-sherpa/build.gradle.kts): it depends on a
 * manually downloaded AAR that is not part of this repository or CI.
 *
 * IMPORTANT: the `com.k2fsa.sherpa.onnx.*` class/method signatures below reflect the
 * public Kotlin API sherpa-onnx has documented for its Android sample app (VITS/Piper
 * style offline TTS: `OfflineTts` + `OfflineTtsConfig` + `generate(text, sid, speed)`
 * returning a `GeneratedAudio` of PCM float samples + sample rate). Verify them against
 * the exact AAR version you download before relying on this in production — sherpa-onnx
 * does not publish these as a versioned, guaranteed-stable API, and the manifest's
 * `minimumCompatibleVersion` field exists precisely so a mismatch can be caught before
 * activation rather than at synthesis time.
 */
class SherpaOnnxTtsProvider : NeuralTtsProvider {
    override val id: String = ProviderId
    override val displayName: String = "Offline neural"
    override val kind: SpeechProviderKind = SpeechProviderKind.OFFLINE_NEURAL
    override val capabilities: Set<SpeechProviderCapability> = setOf(
        SpeechProviderCapability.GUARANTEED_OFFLINE,
    )

    private var engine: OfflineTts? = null
    private var track: AudioTrack? = null
    private val cancelled = AtomicBoolean(false)

    override var activeModelId: String? = null
        private set

    override fun isAvailable(): Boolean = engine != null

    override fun activateModel(modelId: String, modelDirectory: File): Boolean {
        return try {
            engine?.release()

            // The exact file names below (model.onnx / tokens.txt) are the sherpa-onnx
            // VITS/Piper convention; a manifest entry's `files` list is the source of
            // truth for what actually gets downloaded into modelDirectory.
            val config = OfflineTtsConfig(
                model = OfflineTtsModelConfig(
                    vits = OfflineTtsVitsModelConfig(
                        model = File(modelDirectory, "model.onnx").absolutePath,
                        tokens = File(modelDirectory, "tokens.txt").absolutePath,
                    ),
                    numThreads = 1,
                    debug = false,
                ),
            )

            engine = OfflineTts(config = config)
            activeModelId = modelId
            true
        } catch (exception: Exception) {
            engine = null
            activeModelId = null
            false
        }
    }

    override suspend fun availableVoices(): List<SpeechVoiceDescriptor> = emptyList()

    override fun speak(item: SpeechItem, resolution: SpeechResolution, listener: TtsProviderListener) {
        val activeEngine = engine
        if (activeEngine == null) {
            listener.onError(item.key, "No offline-neural model is activated.")
            return
        }

        cancelled.set(false)
        listener.onStart(item.key)

        try {
            val audio: GeneratedAudio = activeEngine.generate(
                text = item.text,
                sid = 0,
                speed = resolution.rate.toFloat(),
            )

            if (cancelled.get()) {
                return
            }

            playSamples(audio.samples, audio.sampleRate, resolution.volume.toFloat())

            if (!cancelled.get()) {
                listener.onDone(item.key)
            }
        } catch (exception: Exception) {
            listener.onError(item.key, exception.message ?: "Offline-neural synthesis failed.")
        }
    }

    override fun stop() {
        cancelled.set(true)
        track?.let { activeTrack ->
            runCatching { activeTrack.pause() }
            runCatching { activeTrack.flush() }
            runCatching { activeTrack.stop() }
            runCatching { activeTrack.release() }
        }
        track = null
    }

    override fun shutdown() {
        stop()
        engine?.release()
        engine = null
        activeModelId = null
    }

    private fun playSamples(samples: FloatArray, sampleRate: Int, volume: Float) {
        val minBufferSize = AudioTrack.getMinBufferSize(
            sampleRate,
            AudioFormat.CHANNEL_OUT_MONO,
            AudioFormat.ENCODING_PCM_FLOAT,
        )

        val audioTrack = AudioTrack.Builder()
            .setAudioAttributes(
                AudioAttributes.Builder()
                    .setUsage(AudioAttributes.USAGE_MEDIA)
                    .setContentType(AudioAttributes.CONTENT_TYPE_SPEECH)
                    .build(),
            )
            .setAudioFormat(
                AudioFormat.Builder()
                    .setEncoding(AudioFormat.ENCODING_PCM_FLOAT)
                    .setSampleRate(sampleRate)
                    .setChannelMask(AudioFormat.CHANNEL_OUT_MONO)
                    .build(),
            )
            .setBufferSizeInBytes(maxOf(minBufferSize, samples.size * 4))
            .setTransferMode(AudioTrack.MODE_STATIC)
            .build()

        track = audioTrack
        audioTrack.setVolume(volume.coerceIn(0f, 1f))
        audioTrack.write(samples, 0, samples.size, AudioTrack.WRITE_BLOCKING)
        audioTrack.play()

        val durationMs = (samples.size.toDouble() / sampleRate * 1000).toLong()
        var waited = 0L
        while (!cancelled.get() && waited < durationMs) {
            Thread.sleep(20)
            waited += 20
        }
    }

    companion object {
        const val ProviderId = "offline-neural-sherpa-onnx"
    }
}
