package de.juloc.jularr.core.api

import de.juloc.jularr.core.model.ClientAccount
import de.juloc.jularr.core.model.ClientOfflineLibraryChapterPayload
import de.juloc.jularr.core.model.ClientOfflineLibraryChapterRef
import de.juloc.jularr.core.model.ClientOfflineLibraryContentBlock
import de.juloc.jularr.core.model.ClientOfflineLibraryInlineRun
import de.juloc.jularr.core.model.ClientOfflineLibraryManifest
import de.juloc.jularr.core.model.ClientOfflineLibraryTranslation
import de.juloc.jularr.core.model.ClientOfflineLibraryVolume
import de.juloc.jularr.core.model.OfflineLibraryBookmarkEvent
import de.juloc.jularr.core.model.OfflineLibraryBookmarkResult
import de.juloc.jularr.core.model.OfflineLibraryChapterPackage
import de.juloc.jularr.core.model.OfflineLibraryManifestPackage
import de.juloc.jularr.core.model.OfflineLibraryProgressEvent
import de.juloc.jularr.core.model.OfflineLibraryProgressResult
import de.juloc.jularr.core.model.OfflineLibrarySyncResult
import org.json.JSONArray
import org.json.JSONObject

/**
 * Additive v1 offline Book/Novel library endpoints, advertised by the
 * `offlineLibrary` capability (`docs/OFFLINE_LIBRARY.md`). Shares the same
 * server contract as the PWA download manager (part 1, PR #359); Android only
 * replays the same event shapes, it does not reimplement the conflict rules
 * (forward-only progress, last-writer-wins bookmarks with tombstones).
 */
interface JularrLibraryApi {
    suspend fun getMe(): ClientAccount
    suspend fun getLibraryManifest(workId: String): OfflineLibraryManifestPackage
    suspend fun getLibraryChapter(chapterId: String): OfflineLibraryChapterPackage
    suspend fun syncLibrary(
        progress: List<OfflineLibraryProgressEvent>,
        bookmarks: List<OfflineLibraryBookmarkEvent>,
    ): OfflineLibrarySyncResult

    companion object {
        /** Server-side bound of one reconciliation request, per list. */
        const val MaxSyncBatchItems = 200
    }
}

/**
 * Parser for the manifest/chapter/sync wire shapes. Shared by the HTTP client
 * and by offline reads, which re-parse the stored JSON without network access.
 */
object OfflineLibraryJson {
    fun parseManifest(json: String): ClientOfflineLibraryManifest =
        JSONObject(json).toManifest()

    fun parseChapter(json: String): ClientOfflineLibraryChapterPayload =
        JSONObject(json).toChapterPayload()

    internal fun syncBody(
        progress: List<OfflineLibraryProgressEvent>,
        bookmarks: List<OfflineLibraryBookmarkEvent>,
    ): JSONObject =
        JSONObject()
            .put(
                "progress",
                JSONArray().apply {
                    progress.forEach { event ->
                        put(
                            JSONObject()
                                .put("clientEventId", event.clientEventId)
                                .put("workId", event.workId)
                                .put("chapterId", event.chapterId)
                                .put("positionPermille", event.positionPermille)
                                .put("anchorLanguage", event.anchorLanguage ?: JSONObject.NULL)
                                .put("anchorParagraphIndex", event.anchorParagraphIndex ?: JSONObject.NULL)
                                .put("anchorOffset", event.anchorOffset)
                                .put("clientTimestampUtc", event.clientTimestampUtc),
                        )
                    }
                },
            )
            .put(
                "bookmarks",
                JSONArray().apply {
                    bookmarks.forEach { event ->
                        put(
                            JSONObject()
                                .put("clientEventId", event.clientEventId)
                                .put("bookmarkId", event.bookmarkId)
                                .put("type", event.type)
                                .put("workId", event.workId)
                                .put("chapterId", event.chapterId)
                                .put("language", event.language ?: JSONObject.NULL)
                                .put("positionPermille", event.positionPermille)
                                .put("paragraphIndex", event.paragraphIndex ?: JSONObject.NULL)
                                .put("characterOffset", event.characterOffset)
                                .put("anchorText", event.anchorText ?: JSONObject.NULL)
                                .put("label", event.label ?: JSONObject.NULL)
                                .put("style", event.style ?: JSONObject.NULL)
                                .put("color", event.color ?: JSONObject.NULL)
                                .put("clientTimestampUtc", event.clientTimestampUtc),
                        )
                    }
                },
            )

