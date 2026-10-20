package de.juloc.jularr.tv

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableLongStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.input.key.onKeyEvent
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.viewinterop.AndroidView
import androidx.compose.ui.unit.dp
import androidx.media3.ui.PlayerView
import de.juloc.jularr.core.player.JularrMedia3Player
import kotlinx.coroutines.delay

/**
 * The player of the television: the stream of the instance with the D-pad's controls. OK plays
 * and pauses, left and right seek, and the progress goes back to the instance.
 */
@androidx.annotation.OptIn(androidx.media3.common.util.UnstableApi::class)
@Composable
fun TvPlayerScreen(controller: TvController) {
    val colors = LocalTvColors.current
    val type = LocalTvType.current
    val context = LocalContext.current
    val player = remember { JularrMedia3Player(context) }
    var position by remember { mutableLongStateOf(0) }
    var duration by remember { mutableStateOf<Long?>(null) }

    // The stream of the instance opens as soon as the plan is there.
    LaunchedEffect(controller.playbackPlan) {
        val plan = controller.playbackPlan ?: return@LaunchedEffect
        val stream = if (plan.streamUrl.startsWith("http")) plan.streamUrl else controller.origin + plan.streamUrl
        val headers = controller.deviceToken?.let { mapOf("Authorization" to "Bearer $it") } ?: emptyMap()
        player.open(
            uri = android.net.Uri.parse(stream),
            startPositionMs = plan.resumePositionMs,
            requestHeaders = headers,
            metadata = de.juloc.jularr.core.player.PlaybackMetadata(
                mediaId = plan.playbackSessionId,
                title = controller.playingTitle,
            ),
        )
        duration = plan.durationMs
    }

    // The place in the stream moves with the picture; the instance hears the progress every
    // few seconds.
    LaunchedEffect(Unit) {
        var reported = 0L
        while (true) {
            delay(500)
            val current = player.positionMs()
            position = current
            if (current - reported >= ProgressIntervalMs) {
                reported = current
                val length = player.durationMs() ?: duration
                controller.reportProgress(current, length, isEnded = false, isCompleted = false)
            }
        }
    }

    DisposableEffect(Unit) {
        onDispose {
            controller.reportProgress(player.positionMs(), player.durationMs(), isEnded = true, isCompleted = false)
            player.close()
        }
    }

    Box(
        modifier = Modifier
            .fillMaxSize()
            .background(colors.background)
            .onKeyEvent { event ->
                val native = event.nativeKeyEvent
                if (native.action != android.view.KeyEvent.ACTION_DOWN) {
                    return@onKeyEvent false
                }
                when (native.keyCode) {
                    android.view.KeyEvent.KEYCODE_DPAD_CENTER,
                    android.view.KeyEvent.KEYCODE_ENTER,
                    -> {
                        player.togglePlayPause()
                        true
                    }

                    android.view.KeyEvent.KEYCODE_DPAD_LEFT -> {
                        player.seekTo(player.positionMs() - SeekStepMs)
                        true
                    }

                    android.view.KeyEvent.KEYCODE_DPAD_RIGHT -> {
                        player.seekTo(player.positionMs() + SeekStepMs)
                        true
                    }

                    else -> false
                }
            },
    ) {
        AndroidView(
            factory = { viewContext ->
                PlayerView(viewContext).apply {
                    useController = false
                    this.player = player.player
                    setShutterBackgroundColor(android.graphics.Color.BLACK)
                }
            },
            modifier = Modifier.fillMaxSize(),
        )

        // The line of the title with the place in the stream.
        Row(
            modifier = Modifier
                .align(Alignment.BottomStart)
                .fillMaxWidth()
                .background(colors.background.copy(alpha = 0.75f))
                .padding(horizontal = TvLayout.ContentPadding, vertical = 10.dp),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            Text(
                text = controller.playingTitle,
                fontSize = type.body,
                color = colors.text,
            )
            Text(
                text = "  ${formatTime(position)} / ${formatTime(duration ?: 0)}",
                fontSize = type.body,
                color = colors.muted,
            )
        }
    }
}

/** The time of the stream as the television shows it. */
internal fun formatTime(milliseconds: Long): String {
    val total = (milliseconds / 1000).coerceAtLeast(0)
    val hours = total / 3600
    val minutes = (total % 3600) / 60
    val seconds = total % 60
    return if (hours > 0) {
        "%d:%02d:%02d".format(hours, minutes, seconds)
    } else {
        "%d:%02d".format(minutes, seconds)
    }
}

/** The seconds the D-pad seeks. */
private const val SeekStepMs = 10_000L

/** The seconds between two reports of the progress. */
private const val ProgressIntervalMs = 10_000L
