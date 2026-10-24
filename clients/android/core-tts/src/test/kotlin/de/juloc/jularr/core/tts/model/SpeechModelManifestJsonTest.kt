package de.juloc.jularr.core.tts.model

import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

class SpeechModelManifestJsonTest {
    private val validSha256 = "a".repeat(64)

    @Test
    fun blankOrInvalidJsonYieldsAnEmptyManifest() {
        assertEquals(0, SpeechModelManifestJson.parse(null).entries.size)
        assertEquals(0, SpeechModelManifestJson.parse("").entries.size)
        assertEquals(0, SpeechModelManifestJson.parse("not json").entries.size)
        assertEquals(0, SpeechModelManifestJson.parse("""{"models":[]}""").entries.size)
    }

    @Test
    fun validEntryParsesLanguagesVoicesAndTotalSize() {
        val json = """
            {
              "models": [
                {
                  "providerId": "sherpa-onnx",
                  "modelId": "piper-de-thorsten",
                  "version": "1.0.0",
                  "languages": ["de_DE"],
                  "voices": ["thorsten"],
                  "files": [
                    { "name": "model.onnx", "url": "https://example.invalid/model.onnx", "sizeBytes": 1000, "sha256": "$validSha256" },
                    { "name": "tokens.txt", "url": "https://example.invalid/tokens.txt", "sizeBytes": 200, "sha256": "$validSha256" }
                  ],
                  "minimumCompatibleVersion": "0.5.0"
                }
              ]
            }
        """.trimIndent()

        val manifest = SpeechModelManifestJson.parse(json)

        assertEquals(1, manifest.entries.size)
        val entry = manifest.entries[0]
        assertEquals("sherpa-onnx", entry.providerId)
        assertEquals("piper-de-thorsten", entry.modelId)
        assertEquals(listOf("de-DE"), entry.languages)
        assertEquals(listOf("thorsten"), entry.voices)
        assertEquals(2, entry.files.size)
        assertEquals(1200L, entry.totalSizeBytes)
        assertEquals("0.5.0", entry.minimumCompatibleVersion)
    }

    @Test
    fun entryWithoutAnyRecognizedLanguageIsDropped() {
        val json = """
            {
              "models": [
                {
                  "providerId": "sherpa-onnx",
                  "modelId": "unknown-language",
                  "version": "1.0.0",
                  "languages": ["!!!"],
                  "files": [
                    { "name": "model.onnx", "url": "https://example.invalid/model.onnx", "sizeBytes": 1000, "sha256": "$validSha256" }
                  ]
                }
              ]
            }
        """.trimIndent()

        assertEquals(0, SpeechModelManifestJson.parse(json).entries.size)
    }

    @Test
    fun entryWithAnUnverifiableFileIsDroppedEntirely() {
        val json = """
            {
              "models": [
                {
                  "providerId": "sherpa-onnx",
                  "modelId": "bad-checksum",
                  "version": "1.0.0",
                  "languages": ["ja-JP"],
                  "files": [
                    { "name": "model.onnx", "url": "https://example.invalid/model.onnx", "sizeBytes": 1000, "sha256": "not-hex" }
                  ]
                }
              ]
            }
        """.trimIndent()

        assertTrue(SpeechModelManifestJson.parse(json).entries.isEmpty())
    }
}
