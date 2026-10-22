package de.juloc.jularr.tv

import android.net.Uri
import androidx.annotation.OptIn
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.platform.LocalContext
import androidx.media3.common.util.UnstableApi
import androidx.tv.material3.Button
import androidx.tv.material3.MaterialTheme
import androidx.tv.material3.Text
import de.juloc.jularr.core.api.AppApiException
import de.juloc.jularr.core.api.DeviceTokenStore
import de.juloc.jularr.core.api.playback.HttpTvPlaybackV1Client
import de.juloc.jularr.core.api.playback.TvPlaybackV1Client
import de.juloc.jularr.core.api.playback.TvPlaybackV1Plan
import de.juloc.jularr.core.player.JularrMedia3Player
import de.juloc.jularr.core.player.JularrPlaybackMetadata
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.delay
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch

/**
 * Preserves the complete original TvPlayerScreen/Media3 UI while the clean-start server
 * decides DirectPlay, Remux or HLS Transcode and owns the progress session.
 */
@OptIn(UnstableApi::class)
@Composable
internal fun TvV1PlaybackHost(
    origin: String,
    tokenStore: DeviceTokenStore,
    mediaAssetId: Long,
    title: String,
    onExit: () -> Unit,
    onUnauthorized: () -> Unit,
    createClient: (String, DeviceTokenStore) -> TvPlaybackV1Client = { server, tokens ->
        HttpTvPlaybackV1Client(server, tokens)
    },
) {
    val context = LocalContext.current
    val player = remember(context) { JularrMedia3Player(context.applicationContext) }
    val api = remember(origin, tokenStore) { createClient(origin, tokenStore) }
    val scope = rememberCoroutineScope()
    var plan by remember(mediaAssetId) { mutableStateOf<TvPlaybackV1Plan?>(null) }
    var loading by remember(mediaAssetId) { mutableStateOf(true) }
    var error by remember(mediaAssetId) { mutableStateOf<String?>(null) }
    var retry by remember(mediaAssetId) { mutableStateOf(0) }
    var finishing by remember(mediaAssetId) { mutableStateOf(false) }
    var positionMs by remember(mediaAssetId) { mutableStateOf(0L) }
    var durationMs by remember(mediaAssetId) { mutableStateOf<Long?>(null) }

    DisposableEffect(player) {
        onDispose {
            player.player.stop()
            player.close()
        }
    }

    LaunchedEffect(mediaAssetId, retry) {
        loading = true
        error = null
        try {
            val current = api.start(mediaAssetId, TvV1PlaybackCapabilities.read())
            // The server may rotate credentials in the session-creation response.
            val credential = checkNotNull(tokenStore.current) { "The device token is no longer valid." }
            val uri = Uri.parse(current.streamUrl)
            check(uri.scheme == "http" || uri.scheme == "https")
            plan = current
            positionMs = current.resumePositionMs
            durationMs = current.durationMs
            player.open(
                uri = uri,
                startPositionMs = current.resumePositionMs,
                requestHeaders = mapOf("Authorization" to "Bearer $credential"),
                metadata = JularrPlaybackMetadata(mediaAssetId.toString(), title),
            )
        } catch (cancellation: CancellationException) {
            throw cancellation
        } catch (failure: AppApiException) {
            if (failure.statusCode == 401) onUnauthorized()
            else error = "Jularr Playback: ${failure.code} (HTTP ${failure.statusCode})"
        } catch (failure: Exception) {
            error = failure.message ?: "Die Wiedergabe konnte nicht gestartet werden."
        } finally {
            loading = false
        }
    }

    // Session heartbeat; never update before the Media3 player has actually started.
    LaunchedEffect(plan, finishing) {
        val active = plan ?: return@LaunchedEffect
        if (finishing) return@LaunchedEffect
        while (isActive) {
            delay(10_000L)
            if (finishing) break
            try {
                api.update(
                    active.playbackSessionId,
                    player.player.currentPosition.coerceAtLeast(0),
                    player.player.duration.takeIf { it > 0 } ?: active.durationMs,
                    isEnded = false,
                    isCompleted = false,
                )
            } catch (cancel: CancellationException) {
                throw cancel
            } catch (failure: AppApiException) {
                if (failure.statusCode == 401) {
                    onUnauthorized()
                    return@LaunchedEffect
                }
                error = "Fortschritt konnte nicht gespeichert werden: ${failure.code}"
                return@LaunchedEffect
            } catch (failure: Exception) {
                error = "Fortschritt konnte nicht gespeichert werden: ${failure.message.orEmpty()}"
                return@LaunchedEffect
            }
        }
    }

    fun completePlayback(completed: Boolean) {
        if (finishing) return
        finishing = true
        val current = plan
        val finalPosition = player.player.currentPosition.coerceAtLeast(positionMs)
        val finalDuration = player.player.duration.takeIf { it > 0 } ?: durationMs
        scope.launch {
            if (current != null) {
                try {
                    api.update(
                        current.playbackSessionId,
                        finalPosition,
                        finalDuration,
                        isEnded = true,
                        isCompleted = completed,
                    )
                } catch (cancel: CancellationException) {
                    throw cancel
                } catch (failure: AppApiException) {
                    // A revoked session must return to device pairing.
                    if (failure.statusCode == 401) {
                        onUnauthorized()
                        return@launch
                    }
                    error = "Fortschritt konnte nicht beendet werden: ${failure.code}"
                    finishing = false
                    return@launch
                } catch (failure: Exception) {
                    error = failure.message ?: "Fortschritt konnte nicht beendet werden."
                    finishing = false
                    return@launch
                }
            }
            player.player.stop()
            onExit()
        }
    }

    when {
        error != null -> TvCenteredPanel(
            title = "Wiedergabefehler",
            description = requireNotNull(error),
        ) {
            Button(onClick = {
                if (plan == null) retry += 1
                else {
                    // Allow the server to close its old session before starting another.
                    completePlayback(completed = false)
                }
            }) {
                Text(if (plan == null) "Erneut versuchen" else "Zurück zu den Details")
            }
            Button(onClick = onExit) { Text("Zurück") }
        }
        loading || plan == null -> TvCenteredPanel(
            title = "Jularr TV",
            description = "Wiedergabesession wird vorbereitet …",
        ) {
            Text(title, style = MaterialTheme.typography.titleLarge)
            Button(onClick = { completePlayback(completed = false) }) { Text("Abbrechen") }
        }
        else -> TvPlayerScreen(
            player = player,
            episodeTitle = title,
            currentCue = null,
            serverOrigin = origin,
            requestHeaders = tokenStore.current?.let { mapOf("Authorization" to "Bearer $it") }.orEmpty(),
            playbackSpeeds = listOf(0.75f, 1f, 1.25f, 1.5f, 2f),
            onPositionChanged = { position, duration, _ ->
                positionMs = position
                if (duration > 0) durationMs = duration
            },
            onSeeked = { position, duration, _ ->
                positionMs = position
                if (duration > 0) durationMs = duration
            },
            onPlaybackEnded = { position, duration ->
                positionMs = position
                if (duration > 0) durationMs = duration
                completePlayback(completed = true)
            },
            onPlaybackFailure = { position ->
                positionMs = position
                error = "Media3 konnte diesen Stream nicht wiedergeben."
            },
            onRetryPlayback = { retry += 1 },
            onExit = { completePlayback(completed = false) },
            onSetTermState = { _, _ -> },
            onOpenOnPhone = { _, _ -> },
        )
    }
}
