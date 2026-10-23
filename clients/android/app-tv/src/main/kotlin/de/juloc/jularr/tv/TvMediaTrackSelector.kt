package de.juloc.jularr.tv

import androidx.annotation.OptIn
import androidx.media3.common.C
import androidx.media3.common.Format
import androidx.media3.common.Player
import androidx.media3.common.TrackSelectionOverride
import androidx.media3.common.util.UnstableApi
import de.juloc.jularr.core.model.MediaTrack

@OptIn(UnstableApi::class)
object TvMediaTrackSelector {
    fun selectAudio(
        player: Player,
        requested: MediaTrack,
    ): Boolean =
        selectTrack(
            player = player,
            trackType = C.TRACK_TYPE_AUDIO,
            requested = requested,
        )

    fun selectSubtitle(
        player: Player,
        requested: MediaTrack?,
    ): Boolean {
        if (requested == null) {
            player.trackSelectionParameters = player.trackSelectionParameters
                .buildUpon()
                .clearOverridesOfType(C.TRACK_TYPE_TEXT)
                .setTrackTypeDisabled(C.TRACK_TYPE_TEXT, true)
                .build()
            return true
        }

        return selectTrack(
            player = player,
            trackType = C.TRACK_TYPE_TEXT,
            requested = requested,
        )
    }

    private fun selectTrack(
        player: Player,
        trackType: Int,
        requested: MediaTrack,
    ): Boolean {
        val candidate = player.currentTracks.groups
            .asSequence()
            .filter { it.type == trackType }
            .flatMap { group ->
                (0 until group.length).asSequence().map { index ->
                    Candidate(
                        group = group,
                        index = index,
                        score = score(
                            format = group.getTrackFormat(index),
                            requested = requested,
                        ),
                    )
                }
            }
            .filter { it.score > 0 }
            .maxByOrNull { it.score }
            ?: return false

        player.trackSelectionParameters = player.trackSelectionParameters
            .buildUpon()
            .clearOverridesOfType(trackType)
            .setTrackTypeDisabled(trackType, false)
            .setOverrideForType(
                TrackSelectionOverride(
                    candidate.group.mediaTrackGroup,
                    listOf(candidate.index),
                ),
            )
            .build()
        return true
    }

    internal fun score(
        format: Format,
        requested: MediaTrack,
    ): Int =
        score(
            language = format.language,
            label = format.label,
            sampleMimeType = format.sampleMimeType,
            selectionFlags = format.selectionFlags,
            requested = requested,
        )

    /**
     * Scores plain track attributes so the matching rules stay testable on the JVM:
     * building a Media3 [Format] touches Android framework stubs that throw in unit tests.
     */
    internal fun score(
        language: String?,
        label: String?,
        sampleMimeType: String?,
        selectionFlags: Int,
        requested: MediaTrack,
    ): Int {
        var score = 0

        if (!requested.language.isNullOrBlank() &&
            requested.language.equals(language, ignoreCase = true)
        ) {
            score += 6
        }

        if (!requested.title.isNullOrBlank() &&
            requested.title.equals(label, ignoreCase = true)
        ) {
            score += 5
        }

        val codec = requested.codec?.lowercase()
        val mime = sampleMimeType?.lowercase()
        if (codec != null && mime != null && codecMatchesMime(codec, mime)) {
            score += 3
        }

        if (requested.isDefault &&
            selectionFlags and C.SELECTION_FLAG_DEFAULT != 0
        ) {
            score += 1
        }

        if (requested.isForced &&
            selectionFlags and C.SELECTION_FLAG_FORCED != 0
        ) {
            score += 1
        }

        return score
    }

    private fun codecMatchesMime(
        codec: String,
        mime: String,
    ): Boolean =
        when (codec) {
            "aac" -> mime == "audio/mp4a-latm"
            "opus" -> mime == "audio/opus"
            "vorbis" -> mime == "audio/vorbis"
            "flac" -> mime == "audio/flac"
            "ac3" -> mime == "audio/ac3"
            "eac3" -> mime == "audio/eac3"
            "ass", "ssa" -> mime.contains("ssa") || mime.contains("ass")
            "subrip", "srt" -> mime.contains("subrip")
            "webvtt" -> mime.contains("vtt")
            else -> mime.contains(codec)
        }

    private data class Candidate(
        val group: androidx.media3.common.Tracks.Group,
        val index: Int,
        val score: Int,
    )
}
