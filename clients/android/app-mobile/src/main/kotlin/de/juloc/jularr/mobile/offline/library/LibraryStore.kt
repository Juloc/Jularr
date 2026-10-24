package de.juloc.jularr.mobile.offline.library

import android.content.Context
import android.util.AtomicFile
import de.juloc.jularr.mobile.offline.DownloadState
import de.juloc.jularr.mobile.offline.OfflineAccount
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import org.json.JSONArray
import org.json.JSONObject
import java.io.File

/**
 * App-private persistence of the offline library: managed books/chapters, the
 * accessible account, device settings and the reading-state sync queues.
 * Sibling of `OfflineStore` (bounded offline playback, #341) under its own
 * `library/` root, same `AtomicFile` + hand-written JSON codec approach (no
 * Room/reflection library exists anywhere in this app; see
 * docs/ANDROID_CLIENTS.md). Everything lives below `noBackupFilesDir`, so it
 * is never part of a device backup.
 */
class LibraryStore private constructor(val root: File) {
    private val lock = Any()
    private val stateFile = AtomicFile(File(root, "state.json"))
    private val mutableState = MutableStateFlow(load())

    val state: StateFlow<LibrarySnapshot> = mutableState.asStateFlow()

    val snapshot: LibrarySnapshot
        get() = mutableState.value

    fun update(transform: (LibrarySnapshot) -> LibrarySnapshot): LibrarySnapshot =
        synchronized(lock) {
            val current = mutableState.value
            val next = transform(current)
            if (next != current) {
                write(next)
                mutableState.value = next
            }
            next
        }

    fun ownerDirectory(ownerKey: String): File = File(root, ownerKey)

    /**
     * Chapters and assets are addressed by their own globally-unique id/name
     * (matching the request shape `/chapters/{chapterId}` and
     * `/assets/{volumeId}/{asset}` exactly), not nested under a book
     * directory, so the WebView interception (`LibraryRequestInterception`)
     * can resolve a file directly from the request path alone.
     */
    fun manifestFile(ownerKey: String, workId: String): File =
        File(File(ownerDirectory(ownerKey), "books"), "$workId.json")

    fun chapterFile(ownerKey: String, chapterId: String): File =
        File(File(ownerDirectory(ownerKey), "chapters"), "$chapterId.json")

    fun assetFile(ownerKey: String, asset: String): File =
        File(File(ownerDirectory(ownerKey), "assets"), asset)

    private fun load(): LibrarySnapshot {
        root.mkdirs()
        return runCatching {
            LibraryStateCodec.decode(String(stateFile.readFully(), Charsets.UTF_8))
        }.getOrElse { LibrarySnapshot() }
    }

    private fun write(snapshot: LibrarySnapshot) {
        root.mkdirs()
        val stream = stateFile.startWrite()
        try {
            stream.write(LibraryStateCodec.encode(snapshot).toByteArray(Charsets.UTF_8))
            stateFile.finishWrite(stream)
        } catch (exception: Exception) {
            stateFile.failWrite(stream)
            throw exception
        }
    }

    companion object {
        @Volatile
        private var instance: LibraryStore? = null

        fun get(context: Context): LibraryStore =
            instance ?: synchronized(this) {
                instance ?: LibraryStore(
                    File(context.applicationContext.noBackupFilesDir, "library"),
                ).also { instance = it }
            }
    }
}

/**
 * Writes [bytes] to `<file>.part`, reads them back and compares before an
 * atomic rename into [file] — the same "write, then read back and compare"
 * finalization rule the PWA engine uses (docs/OFFLINE_LIBRARY.md#atomic-finalization),
 * catching a truncated/corrupted write immediately instead of surfacing a
 * broken chapter later. Returns `false` (and leaves [file] untouched) if the
 * write could not be verified.
 */
object LibraryContentIo {
    fun writeVerified(file: File, bytes: ByteArray): Boolean {
        val partial = File(file.parentFile, "${file.name}.part")
        partial.parentFile?.mkdirs()
        return try {
            partial.writeBytes(bytes)
            val readBack = partial.readBytes()
            if (!readBack.contentEquals(bytes)) {
                partial.delete()
                return false
            }
            partial.renameTo(file)
        } catch (_: java.io.IOException) {
            partial.delete()
            false
        }
    }
}

object LibraryStateCodec {
    private const val Version = 1

