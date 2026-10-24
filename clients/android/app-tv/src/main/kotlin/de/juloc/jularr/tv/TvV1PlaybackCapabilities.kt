package de.juloc.jularr.tv

import android.media.MediaCodecList
import de.juloc.jularr.core.api.playback.TvPlaybackV1Capabilities

/**
 * Advertise video/audio codecs with real Android decoder support, rather than pretending
 * every TV can decode every format. The supported containers are handled by Media3 extractors.
 */
internal object TvV1PlaybackCapabilities {
    fun read(): TvPlaybackV1Capabilities {
        val supported = MediaCodecList(MediaCodecList.REGULAR_CODECS).codecInfos
            .asSequence()
            .filterNot { it.isEncoder }
            .flatMap { it.supportedTypes.asSequence() }
            .map { it.lowercase() }
            .toSet()
        val video = linkedMapOf(
            "h264" to "video/avc",
            "hevc" to "video/hevc",
            "vp9" to "video/x-vnd.on2.vp9",
            "vp8" to "video/x-vnd.on2.vp8",
            "av1" to "video/av01",
            "mpeg4" to "video/mp4v-es",
        ).filterValues { it in supported }.keys.toList()
        val audio = linkedMapOf(
            "aac" to "audio/mp4a-latm",
            "mp3" to "audio/mpeg",
            "opus" to "audio/opus",
            "vorbis" to "audio/vorbis",
            "flac" to "audio/flac",
            "ac3" to "audio/ac3",
            "eac3" to "audio/eac3",
        ).filterValues { it in supported }.keys.toList()
        val containers = listOf("mp4", "mkv", "webm", "ts") +
            if ("h264" in video && "aac" in audio) listOf("hls") else emptyList()
        return TvPlaybackV1Capabilities(containers, video, audio)
    }
}
