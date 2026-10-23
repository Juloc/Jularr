package de.juloc.jularr.core.tts.model

import kotlinx.coroutines.runBlocking
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test
import java.io.File
import java.security.MessageDigest

class TtsModelManagerTest {
    private lateinit var root: File

    @Before
    fun setUp() {
        root = createTempDir(prefix = "jularr-tts-models-")
    }

    @After
    fun tearDown() {
        root.deleteRecursively()
    }

    private fun sha256(bytes: ByteArray): String =
        MessageDigest.getInstance("SHA-256").digest(bytes).joinToString("") { "%02x".format(it) }

    private fun entryFor(bytes: ByteArray, modelId: String = "model-1") = SpeechModelManifestEntry(
        providerId = "sherpa-onnx",
        modelId = modelId,
        version = "1.0.0",
        languages = listOf("ja-JP"),
        voices = listOf("v1"),
        files = listOf(SpeechModelFile("model.bin", "https://example.invalid/model.bin", bytes.size.toLong(), sha256(bytes))),
        totalSizeBytes = bytes.size.toLong(),
        minimumCompatibleVersion = "0.0.0",
    )

    @Test
    fun downloadVerifiesChecksumAndActivatesAtomically() = runBlocking {
        val bytes = "model-bytes".toByteArray()
        val manager = TtsModelManager(root) { file, destination -> destination.writeBytes(bytes) }

        val result = manager.download(entryFor(bytes))

        assertTrue(result is TtsModelDownloadResult.Success)
        assertTrue(manager.isInstalled("model-1"))
        assertEquals(listOf("model-1"), manager.installedModels().map { it.modelId })
        assertEquals(bytes.size.toLong(), manager.storageUsageBytes("model-1"))

        // No leftover staging directory should remain.
        assertTrue(root.listFiles()!!.none { it.name.startsWith(".staging-") })
    }

    @Test
    fun checksumMismatchNeverActivatesAndLeavesNoStagingDirectory() = runBlocking {
        val bytes = "model-bytes".toByteArray()
        val corrupted = "corrupted!!".toByteArray()
        val manager = TtsModelManager(root) { file, destination -> destination.writeBytes(corrupted) }

        val result = manager.download(entryFor(bytes))

        assertTrue(result is TtsModelDownloadResult.ChecksumMismatch)
        assertFalse(manager.isInstalled("model-1"))
        assertTrue(manager.installedModels().isEmpty())
        assertTrue(
            "A failed download must never leave a partial model directory behind.",
            root.listFiles().orEmpty().isEmpty(),
        )
    }

    @Test
    fun downloadFailureIsReportedAndLeavesNoTrace() = runBlocking {
        val bytes = "model-bytes".toByteArray()
        val manager = TtsModelManager(root) { _, _ -> throw java.io.IOException("network down") }

        val result = manager.download(entryFor(bytes))

        assertTrue(result is TtsModelDownloadResult.DownloadFailed)
        assertTrue(manager.installedModels().isEmpty())
    }

    @Test
    fun redownloadingReplacesThePreviousVersionAtomically() = runBlocking {
        val v1 = "version-one".toByteArray()
        val v2 = "version-two-longer".toByteArray()
        var payload = v1
        val manager = TtsModelManager(root) { _, destination -> destination.writeBytes(payload) }

        manager.download(entryFor(v1))
        assertEquals(v1.size.toLong(), manager.storageUsageBytes("model-1"))

        payload = v2
        manager.download(entryFor(v2))

        assertEquals(1, manager.installedModels().size)
        assertEquals(v2.size.toLong(), manager.storageUsageBytes("model-1"))
    }

    @Test
    fun deleteRemovesAnInstalledModel() = runBlocking {
        val bytes = "model-bytes".toByteArray()
        val manager = TtsModelManager(root) { _, destination -> destination.writeBytes(bytes) }
        manager.download(entryFor(bytes))

        assertTrue(manager.delete("model-1"))
        assertFalse(manager.isInstalled("model-1"))
    }

    private fun createTempDir(prefix: String): File =
        File.createTempFile(prefix, "").let {
            it.delete()
            it.mkdirs()
            it
        }
}