    fun encode(snapshot: LibrarySnapshot): String =
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
            .put("settings", JSONObject().put("wifiOnly", snapshot.settings.wifiOnly))
            .put("books", JSONArray().apply { snapshot.books.forEach { put(encodeBook(it)) } })
            .put("chapters", JSONArray().apply { snapshot.chapters.forEach { put(encodeChapter(it)) } })
            .put(
                "pendingProgress",
                JSONObject().apply {
                    snapshot.pendingProgress.forEach { (ownerKey, entries) ->
                        put(ownerKey, JSONArray().apply { entries.forEach { put(encodeProgress(it)) } })
                    }
                },
            )
            .put(
                "pendingBookmarks",
                JSONObject().apply {
                    snapshot.pendingBookmarks.forEach { (ownerKey, entries) ->
                        put(ownerKey, JSONArray().apply { entries.forEach { put(encodeBookmark(it)) } })
                    }
                },
            )
            .toString()

    fun decode(json: String): LibrarySnapshot {
        val root = JSONObject(json)
        if (root.optInt("version", 0) != Version) {
            return LibrarySnapshot()
        }

        val account = root.optJSONObject("account")?.let { account ->
            OfflineAccount(
                origin = account.getString("origin"),
                profileId = account.getString("profileId"),
                signedIn = account.getBoolean("signedIn"),
            )
        }
        val settings = root.optJSONObject("settings")?.let { settings ->
            LibrarySettings(wifiOnly = settings.optBoolean("wifiOnly", true))
        } ?: LibrarySettings()

        val books = root.optJSONArray("books")?.let { array ->
            List(array.length()) { index -> decodeBook(array.getJSONObject(index)) }
        }.orEmpty()

        val chapters = root.optJSONArray("chapters")?.let { array ->
            List(array.length()) { index -> decodeChapter(array.getJSONObject(index)) }
        }.orEmpty()

        val pendingProgress = buildMap {
            val pendingJson = root.optJSONObject("pendingProgress") ?: JSONObject()
            pendingJson.keys().forEach { ownerKey ->
                val entries = pendingJson.getJSONArray(ownerKey)
                put(ownerKey, List(entries.length()) { index -> decodeProgress(entries.getJSONObject(index)) })
            }
        }

        val pendingBookmarks = buildMap {
            val pendingJson = root.optJSONObject("pendingBookmarks") ?: JSONObject()
            pendingJson.keys().forEach { ownerKey ->
                val entries = pendingJson.getJSONArray(ownerKey)
                put(ownerKey, List(entries.length()) { index -> decodeBookmark(entries.getJSONObject(index)) })
            }
        }

        return LibrarySnapshot(account, settings, books, chapters, pendingProgress, pendingBookmarks)
    }

    private fun encodeBook(book: LibraryBookRecord): JSONObject =
        JSONObject()
            .put("ownerKey", book.ownerKey)
            .put("workId", book.workId)
            .put("title", book.title)
            .put("author", book.author ?: JSONObject.NULL)
            .put("coverAssetUrl", book.coverAssetUrl ?: JSONObject.NULL)
            .put("contentVersion", book.contentVersion)
            .put(
                "selectedChapterIds",
                book.selectedChapterIds?.let { ids -> JSONArray().apply { ids.forEach { put(it) } } }
                    ?: JSONObject.NULL,
            )
            .put("wholeBook", book.wholeBook)
            .put("assetFileNames", JSONArray().apply { book.assetFileNames.forEach { put(it) } })
            .put("createdAtMs", book.createdAtMs)

    private fun decodeBook(json: JSONObject): LibraryBookRecord =
        LibraryBookRecord(
            ownerKey = json.getString("ownerKey"),
            workId = json.getString("workId"),
            title = json.getString("title"),
            author = json.stringOrNull("author"),
            coverAssetUrl = json.stringOrNull("coverAssetUrl"),
            contentVersion = json.getString("contentVersion"),
            selectedChapterIds = json.optJSONArray("selectedChapterIds")?.let { array ->
                List(array.length()) { index -> array.getString(index) }.toSet()
            },
            wholeBook = json.optBoolean("wholeBook", true),
            assetFileNames = json.optJSONArray("assetFileNames")?.let { array ->
                List(array.length()) { index -> array.getString(index) }.toSet()
            } ?: emptySet(),
            createdAtMs = json.optLong("createdAtMs", 0),
        )

