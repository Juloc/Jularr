package de.juloc.jularr.mobile.offline

import android.content.Context
import android.util.AtomicFile
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import org.json.JSONArray
import org.json.JSONObject
import java.io.File

/**
 * App-private persistence of managed downloads, the accessible account,
 * device download settings and the offline progress sync queue. Everything
 * lives below `noBackupFilesDir`, so it is never part of a device backup.
 */
class OfflineStore private constructor(val root: File) {
    private val lock = Any()
    private val stateFile = AtomicFile(File(root, "state.json"))
    private val mutableState = MutableStateFlow(load())

    val state: StateFlow<OfflineSnapshot> = mutableState.asStateFlow()

    val snapshot: OfflineSnapshot
        get() = mutableState.value

    fun update(transform: (OfflineSnapshot) -> OfflineSnapshot): OfflineSnapshot =
        synchronized(lock) {
            val current = mutableState.value
            val next = transform(current)
            if (next != current) {
                write(next)
                mutableState.value = next
            }
            next
        }

    fun ownerDirectory(ownerKey: String): File =
        File(root, ownerKey)

    fun episodeDirectory(ownerKey: String, episodeId: String): File =
        File(ownerDirectory(ownerKey), episodeId)

    fun partialFile(ownerKey: String, episodeId: String): File =
        File(episodeDirectory(ownerKey, episodeId), "media.part")

    fun mediaFile(ownerKey: String, episodeId: String): File =
        File(episodeDirectory(ownerKey, episodeId), "media")

    fun descriptorFile(ownerKey: String, episodeId: String): File =
        File(episodeDirectory(ownerKey, episodeId), "descriptor.json")

    private fun load(): OfflineSnapshot {
        root.mkdirs()
        return runCatching {
            OfflineStateCodec.decode(String(stateFile.readFully(), Charsets.UTF_8))
        }.getOrElse { OfflineSnapshot() }
    }

    private fun write(snapshot: OfflineSnapshot) {
        root.mkdirs()
        val stream = stateFile.startWrite()
        try {
            stream.write(OfflineStateCodec.encode(snapshot).toByteArray(Charsets.UTF_8))
            stateFile.finishWrite(stream)
        } catch (exception: Exception) {
            stateFile.failWrite(stream)
            throw exception
        }
    }

    companion object {
        @Volatile
        private var instance: OfflineStore? = null

        fun get(context: Context): OfflineStore =
            instance ?: synchronized(this) {
                instance ?: OfflineStore(
                    File(context.applicationContext.noBackupFilesDir, "offline"),
                ).also { instance = it }
            }
    }
}

object OfflineStateCodec {
    private const val Version = 1

    fun encode(snapshot: OfflineSnapshot): String =
        JSONObject()
            .put("version", Version)
            .put(
                "account",
                snapshot.account?.let { account ->
                    JSONObject()
                        .put("origin", account.origin)
                        .put("profileId", account.profileId)
                        .put("signedIn", account.signedIn)
                } ?: JSONObject.NULL,
            )
            .put(
                "settings",
                JSONObject()
                    .put("limitBytes", snapshot.settings.limitBytes)
                    .put("wifiOnly", snapshot.settings.wifiOnly),
            )
            .put(
                "downloads",
                JSONArray().apply {
                    snapshot.downloads.forEach { put(encodeDownload(it)) }
                },
            )
            .put(
                "pending",
                JSONObject().apply {
                    snapshot.pending.forEach { (ownerKey, entries) ->
                        put(
                            ownerKey,
                            JSONArray().apply {
                                entries.forEach { entry ->
                                    put(
                                        JSONObject()
                                            .put("episodeId", entry.episodeId)
                                            .put("positionMs", entry.positionMs)
                                            .put("durationMs", entry.durationMs ?: JSONObject.NULL)
                                            .put("completed", entry.completed),
                                    )
                                }
                            },
                        )
                    }
                },
            )
            .toString()

    fun decode(json: String): OfflineSnapshot {
        val root = JSONObject(json)
        if (root.optInt("version", 0) != Version) {
            return OfflineSnapshot()
        }

        val account = root.optJSONObject("account")?.let { account ->
            OfflineAccount(
                origin = account.getString("origin"),
                profileId = account.getString("profileId"),
                signedIn = account.getBoolean("signedIn"),
            )
        }
        val settings = root.optJSONObject("settings")?.let { settings ->
            OfflineSettings(
                limitBytes = settings.optLong("limitBytes", OfflineStoragePolicy.DefaultLimitBytes),
                wifiOnly = settings.optBoolean("wifiOnly", true),
            )
        } ?: OfflineSettings()

        val downloads = root.optJSONArray("downloads")?.let { array ->
            List(array.length()) { index -> decodeDownload(array.getJSONObject(index)) }
        }.orEmpty()

        val pending = buildMap {
            val pendingJson = root.optJSONObject("pending") ?: JSONObject()
            pendingJson.keys().forEach { ownerKey ->
                val entries = pendingJson.getJSONArray(ownerKey)
                put(
                    ownerKey,
                    List(entries.length()) { index ->
                        val entry = entries.getJSONObject(index)
                        PendingProgress(
                            episodeId = entry.getString("episodeId"),
                            positionMs = entry.getLong("positionMs"),
                            durationMs = if (entry.isNull("durationMs")) null else entry.getLong("durationMs"),
                            completed = entry.getBoolean("completed"),
                        )
                    },
                )
            }
        }

        return OfflineSnapshot(account, settings, downloads, pending)
    }

    private fun encodeDownload(download: OfflineDownload): JSONObject =
        JSONObject()
            .put("ownerKey", download.ownerKey)
            .put("episodeId", download.episodeId)
            .put("animeTitle", download.animeTitle)
            .put("episodeTitle", download.episodeTitle)
            .put("seasonNumber", download.seasonNumber)
            .put("episodeNumber", download.episodeNumber)
            .put("contentUrl", download.contentUrl)
            .put("sizeBytes", download.sizeBytes)
            .put("eTag", download.eTag)
            .put("fingerprint", download.fingerprint)
            .put("fingerprintAlgorithm", download.fingerprintAlgorithm)
            .put("state", download.state.name)
            .put("downloadedBytes", download.downloadedBytes)
            .put("failure", download.failure ?: JSONObject.NULL)
            .put("createdAtMs", download.createdAtMs)
            .put("resumePositionMs", download.resumePositionMs)
            .put("watched", download.watched)

    private fun decodeDownload(json: JSONObject): OfflineDownload =
        OfflineDownload(
            ownerKey = json.getString("ownerKey"),
            episodeId = json.getString("episodeId"),
            animeTitle = json.getString("animeTitle"),
            episodeTitle = json.getString("episodeTitle"),
            seasonNumber = json.getInt("seasonNumber"),
            episodeNumber = json.getInt("episodeNumber"),
            contentUrl = json.getString("contentUrl"),
            sizeBytes = json.getLong("sizeBytes"),
            eTag = json.getString("eTag"),
            fingerprint = json.getString("fingerprint"),
            fingerprintAlgorithm = json.getString("fingerprintAlgorithm"),
            state = DownloadState.valueOf(json.getString("state")),
            downloadedBytes = json.optLong("downloadedBytes", 0),
            failure = if (json.isNull("failure")) null else json.optString("failure"),
            createdAtMs = json.optLong("createdAtMs", 0),
            resumePositionMs = json.optLong("resumePositionMs", 0),
            watched = json.optBoolean("watched", false),
        )
}
