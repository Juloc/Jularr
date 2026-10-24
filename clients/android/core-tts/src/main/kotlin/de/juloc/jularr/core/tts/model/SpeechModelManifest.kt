package de.juloc.jularr.core.tts.model

import de.juloc.jularr.core.tts.SpeechPreferenceResolver
import org.json.JSONArray
import org.json.JSONException
import org.json.JSONObject

/** One downloadable file belonging to a model pack; see docs/TTS.md (Phase 3). */
data class SpeechModelFile(
    val name: String,
    val url: String,
    val sizeBytes: Long,
    val sha256: String,
)

/**
 * One explicit, versioned, downloadable offline-neural voice pack. Mirrors the server's
 * `SpeechModelManifestEntry` (`Jularr.Web.Features.Speech.SpeechModelManifest`) field
 * for field. A manifest entry never implies support for a language or voice it does not
 * list.
 */
data class SpeechModelManifestEntry(
    val providerId: String,
    val modelId: String,
    val version: String,
    val languages: List<String>,
    val voices: List<String>,
    val files: List<SpeechModelFile>,
    val totalSizeBytes: Long,
    val minimumCompatibleVersion: String,
)

data class SpeechModelManifest(val entries: List<SpeechModelManifestEntry>) {
    companion object {
        val Empty = SpeechModelManifest(emptyList())
    }
}

/**
 * Parses the manifest served by `GET /api/client/v1/speech/models`. Invalid or
 * unverifiable entries are dropped rather than surfaced, same rule as the server parser.
 */
object SpeechModelManifestJson {
    fun parse(json: String?): SpeechModelManifest {
        if (json.isNullOrBlank()) {
            return SpeechModelManifest.Empty
        }

        val root = try {
            JSONObject(json)
        } catch (_: JSONException) {
            return SpeechModelManifest.Empty
        }

        val models = root.optJSONArray("models") ?: return SpeechModelManifest.Empty
        val entries = (0 until models.length())
            .mapNotNull { index -> models.optJSONObject(index) }
            .mapNotNull(::tryBuildEntry)

        return SpeechModelManifest(entries)
    }

    private fun tryBuildEntry(model: JSONObject): SpeechModelManifestEntry? {
        val providerId = model.optString("providerId").trim()
        val modelId = model.optString("modelId").trim()
        val version = model.optString("version").trim()
        if (providerId.isEmpty() || modelId.isEmpty() || version.isEmpty()) {
            return null
        }

        val languages = model.optJSONArray("languages")
            .toStringList()
            .map { SpeechPreferenceResolver.normalizeLanguageTag(it) }
            .filter { it != "und" }
            .distinct()
        if (languages.isEmpty()) {
            return null
        }

        val voices = model.optJSONArray("voices")
            .toStringList()
            .map { it.trim() }
            .filter { it.isNotEmpty() }
            .distinct()

        val filesJson = model.optJSONArray("files")
        val fileCount = filesJson?.length() ?: 0
        val files = (0 until fileCount)
            .mapNotNull { index -> filesJson?.optJSONObject(index) }
            .mapNotNull(::tryBuildFile)

        // Any unverifiable file drops the whole model: a client must never be offered a
        // pack it cannot fully verify.
        if (files.isEmpty() || files.size != fileCount) {
            return null
        }

        val minimumCompatibleVersion = model.optString("minimumCompatibleVersion").trim()

        return SpeechModelManifestEntry(
            providerId = providerId,
            modelId = modelId,
            version = version,
            languages = languages,
            voices = voices,
            files = files,
            totalSizeBytes = files.sumOf { it.sizeBytes },
            minimumCompatibleVersion = minimumCompatibleVersion.ifEmpty { "0.0.0" },
        )
    }

    private fun tryBuildFile(file: JSONObject): SpeechModelFile? {
        val name = file.optString("name").trim()
        val url = file.optString("url").trim()
        val sizeBytes = file.optLong("sizeBytes", -1)
        val sha256 = file.optString("sha256").trim().lowercase()

        if (name.isEmpty() || url.isEmpty() || sizeBytes <= 0) {
            return null
        }

        if (sha256.length != 64 || sha256.any { it !in HexDigits }) {
            return null
        }

        return SpeechModelFile(name, url, sizeBytes, sha256)
    }

    private fun JSONArray?.toStringList(): List<String> {
        if (this == null) {
            return emptyList()
        }

        return (0 until length()).mapNotNull { index -> optString(index, null) }
    }

    private const val HexDigits = "0123456789abcdef"
}