    private fun encodeChapter(chapter: LibraryChapterRecord): JSONObject =
        JSONObject()
            .put("ownerKey", chapter.ownerKey)
            .put("workId", chapter.workId)
            .put("chapterId", chapter.chapterId)
            .put("volumeId", chapter.volumeId)
            .put("number", chapter.number)
            .put("title", chapter.title)
            .put("hash", chapter.hash)
            .put("verifiedHash", chapter.verifiedHash ?: JSONObject.NULL)
            .put("state", chapter.state.name)
            .put("failure", chapter.failure ?: JSONObject.NULL)
            .put("createdAtMs", chapter.createdAtMs)

    private fun decodeChapter(json: JSONObject): LibraryChapterRecord =
        LibraryChapterRecord(
            ownerKey = json.getString("ownerKey"),
            workId = json.getString("workId"),
            chapterId = json.getString("chapterId"),
            volumeId = json.getString("volumeId"),
            number = json.getInt("number"),
            title = json.getString("title"),
            hash = json.getString("hash"),
            verifiedHash = json.stringOrNull("verifiedHash"),
            state = DownloadState.valueOf(json.getString("state")),
            failure = json.stringOrNull("failure"),
            createdAtMs = json.optLong("createdAtMs", 0),
        )

    private fun encodeProgress(entry: LibraryPendingProgress): JSONObject =
        JSONObject()
            .put("workId", entry.workId)
            .put("chapterId", entry.chapterId)
            .put("positionPermille", entry.positionPermille)
            .put("anchorLanguage", entry.anchorLanguage ?: JSONObject.NULL)
            .put("anchorParagraphIndex", entry.anchorParagraphIndex ?: JSONObject.NULL)
            .put("anchorOffset", entry.anchorOffset)
            .put("clientEventId", entry.clientEventId)
            .put("clientTimestampUtc", entry.clientTimestampUtc)

    private fun decodeProgress(json: JSONObject): LibraryPendingProgress =
        LibraryPendingProgress(
            workId = json.getString("workId"),
            chapterId = json.getString("chapterId"),
            positionPermille = json.getInt("positionPermille"),
            anchorLanguage = json.stringOrNull("anchorLanguage"),
            anchorParagraphIndex = json.intOrNull("anchorParagraphIndex"),
            anchorOffset = json.optInt("anchorOffset", 0),
            clientEventId = json.getString("clientEventId"),
            clientTimestampUtc = json.getString("clientTimestampUtc"),
        )

    private fun encodeBookmark(entry: LibraryPendingBookmark): JSONObject =
        JSONObject()
            .put("bookmarkId", entry.bookmarkId)
            .put("type", entry.type)
            .put("workId", entry.workId)
            .put("chapterId", entry.chapterId)
            .put("language", entry.language ?: JSONObject.NULL)
            .put("positionPermille", entry.positionPermille)
            .put("paragraphIndex", entry.paragraphIndex ?: JSONObject.NULL)
            .put("characterOffset", entry.characterOffset)
            .put("anchorText", entry.anchorText ?: JSONObject.NULL)
            .put("label", entry.label ?: JSONObject.NULL)
            .put("style", entry.style ?: JSONObject.NULL)
            .put("color", entry.color ?: JSONObject.NULL)
            .put("clientEventId", entry.clientEventId)
            .put("clientTimestampUtc", entry.clientTimestampUtc)

    private fun decodeBookmark(json: JSONObject): LibraryPendingBookmark =
        LibraryPendingBookmark(
            bookmarkId = json.getString("bookmarkId"),
            type = json.getString("type"),
            workId = json.getString("workId"),
            chapterId = json.getString("chapterId"),
            language = json.stringOrNull("language"),
            positionPermille = json.getInt("positionPermille"),
            paragraphIndex = json.intOrNull("paragraphIndex"),
            characterOffset = json.optInt("characterOffset", 0),
            anchorText = json.stringOrNull("anchorText"),
            label = json.stringOrNull("label"),
            style = json.stringOrNull("style"),
            color = json.stringOrNull("color"),
            clientEventId = json.getString("clientEventId"),
            clientTimestampUtc = json.getString("clientTimestampUtc"),
        )

    private fun JSONObject.stringOrNull(name: String): String? =
        if (!has(name) || isNull(name)) null else getString(name)

    private fun JSONObject.intOrNull(name: String): Int? =
        if (!has(name) || isNull(name)) null else getInt(name)
}
