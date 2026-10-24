package de.juloc.jularr.core.tts.model

import org.json.JSONArray
import org.json.JSONException
import org.json.JSONObject
import java.io.File
import java.security.MessageDigest

data class InstalledTtsModel(
    val modelId: String,
    val providerId: String,
    val version: String,
    val languages: List<String>,
    val storageBytes: Long,
)

sealed interface TtsModelDownloadResult {
    data class Success(val model: InstalledTtsModel) : TtsModelDownloadResult
    data class ChecksumMismatch(val fileName: String) : TtsModelDownloadResult
    data class DownloadFailed(val message: String) : TtsModelDownloadResult
}

fun interface TtsModelFileDownloader {
    /** Downloads [file] fully into [destination] (overwriting it). Throws on failure. */
    suspend fun download(file: SpeechModelFile, destination: File)
}

/**
 * Explicit-download model manager for offline-neural voice packs (docs/TTS.md, Phase 3).
 * Activation is strictly download -> verify every SHA-256 -> atomically activate: a
 * partially downloaded or checksum-failed model is never selectable, because it only ever
 * exists under a staging directory name until every file is verified.
 */
class TtsModelManager(
    private val modelsRoot: File,
    private val downloader: TtsModelFileDownloader,
) {
    fun installedModels(): List<InstalledTtsModel> {
        if (!modelsRoot.isDirectory) {
            return emptyList()
        }

        return modelsRoot.listFiles { file -> file.isDirectory && !file.name.startsWith(StagingPrefix) }
            .orEmpty()
            .mapNotNull(::readMarker)
    }

    fun isInstalled(modelId: String): Boolean = readMarker(File(modelsRoot, modelId)) != null

    fun storageUsageBytes(modelId: String): Long =
        readMarker(File(modelsRoot, modelId))?.storageBytes ?: 0L

    fun delete(modelId: String): Boolean {
        val dir = File(modelsRoot, modelId)
        return dir.exists() && dir.deleteRecursively()
    }

    suspend fun download(
        entry: SpeechModelManifestEntry,
        onProgress: (downloadedBytes: Long, totalBytes: Long) -> Unit = { _, _ -> },
    ): TtsModelDownloadResult {
        val staging = File(modelsRoot, "$StagingPrefix${entry.modelId}-${System.nanoTime()}")

        return try {
            staging.deleteRecursively()
            staging.mkdirs()

            var downloaded = 0L
            for (file in entry.files) {
                val destination = File(staging, file.name)
                downloader.download(file, destination)

                val actualSha256 = sha256Of(destination)
                if (!actualSha256.equals(file.sha256, ignoreCase = true)) {
                    return TtsModelDownloadResult.ChecksumMismatch(file.name)
                }

                downloaded += file.sizeBytes
                onProgress(downloaded, entry.totalSizeBytes)
            }

            writeMarker(staging, entry)

            val finalDir = File(modelsRoot, entry.modelId)
            finalDir.deleteRecursively()
            if (!staging.renameTo(finalDir)) {
                return TtsModelDownloadResult.DownloadFailed("Could not activate the downloaded model.")
            }

            TtsModelDownloadResult.Success(
                InstalledTtsModel(
                    modelId = entry.modelId,
                    providerId = entry.providerId,
                    version = entry.version,
                    languages = entry.languages,
                    storageBytes = entry.totalSizeBytes,
                ),
            )
        } catch (exception: Exception) {
            TtsModelDownloadResult.DownloadFailed(exception.message ?: "Download failed.")
        } finally {
            // A staging directory left behind (error, crash, cancellation) must never be
            // mistaken for an installed model; installedModels() already excludes anything
            // with the staging prefix, but clean up eagerly too.
            if (staging.exists()) {
                staging.deleteRecursively()
            }
        }
    }

    private fun readMarker(dir: File): InstalledTtsModel? {
        if (!dir.isDirectory) {
            return null
        }

        val markerFile = File(dir, MarkerFileName)
        if (!markerFile.isFile) {
            // No marker means the model was never fully activated (or was deleted).
            return null
        }

        return try {
            val json = JSONObject(markerFile.readText())
            val languagesJson = json.optJSONArray("languages")
            InstalledTtsModel(
                modelId = json.getString("modelId"),
                providerId = json.getString("providerId"),
                version = json.getString("version"),
                languages = (0 until (languagesJson?.length() ?: 0))
                    .mapNotNull { index -> languagesJson?.optString(index, null) },
                // The declared total from the verified manifest, not a directory scan: a
                // directory scan would also count the marker file's own bytes.
                storageBytes = json.optLong("totalSizeBytes", 0L),
            )
        } catch (_: JSONException) {
            null
        }
    }

    private fun writeMarker(dir: File, entry: SpeechModelManifestEntry) {
        val json = JSONObject()
            .put("providerId", entry.providerId)
            .put("modelId", entry.modelId)
            .put("version", entry.version)
            .put("languages", JSONArray(entry.languages))
            .put("totalSizeBytes", entry.totalSizeBytes)
        File(dir, MarkerFileName).writeText(json.toString())
    }

    private fun sha256Of(file: File): String {
        val digest = MessageDigest.getInstance("SHA-256")
        file.inputStream().use { stream ->
            val buffer = ByteArray(8192)
            while (true) {
                val read = stream.read(buffer)
                if (read < 0) break
                digest.update(buffer, 0, read)
            }
        }

        return digest.digest().joinToString("") { "%02x".format(it) }
    }

    private companion object {
        const val StagingPrefix = ".staging-"
        const val MarkerFileName = "manifest.json"
    }
}
