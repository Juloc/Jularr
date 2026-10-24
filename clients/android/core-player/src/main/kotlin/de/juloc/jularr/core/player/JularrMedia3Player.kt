package de.juloc.jularr.core.player

import android.content.Context
import android.net.Uri
import androidx.media3.common.AudioAttributes
import androidx.media3.common.MediaItem
import androidx.media3.common.MediaMetadata
import androidx.media3.common.Player
import androidx.media3.common.util.UnstableApi
import androidx.media3.datasource.DefaultDataSource
import androidx.media3.datasource.DefaultHttpDataSource
import androidx.media3.exoplayer.ExoPlayer
import androidx.media3.exoplayer.source.DefaultMediaSourceFactory
import androidx.media3.session.MediaSession
import de.juloc.jularr.core.model.PlayerEpisode
import java.io.Closeable

data class JularrPlaybackMetadata(
    val mediaId: String,
    val title: String,
    val seriesTitle: String? = null,
    val episodeLabel: String? = null,
)

fun PlayerEpisode.toPlaybackMetadata(): JularrPlaybackMetadata =
    JularrPlaybackMetadata(
        mediaId = id,
        title = title,
        seriesTitle = animeTitle,
        episodeLabel = "S${seasonNumber.toString().padStart(2, '0')} " +
            "E${number.toString().padStart(2, '0')}",
    )

@UnstableApi
class JularrMedia3Player(context: Context) : Closeable {
    private val httpDataSourceFactory = DefaultHttpDataSource.Factory()
        .setAllowCrossProtocolRedirects(false)

    // DefaultDataSource delegates http(s) to the authenticated HTTP factory and also reads
    // app-private file:// URIs, which offline playback of downloaded episodes uses.
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

    val mediaSession: MediaSession = MediaSession.Builder(context, exoPlayer).build()

    fun open(
        uri: Uri,
        startPositionMs: Long = 0,
        playWhenReady: Boolean = true,
        requestHeaders: Map<String, String> = emptyMap(),
        metadata: JularrPlaybackMetadata? = null,
        subtitleConfigurations: List<MediaItem.SubtitleConfiguration> = emptyList(),
    ) {
        httpDataSourceFactory.setDefaultRequestProperties(requestHeaders)
        exoPlayer.setMediaItem(buildMediaItem(uri, metadata, subtitleConfigurations))
        exoPlayer.prepare()

        if (startPositionMs > 0) {
            exoPlayer.seekTo(startPositionMs)
        }

        exoPlayer.playWhenReady = playWhenReady
    }

    /**
     * Update credentials on the shared Media3 HTTP factory after an API response rotates
     * the device bearer. This is especially important for subsequent HLS segment requests.
     */
    fun updateRequestHeaders(requestHeaders: Map<String, String>) {
        httpDataSourceFactory.setDefaultRequestProperties(requestHeaders)
    }

    override fun close() {
        mediaSession.release()
        exoPlayer.release()
    }

    private fun buildMediaItem(
        uri: Uri,
        metadata: JularrPlaybackMetadata?,
        subtitleConfigurations: List<MediaItem.SubtitleConfiguration>,
    ): MediaItem {
        val builder = MediaItem.Builder()
            .setUri(uri)
            .setSubtitleConfigurations(subtitleConfigurations)
        if (metadata == null) return builder.build()

        return builder
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
