package de.juloc.jularr.core.player

import android.content.Context
import android.net.Uri
import androidx.media3.common.AudioAttributes
import androidx.media3.common.C
import androidx.media3.common.MediaItem
import androidx.media3.common.MediaMetadata
import androidx.media3.common.Player
import androidx.media3.common.util.UnstableApi
import androidx.media3.datasource.DefaultDataSource
import androidx.media3.datasource.DefaultHttpDataSource
import androidx.media3.exoplayer.ExoPlayer
import androidx.media3.exoplayer.source.DefaultMediaSourceFactory
import java.io.Closeable

/** What the player shows about the title it plays. */
data class PlaybackMetadata(
    val mediaId: String,
    val title: String,
    val seriesTitle: String? = null,
    val episodeLabel: String? = null,
)

/**
 * The player of the television. It reads the stream of the instance with the device token, so
 * the instance knows who watches.
 */
@UnstableApi
class JularrMedia3Player(context: Context) : Closeable {
    private val httpDataSourceFactory = DefaultHttpDataSource.Factory()
        .setAllowCrossProtocolRedirects(false)

    // The data source sends the token of the device with every request of the stream.
    private val mediaSourceFactory = DefaultMediaSourceFactory(context)
        .setDataSourceFactory(DefaultDataSource.Factory(context, httpDataSourceFactory))

    private val exoPlayer: ExoPlayer = ExoPlayer.Builder(context)
        .setMediaSourceFactory(mediaSourceFactory)
        .build()
        .also {
            it.setAudioAttributes(AudioAttributes.DEFAULT, true)
        }

    val player: Player
        get() = exoPlayer

    /** Opens the stream and continues where the viewer stopped. */
    fun open(
        uri: Uri,
        startPositionMs: Long = 0,
        playWhenReady: Boolean = true,
        requestHeaders: Map<String, String> = emptyMap(),
        metadata: PlaybackMetadata? = null,
    ) {
        httpDataSourceFactory.setDefaultRequestProperties(requestHeaders)
        exoPlayer.setMediaItem(buildMediaItem(uri, metadata))
        exoPlayer.prepare()
        if (startPositionMs > 0) {
            exoPlayer.seekTo(startPositionMs)
        }
        exoPlayer.playWhenReady = playWhenReady
    }

    /** Seeks to a place in the stream. */
    fun seekTo(positionMs: Long) {
        exoPlayer.seekTo(positionMs.coerceIn(0, exoPlayer.duration.takeIf { it > 0 } ?: Long.MAX_VALUE))
    }

    /** The place in the stream, in milliseconds. */
    fun positionMs(): Long = exoPlayer.currentPosition.coerceAtLeast(0)

    /** The length of the stream, in milliseconds, when the instance knows it. */
    fun durationMs(): Long? = exoPlayer.duration.takeIf { it > 0 && it != C.TIME_UNSET }

    /** Plays or pauses the stream. */
    fun togglePlayPause() {
        if (exoPlayer.isPlaying) {
            exoPlayer.pause()
        } else {
            exoPlayer.play()
        }
    }

    override fun close() {
        exoPlayer.release()
    }

    private fun buildMediaItem(uri: Uri, metadata: PlaybackMetadata?): MediaItem {
        if (metadata == null) {
            return MediaItem.fromUri(uri)
        }
        return MediaItem.Builder()
            .setUri(uri)
            .setMediaId(metadata.mediaId)
            .setMediaMetadata(
                MediaMetadata.Builder()
                    .setTitle(metadata.title)
                    .setArtist(metadata.seriesTitle)
                    .setSubtitle(metadata.episodeLabel)
                    .build(),
            )
            .build()
    }
}
