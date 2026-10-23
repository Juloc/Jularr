package de.juloc.jularr.tv

import android.media.MediaCodecList
import android.media.MediaFormat
import de.juloc.jularr.core.model.PlayerBootstrap

object TvDeviceCodecSupport {
    fun supportsDirectPlayback(bootstrap: PlayerBootstrap): Boolean {
        val media = bootstrap.media ?: return false
        val videoMime = videoMime(media.videoCodec) ?: return false
        val audioMime = media.audioCodec
            ?.takeIf { it.isNotBlank() }
            ?.let(::audioMime)

        return hasDecoder(videoMime) &&
            (audioMime == null || hasDecoder(audioMime))
    }

    internal fun videoMime(codec: String?): String? =
        when (codec?.trim()?.lowercase()) {
            "h264", "avc", "avc1" -> "video/avc"
            "h265", "hevc", "hev1", "hvc1" -> "video/hevc"
            "av1", "av01" -> "video/av01"
            "vp9", "vp09" -> "video/x-vnd.on2.vp9"
            "vp8", "vp08" -> "video/x-vnd.on2.vp8"
            "mpeg4" -> "video/mp4v-es"
            else -> null
        }

    internal fun audioMime(codec: String?): String? =
        when (codec?.trim()?.lowercase()) {
            "aac", "mp4a" -> "audio/mp4a-latm"
            "opus" -> "audio/opus"
            "vorbis" -> "audio/vorbis"
            "flac" -> "audio/flac"
            "ac3", "ac-3" -> "audio/ac3"
            "eac3", "e-ac-3" -> "audio/eac3"
            "mp3", "mp2", "mpeg" -> "audio/mpeg"
            else -> null
        }

    private fun hasDecoder(mime: String): Boolean =
        runCatching {
            val format = MediaFormat().apply {
                setString(MediaFormat.KEY_MIME, mime)
            }
            MediaCodecList(MediaCodecList.ALL_CODECS)
                .findDecoderForFormat(format) != null
        }.getOrDefault(false)
}
