package de.juloc.jularr.tv

import de.juloc.jularr.core.model.ClientCapabilities
import de.juloc.jularr.core.model.PlayerBootstrap
import de.juloc.jularr.core.player.DevicePlaybackSupport
import de.juloc.jularr.core.player.PlaybackSelector
import de.juloc.jularr.core.player.PlaybackTransport
import java.net.URI
import java.net.URLEncoder
import java.nio.charset.StandardCharsets

data class TvPlaybackPlan(
    val transport: PlaybackTransport,
    val uri: String,
    val startPositionMs: Long,
)

object TvPlaybackPlanner {
    fun plan(
        serverOrigin: String,
        capabilities: ClientCapabilities,
        bootstrap: PlayerBootstrap,
        directSupported: Boolean,
        startPositionMs: Long,
    ): TvPlaybackPlan {
        val media = bootstrap.media
            ?: throw IllegalStateException("This episode does not have playable media.")

        val transport = PlaybackSelector.select(
            capabilities = capabilities,
            bootstrap = bootstrap,
            device = DevicePlaybackSupport(
                directContainerAndCodecSupported = directSupported,
            ),
        )

        val normalizedPosition = startPositionMs.coerceAtLeast(0)
        val route = when (transport) {
            PlaybackTransport.DIRECT -> media.directContentUrl
            PlaybackTransport.LIVE_MP4_FALLBACK,
            PlaybackTransport.HLS_FALLBACK,
            -> {
                val fallback = bootstrap.fallback.url
                    ?: throw IllegalStateException("Server fallback URL is missing.")
                if (bootstrap.fallback.canRestartAtPosition && normalizedPosition > 0) {
                    withQueryParameter(
                        fallback,
                        "startSeconds",
                        formatSeconds(normalizedPosition),
                    )
                } else {
                    fallback
                }
            }
        }

        return TvPlaybackPlan(
            transport = transport,
            uri = resolveSameOrigin(serverOrigin, route),
            startPositionMs = if (transport == PlaybackTransport.DIRECT) {
                normalizedPosition
            } else {
                0L
            },
        )
    }

    fun resolveSameOrigin(
        serverOrigin: String,
        route: String,
    ): String {
        val origin = URI(TvServerOrigin.normalize(serverOrigin))
        val resolved = origin.resolve(route)

        require(
            origin.scheme.equals(resolved.scheme, ignoreCase = true) &&
                origin.host.equals(resolved.host, ignoreCase = true) &&
                effectivePort(origin) == effectivePort(resolved),
        ) {
            "Playback route escaped the configured Jularr server origin."
        }

        return resolved.toString()
    }

    private fun withQueryParameter(
        route: String,
        name: String,
        value: String,
    ): String {
        val separator = if ('?' in route) '&' else '?'
        return buildString {
            append(route)
            append(separator)
            append(URLEncoder.encode(name, StandardCharsets.UTF_8.name()))
            append('=')
            append(URLEncoder.encode(value, StandardCharsets.UTF_8.name()))
        }
    }

    private fun formatSeconds(positionMs: Long): String {
        val seconds = positionMs / 1000
        val millis = positionMs % 1000
        return if (millis == 0L) {
            seconds.toString()
        } else {
            "%d.%03d".format(seconds, millis)
        }
    }

    private fun effectivePort(uri: URI): Int =
        when {
            uri.port >= 0 -> uri.port
            uri.scheme.equals("https", ignoreCase = true) -> 443
            else -> 80
        }
}
