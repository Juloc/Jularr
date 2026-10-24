package de.juloc.jularr.core.api

import de.juloc.jularr.core.model.OfflineLibraryBookmarkEvent
import de.juloc.jularr.core.model.OfflineLibraryProgressEvent
import org.json.JSONObject
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class OfflineLibraryJsonTest {
    @Test
    fun parsesTheManifest() {
        val manifest = OfflineLibraryJson.parseManifest(
            """
            {
              "workId": "w1", "schemaVersion": 1, "contentVersion": "cv1",
              "title": "My Novel", "author": "Jane Doe", "description": null,
              "coverAssetUrl": "/api/client/v1/offline-library/assets/v1/abc.jpg",
              "issuedAtUtc": "2026-09-26T10:00:00Z",
              "volumes": [
                { "volumeId": "v1", "number": 1, "title": "Volume 1", "kind": "novel", "coverAssetUrl": null }
              ],
              "chapters": [
                { "chapterId": "c1", "volumeId": "v1", "number": 1, "title": "Chapter 1",
                  "hash": "h1", "hasContent": true, "hasTranslation": false }
              ]
            }
            """.trimIndent(),
        )

        assertEquals("w1", manifest.workId)
        assertEquals("cv1", manifest.contentVersion)
        assertEquals("Jane Doe", manifest.author)
        assertEquals(1, manifest.volumes.size)
        assertEquals("h1", manifest.chapters.single().hash)
        assertTrue(manifest.chapters.single().hasContent)
        assertFalse(manifest.chapters.single().hasTranslation)
    }

    @Test
    fun parsesAChapterPayloadWithBlocksAndTranslations() {
        val payload = OfflineLibraryJson.parseChapter(
            """
            {
              "chapterId": "c1", "workId": "w1", "volumeId": "v1", "number": 1,
              "title": "Chapter 1", "hash": "h1",
              "originalText": "猫です",
              "blocks": [
                { "kind": "paragraph", "level": 0,
                  "runs": [ { "text": "猫です", "ruby": "ねこです", "emphasis": false, "strong": false } ],
                  "imageAssetUrl": null, "imageAlt": null },
                { "kind": "image", "level": 0, "runs": [],
                  "imageAssetUrl": "/api/client/v1/offline-library/assets/v1/img.jpg", "imageAlt": "A cat" }
              ],
              "translations": [ { "targetLanguage": "de", "text": "Es ist eine Katze" } ]
            }
            """.trimIndent(),
        )

        assertEquals("h1", payload.hash)
        assertEquals(2, payload.blocks.size)
        assertEquals("ねこです", payload.blocks.first().runs.single().ruby)
        assertEquals("A cat", payload.blocks[1].imageAlt)
        assertEquals("Es ist eine Katze", payload.translations.single().text)
    }

    @Test
    fun serializesSyncEventsAndParsesResults() {
        val body = OfflineLibraryJson.syncBody(
            progress = listOf(
                OfflineLibraryProgressEvent(
                    clientEventId = "e1", workId = "w1", chapterId = "c1", positionPermille = 500,
                    anchorLanguage = "ja", anchorParagraphIndex = 2, anchorOffset = 10,
                    clientTimestampUtc = "2026-09-26T10:00:00Z",
                ),
            ),
            bookmarks = listOf(
                OfflineLibraryBookmarkEvent(
                    clientEventId = "e2", bookmarkId = "b1", type = "upsert", workId = "w1", chapterId = "c1",
                    language = "ja", positionPermille = 250, paragraphIndex = 1, characterOffset = 5,
                    anchorText = "text", label = null, style = null, color = null,
                    clientTimestampUtc = "2026-09-26T10:01:00Z",
                ),
            ),
        )

        val progressItem = body.getJSONArray("progress").getJSONObject(0)
        assertEquals("w1", progressItem.getString("workId"))
        assertEquals(500, progressItem.getInt("positionPermille"))

        val bookmarkItem = body.getJSONArray("bookmarks").getJSONObject(0)
        assertEquals("upsert", bookmarkItem.getString("type"))
        assertTrue(bookmarkItem.isNull("label"))

        val result = OfflineLibraryJson.parseSyncResult(
            JSONObject(
                """
                { "progress": [ { "clientEventId": "e1", "workId": "w1", "outcome": "applied", "positionPermille": 500 } ],
                  "bookmarks": [ { "clientEventId": "e2", "bookmarkId": "b1", "outcome": "unchanged" } ] }
                """.trimIndent(),
            ),
        )

        assertEquals("applied", result.progress.single().outcome)
        assertEquals(500, result.progress.single().positionPermille)
        assertEquals("unchanged", result.bookmarks.single().outcome)
    }

    @Test
    fun offlineLibraryFlagIsParsed() {
        val parsed = ClientFeatureFlagParser.parse { name -> name == "offlineLibrary" }

        assertTrue(parsed.offlineLibrary)
        assertFalse(parsed.offlineDownloads)
    }
}