    internal fun parseSyncResult(json: JSONObject): OfflineLibrarySyncResult =
        OfflineLibrarySyncResult(
            progress = json.getJSONArray("progress").mapObjects { result ->
                OfflineLibraryProgressResult(
                    clientEventId = result.getString("clientEventId"),
                    workId = result.getString("workId"),
                    outcome = result.getString("outcome"),
                    positionPermille = result.intOrNull("positionPermille"),
                )
            },
            bookmarks = json.getJSONArray("bookmarks").mapObjects { result ->
                OfflineLibraryBookmarkResult(
                    clientEventId = result.getString("clientEventId"),
                    bookmarkId = result.getString("bookmarkId"),
                    outcome = result.getString("outcome"),
                )
            },
        )

    private fun JSONObject.toManifest() = ClientOfflineLibraryManifest(
        workId = getString("workId"),
        schemaVersion = getInt("schemaVersion"),
        contentVersion = getString("contentVersion"),
        title = getString("title"),
        author = stringOrNull("author"),
        description = stringOrNull("description"),
        coverAssetUrl = stringOrNull("coverAssetUrl"),
        issuedAtUtc = getString("issuedAtUtc"),
        volumes = getJSONArray("volumes").mapObjects { volume ->
            ClientOfflineLibraryVolume(
                volumeId = volume.getString("volumeId"),
                number = volume.getInt("number"),
                title = volume.stringOrNull("title"),
                kind = volume.getString("kind"),
                coverAssetUrl = volume.stringOrNull("coverAssetUrl"),
            )
        },
        chapters = getJSONArray("chapters").mapObjects { chapter ->
            ClientOfflineLibraryChapterRef(
                chapterId = chapter.getString("chapterId"),
                volumeId = chapter.getString("volumeId"),
                number = chapter.getInt("number"),
                title = chapter.getString("title"),
                hash = chapter.getString("hash"),
                hasContent = chapter.getBoolean("hasContent"),
                hasTranslation = chapter.getBoolean("hasTranslation"),
            )
        },
    )

    private fun JSONObject.toChapterPayload() = ClientOfflineLibraryChapterPayload(
        chapterId = getString("chapterId"),
        workId = getString("workId"),
        volumeId = getString("volumeId"),
        number = getInt("number"),
        title = getString("title"),
        hash = getString("hash"),
        originalText = getString("originalText"),
        blocks = getJSONArray("blocks").mapObjects { block ->
            ClientOfflineLibraryContentBlock(
                kind = block.getString("kind"),
                level = block.optInt("level", 0),
                runs = block.optJSONArray("runs")?.mapObjects { run ->
                    ClientOfflineLibraryInlineRun(
                        text = run.getString("text"),
                        ruby = run.stringOrNull("ruby"),
                        emphasis = run.optBoolean("emphasis", false),
                        strong = run.optBoolean("strong", false),
                    )
                }.orEmpty(),
                imageAssetUrl = block.stringOrNull("imageAssetUrl"),
                imageAlt = block.stringOrNull("imageAlt"),
            )
        },
        translations = getJSONArray("translations").mapObjects { translation ->
            ClientOfflineLibraryTranslation(
                targetLanguage = translation.getString("targetLanguage"),
                text = translation.getString("text"),
            )
        },
    )

    private fun JSONObject.stringOrNull(name: String): String? =
        if (!has(name) || isNull(name)) null else getString(name)

    private fun JSONObject.intOrNull(name: String): Int? =
        if (!has(name) || isNull(name)) null else getInt(name)

    private inline fun <T> JSONArray.mapObjects(transform: (JSONObject) -> T): List<T> =
        buildList(length()) {
            for (index in 0 until length()) {
                add(transform(getJSONObject(index)))
            }
        }
}
