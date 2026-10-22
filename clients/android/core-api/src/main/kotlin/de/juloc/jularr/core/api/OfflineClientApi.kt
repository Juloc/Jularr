package de.juloc.jularr.core.api

import de.juloc.jularr.core.model.ClientAccount
import de.juloc.jularr.core.model.CueResponse
import de.juloc.jularr.core.model.CueToken
import de.juloc.jularr.core.model.EpisodeProgress
import de.juloc.jularr.core.model.MediaTrack
import de.juloc.jularr.core.model.OfflineDownloadDescriptor
import de.juloc.jularr.core.model.OfflineDownloadPackage
import de.juloc.jularr.core.model.OfflineMedia
import de.juloc.jularr.core.model.OfflineProgressItem
import de.juloc.jularr.core.model.OfflineProgressResult
import de.juloc.jularr.core.model.PlayerEpisode
import de.juloc.jularr.core.model.SubtitleCue
import org.json.JSONArray
import org.json.JSONObject

/** Additive v1 offline-playback endpoints, advertised by the `offlineDownloads` capability. */
interface JularrOfflineApi {
    suspend fun getMe(): ClientAccount
    suspend fun getOfflineDownload(episodeId: String): OfflineDownloadPackage
    suspend fun reconcileOfflineProgress(items: List<OfflineProgressItem>): List<OfflineProgressResult>

    companion object {
        /** Server-side bound of one reconciliation request. */
        const val MaxProgressBatchItems = 100
    }
}

/**
 * Parser for the offline download descriptor. It is shared by the HTTP client
 * and by the offline player, which re-reads the stored descriptor without
 * network access.
 */
object OfflineDownloadJson {
    fun parseDescriptor(json: String): OfflineDownloadDescriptor =
        JSONObject(json).toDescriptor()

    internal fun progressItemsBody(items: List<OfflineProgressItem>): JSONObject =
        JSONObject().put(
            "items",
            JSONArray().apply {
                items.forEach { item ->
                    put(
                        JSONObject()
                            .put("episodeId", item.episodeId)
                            .put("positionMs", item.positionMs)
                            .put("durationMs", item.durationMs ?: JSONObject.NULL)
                            .put("completed", item.completed),
                    )
                }
            },
        )

    internal fun parseProgressResults(json: JSONObject): List<OfflineProgressResult> =
        json.getJSONArray("results").mapObjects { result ->
            OfflineProgressResult(
                episodeId = result.getString("episodeId"),
                outcome = result.getString("outcome"),
                progress = result.objectOrNull("progress")?.toProgress(),
            )
        }

    private fun JSONObject.toDescriptor() = OfflineDownloadDescriptor(
        apiVersion = getInt("apiVersion"),
        issuedAtUtc = getString("issuedAtUtc"),
        episode = getJSONObject("episode").let { episode ->
            PlayerEpisode(
                id = episode.getString("id"),
                animeId = episode.getString("animeId"),
                animeTitle = episode.getString("animeTitle"),
                title = episode.getString("title"),
                seasonNumber = episode.getInt("seasonNumber"),
                number = episode.getInt("number"),
            )
        },
        media = getJSONObject("media").let { media ->
            OfflineMedia(
                mediaFileId = media.getString("mediaFileId"),
                fileName = media.getString("fileName"),
                contentType = media.getString("contentType"),
                sizeBytes = media.getLong("sizeBytes"),
                durationMs = media.longOrNull("durationMs"),
                videoCodec = media.stringOrNull("videoCodec"),
                pixelFormat = media.stringOrNull("pixelFormat"),
                audioCodec = media.stringOrNull("audioCodec"),
                contentUrl = media.getString("contentUrl"),
                eTag = media.getString("eTag"),
                fingerprint = media.getString("fingerprint"),
                fingerprintAlgorithm = media.getString("fingerprintAlgorithm"),
            )
        },
        audioTracks = getJSONArray("audioTracks").mapObjects { it.toTrack() },
        subtitleTracks = getJSONArray("subtitleTracks").mapObjects { it.toTrack() },
        defaultAudioTrackId = stringOrNull("defaultAudioTrackId"),
        defaultSubtitleTrackId = stringOrNull("defaultSubtitleTrackId"),
        learningCues = getJSONObject("learningCues").toCues(),
        progress = getJSONObject("progress").toProgress(),
    )

    private fun JSONObject.toTrack() = MediaTrack(
        id = getString("id"),
        streamIndex = getInt("streamIndex"),
        kind = getString("kind"),
        codec = stringOrNull("codec"),
        language = stringOrNull("language"),
        title = stringOrNull("title"),
        isDefault = getBoolean("isDefault"),
        isForced = getBoolean("isForced"),
        isText = getBoolean("isText"),
    )

    private fun JSONObject.toCues() = CueResponse(
        trackId = stringOrNull("trackId"),
        fromMs = intOrNull("fromMs"),
        toMs = intOrNull("toMs"),
        cues = getJSONArray("cues").mapObjects { cue ->
            SubtitleCue(
                id = cue.getLong("id"),
                startMs = cue.getInt("startMs"),
                endMs = cue.getInt("endMs"),
                text = cue.getString("text"),
                tokens = cue.getJSONArray("tokens").mapObjects { token ->
                    CueToken(
                        surface = token.getString("surface"),
                        termId = token.stringOrNull("termId"),
                        canonical = token.stringOrNull("canonical"),
                        reading = token.stringOrNull("reading"),
                        meaning = token.stringOrNull("meaning"),
                        state = token.getString("state"),
                    )
                },
            )
        },
    )

    private fun JSONObject.toProgress() = EpisodeProgress(
        positionMs = getLong("positionMs"),
        durationMs = longOrNull("durationMs"),
        percent = getInt("percent"),
        isCompleted = getBoolean("isCompleted"),
        updatedAtUtc = stringOrNull("updatedAtUtc"),
    )

    private fun JSONObject.objectOrNull(name: String): JSONObject? =
        if (!has(name) || isNull(name)) null else getJSONObject(name)

    private fun JSONObject.stringOrNull(name: String): String? =
        if (!has(name) || isNull(name)) null else getString(name)

    private fun JSONObject.intOrNull(name: String): Int? =
        if (!has(name) || isNull(name)) null else getInt(name)

    private fun JSONObject.longOrNull(name: String): Long? =
        if (!has(name) || isNull(name)) null else getLong(name)

    private inline fun <T> JSONArray.mapObjects(transform: (JSONObject) -> T): List<T> =
        buildList(length()) {
            for (index in 0 until length()) {
                add(transform(getJSONObject(index)))
            }
        }
}
