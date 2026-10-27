package de.juloc.jularr.core.api

import de.juloc.jularr.core.model.OfflineProgressItem
import org.json.JSONObject
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class OfflineDownloadJsonTest {
    @Test
    fun parsesTheServerDescriptor() {
        val descriptor = OfflineDownloadJson.parseDescriptor(
            """
            {
              "apiVersion": 1,
              "issuedAtUtc": "2026-09-26T10:00:00Z",
              "episode": {
                "id": "e1", "animeId": "a1", "animeTitle": "Anime", "title": "Episode 1",
                "seasonNumber": 1, "number": 1
              },
              "media": {
                "mediaFileId": "m1", "fileName": "episode.mkv", "contentType": "video/x-matroska",
                "sizeBytes": 1400000000, "durationMs": 1420500, "videoCodec": "hevc",
                "pixelFormat": null, "audioCodec": "aac",
                "contentUrl": "/api/client/v1/offline/media/m1/content",
                "eTag": "\"5372a400-1\"", "fingerprint": "abc",
                "fingerprintAlgorithm": "sha256-length-head-tail-64k"
              },
              "audioTracks": [
                { "id": "stream:1", "streamIndex": 1, "kind": "audio", "codec": "aac", "language": "jpn",
                  "title": null, "isDefault": true, "isForced": false, "isText": false }
              ],
              "subtitleTracks": [],
              "defaultAudioTrackId": "stream:1",
              "defaultSubtitleTrackId": null,
              "learningCues": {
                "trackId": "t1", "fromMs": null, "toMs": null,
                "cues": [
                  { "id": 7, "startMs": 1000, "endMs": 2500, "text": "猫です",
                    "tokens": [
                      { "surface": "猫", "termId": "term1", "canonical": "猫", "reading": "ねこ", "meaning": "cat", "state": "learning" },
                      { "surface": "です", "termId": null, "canonical": null, "reading": null, "meaning": null, "state": "new" }
                    ] }
                ]
              },
              "progress": {
                "positionMs": 600000, "durationMs": 1420500, "percent": 42, "isCompleted": false,
                "updatedAtUtc": "2026-09-25T10:00:00Z", "resumePositionMs": 600000
              }
            }
            """.trimIndent(),
        )

        assertEquals(1_400_000_000L, descriptor.media.sizeBytes)
        assertEquals("\"5372a400-1\"", descriptor.media.eTag)
        assertNull(descriptor.media.pixelFormat)
        assertEquals("stream:1", descriptor.defaultAudioTrackId)
        assertEquals("ねこ", descriptor.learningCues.cues.single().tokens.first().reading)
        assertEquals(600_000L, descriptor.progress.positionMs)
        assertFalse(descriptor.progress.isCompleted)
    }

    @Test
    fun serializesProgressItemsAndParsesOutcomes() {
        val body = OfflineDownloadJson.progressItemsBody(
            listOf(OfflineProgressItem("e1", 60_000, null, completed = false)),
        )
        val item = body.getJSONArray("items").getJSONObject(0)
        assertEquals("e1", item.getString("episodeId"))
        assertTrue(item.isNull("durationMs"))

        val results = OfflineDownloadJson.parseProgressResults(
            JSONObject(
                """
                { "results": [
                  { "episodeId": "e1", "outcome": "ignored_behind",
                    "progress": { "positionMs": 90000, "durationMs": null, "percent": 0, "isCompleted": false, "updatedAtUtc": null } },
                  { "episodeId": "e2", "outcome": "episode_not_found", "progress": null }
                ] }
                """.trimIndent(),
            ),
        )

        assertEquals("ignored_behind", results[0].outcome)
        assertEquals(90_000L, results[0].progress?.positionMs)
        assertNull(results[1].progress)
    }

    @Test
    fun offlineDownloadsFlagIsParsed() {
        val parsed = ClientFeatureFlagParser.parse { name -> name == "offlineDownloads" }

        assertTrue(parsed.offlineDownloads)
        assertFalse(parsed.library)
    }
}
