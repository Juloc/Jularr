package de.juloc.jularr.tv

import androidx.activity.compose.BackHandler
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.offset
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.requiredHeight
import androidx.compose.foundation.layout.requiredWidth
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.lazy.LazyRow
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.Icon
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material.icons.filled.Check
import androidx.compose.material.icons.filled.Pause
import androidx.compose.material.icons.filled.PlayArrow
import androidx.compose.material.icons.filled.Replay
import androidx.compose.material.icons.filled.Settings
import androidx.compose.material.icons.filled.SkipNext
import androidx.compose.material.icons.filled.SkipPrevious
import androidx.compose.material.icons.filled.Subtitles
import androidx.compose.material.icons.filled.VolumeUp
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.focus.FocusRequester
import androidx.compose.ui.focus.focusRequester
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.input.key.Key
import androidx.compose.ui.input.key.KeyEventType
import androidx.compose.ui.input.key.key
import androidx.compose.ui.input.key.onPreviewKeyEvent
import androidx.compose.ui.input.key.type
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import androidx.tv.material3.Button
import androidx.tv.material3.MaterialTheme
import androidx.tv.material3.Text
import de.juloc.jularr.core.model.ClientMediaSegment
import de.juloc.jularr.core.model.ClientTrickplayDescriptor
import de.juloc.jularr.core.model.MediaTrack

@Composable
internal fun PlayerControls(
    episodeTitle: String,
    isPlaying: Boolean,
    playbackEnded: Boolean,
    skipSegment: ClientMediaSegment?,
    onSkipSegment: (ClientMediaSegment) -> Unit,
    previousEpisodeTitle: String?,
    nextEpisodeTitle: String?,
    onPreviousEpisode: () -> Unit,
    onNextEpisode: () -> Unit,
    nextEpisodeFocus: FocusRequester,
    canLearn: Boolean,
    positionMs: Long,
    durationMs: Long,
    bufferedPositionMs: Long,
    trickplay: ClientTrickplayDescriptor?,
    serverOrigin: String,
    requestHeaders: Map<String, String>,
    audioTracks: List<MediaTrack>,
    subtitleTracks: List<MediaTrack>,
    selectedAudioTrackId: String?,
    selectedSubtitleTrackId: String?,
    playbackSpeed: Float,
    hasSpeedOptions: Boolean,
    design: TvPlayerDesign,
    primaryControlFocus: FocusRequester,
    audioTrackFocus: FocusRequester,
    subtitleTrackFocus: FocusRequester,
    settingsFocus: FocusRequester,
    onOpenSettingsPanel: () -> Unit,
    onOpenAudioTracks: () -> Unit,
    onOpenSubtitleTracks: () -> Unit,
    onPlayPause: () -> Unit,
    onScrubActive: (Boolean) -> Unit,
    onSeekTo: (Long) -> Unit,
    onRepeatLine: () -> Unit,
    onLearn: () -> Unit,
    onExit: () -> Unit,
    modifier: Modifier = Modifier,
) {
    val focusColor = rememberTvFocusColor()
    var scrubPreviewMs by remember(durationMs) { mutableStateOf<Long?>(null) }
    LaunchedEffect(scrubPreviewMs) { onScrubActive(scrubPreviewMs != null) }
    DisposableEffect(Unit) { onDispose { onScrubActive(false) } }
    BackHandler(enabled = scrubPreviewMs != null) { scrubPreviewMs = null }
    val progress = if (durationMs > 0) {
        ((scrubPreviewMs ?: positionMs).toFloat() / durationMs.toFloat()).coerceIn(0f, 1f)
    } else {
        0f
    }
    val bufferedProgress = if (durationMs > 0) {
        (bufferedPositionMs.toFloat() / durationMs.toFloat()).coerceIn(0f, 1f)
    } else {
        0f
    }

    Box(
        modifier = modifier
            .background(
                Brush.verticalGradient(
                    0.0f to Color.Black.copy(alpha = 0.35f),
                    0.24f to Color.Transparent,
                    0.52f to Color.Transparent,
                    1.0f to Color.Black.copy(alpha = 0.65f),
                ),
            )
            .padding(horizontal = 48.dp, vertical = 32.dp),
    ) {
        Row(
            modifier = Modifier
                .align(Alignment.TopCenter)
                .fillMaxWidth(),
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.spacedBy(16.dp),
        ) {
            var backFocused by remember { mutableStateOf(false) }
            Box(
                modifier = Modifier
                    .size(40.dp)
                    .clip(CircleShape)
                    .background(if (backFocused) design.accent else design.sheet)
                    .tvFocusIndication(backFocused, focusColor, CircleShape)
                    .clickable(onClick = onExit)
                    .reportFocus { backFocused = it },
                contentAlignment = Alignment.Center,
            ) {
                Icon(
                    imageVector = Icons.AutoMirrored.Filled.ArrowBack,
                    contentDescription = stringResource(R.string.tv_action_back),
                    tint = Color.White,
                    modifier = Modifier.size(22.dp),
                )
            }

            Text(
                text = episodeTitle,
                color = design.subtitleText,
                style = MaterialTheme.typography.titleLarge,
                maxLines = 1,
                modifier = Modifier.weight(1f),
            )
            Text(
                text = "${formatTime(scrubPreviewMs ?: positionMs)} / ${formatTime(durationMs)}",
                color = design.muted,
                style = MaterialTheme.typography.bodyMedium,
            )
        }

        Column(
            modifier = Modifier
                .align(Alignment.BottomCenter)
                .fillMaxWidth(),
            verticalArrangement = Arrangement.spacedBy(14.dp),
        ) {
            if (skipSegment != null) {
                Row(
                    modifier = Modifier.fillMaxWidth(),
                    horizontalArrangement = Arrangement.End,
                ) {
                    Button(onClick = { onSkipSegment(skipSegment) }) {
                        Text(stringResource(skipLabelRes(skipSegment.kind)))
                    }
                }
            }
            var scrubFocused by remember { mutableStateOf(false) }
            if (scrubPreviewMs != null) {
                Text(
                    text = stringResource(R.string.tv_player_seek_hint, formatTime(scrubPreviewMs ?: positionMs)),
                    color = design.subtitleText,
                    style = MaterialTheme.typography.bodyLarge,
                )
            }
            if (playbackEnded) {
                Text(
                    text = if (nextEpisodeTitle == null) stringResource(R.string.tv_player_finished)
                    else stringResource(R.string.tv_player_finished_next, nextEpisodeTitle),
                    color = design.subtitleText,
                    style = MaterialTheme.typography.bodyLarge,
                    maxLines = 1,
                )
            }
            Box(
                modifier = Modifier
                    .fillMaxWidth()
                    .height(18.dp)
                    .clip(RoundedCornerShape(9.dp))
                    .background(design.sheet)
                    .tvFocusIndication(scrubFocused, focusColor, RoundedCornerShape(9.dp))
                    .clickable(enabled = durationMs > 0) {
                        if (scrubPreviewMs == null) {
                            scrubPreviewMs = positionMs.coerceIn(0L, durationMs)
                        } else {
                            onSeekTo(scrubPreviewMs ?: positionMs)
                            scrubPreviewMs = null
                        }
                    }
                    .onPreviewKeyEvent { event ->
                        if (event.type != KeyEventType.KeyDown || durationMs <= 0) {
                            false
                        } else {
                            when (event.key) {
                                Key.DirectionLeft -> {
                                    scrubPreviewMs = ((scrubPreviewMs ?: positionMs) - design.seek.backMs)
                                        .coerceAtLeast(0)
                                    true
                                }
                                Key.DirectionRight -> {
                                    scrubPreviewMs = ((scrubPreviewMs ?: positionMs) + design.seek.forwardMs)
                                        .coerceAtMost(durationMs)
                                    true
                                }
                                else -> false
                            }
                        }
                    }
                    .reportFocus {
                        scrubFocused = it
                        if (!it) scrubPreviewMs = null
                    },
                contentAlignment = Alignment.CenterStart,
            ) {
                Box(
                    modifier = Modifier
                        .fillMaxHeight()
                        .fillMaxWidth(bufferedProgress)
                        .background(design.muted.copy(alpha = 0.55f)),
                )
                Box(
                    modifier = Modifier
                        .fillMaxHeight()
                        .fillMaxWidth(progress)
                        .background(design.accent),
                )
            }

            if (trickplay?.state == "ready" && durationMs > 0) {
                TvSeekScenes(
                    descriptor = trickplay,
                    durationMs = durationMs,
                    serverOrigin = serverOrigin,
                    requestHeaders = requestHeaders,
                    onSeekTo = onSeekTo,
                )
            }

        Row(
            modifier = Modifier.fillMaxWidth(),
            horizontalArrangement = Arrangement.spacedBy(20.dp, Alignment.CenterHorizontally),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            if (previousEpisodeTitle != null) {
                var previousFocused by remember { mutableStateOf(false) }
                Box(
                    modifier = Modifier
                        .size(52.dp)
                        .clip(RoundedCornerShape(12.dp))
                        .background(if (previousFocused) design.accent else design.sheet)
                        .tvFocusIndication(previousFocused, focusColor, RoundedCornerShape(12.dp))
                        .clickable(onClick = onPreviousEpisode)
                        .reportFocus { previousFocused = it },
                    contentAlignment = Alignment.Center,
                ) {
                    Icon(
                        imageVector = Icons.Filled.SkipPrevious,
                        contentDescription = "Previous: $previousEpisodeTitle",
                        tint = Color.White,
                        modifier = Modifier.size(28.dp),
                    )
                }
            }

            var playFocused by remember { mutableStateOf(false) }
            Box(
                modifier = Modifier
                    .size(64.dp)
                    .clip(RoundedCornerShape(16.dp))
                    .background(if (playFocused) design.accent else design.accent.copy(alpha = 0.55f))
                    .focusRequester(primaryControlFocus)
                    .tvFocusIndication(playFocused, focusColor, RoundedCornerShape(16.dp))
                    .clickable(onClick = onPlayPause)
                    .reportFocus { playFocused = it },
                contentAlignment = Alignment.Center,
            ) {
                Icon(
                    imageVector = if (isPlaying) Icons.Filled.Pause else Icons.Filled.PlayArrow,
                    contentDescription = if (isPlaying) "Pause" else "Play",
                    tint = Color.White,
                    modifier = Modifier.size(36.dp),
                )
            }

            if (nextEpisodeTitle != null) {
                var nextFocused by remember { mutableStateOf(false) }
                Box(
                    modifier = Modifier
                        .size(52.dp)
                        .clip(RoundedCornerShape(12.dp))
                        .background(if (nextFocused) design.accent else design.sheet)
                        .focusRequester(nextEpisodeFocus)
                        .tvFocusIndication(nextFocused, focusColor, RoundedCornerShape(12.dp))
                        .clickable(onClick = onNextEpisode)
                        .reportFocus { nextFocused = it },
                    contentAlignment = Alignment.Center,
                ) {
                    Icon(
                        imageVector = Icons.Filled.SkipNext,
                        contentDescription = "Next: $nextEpisodeTitle",
                        tint = Color.White,
                        modifier = Modifier.size(28.dp),
                    )
                }
            }
        }


            Row(
                modifier = Modifier.fillMaxWidth(),
                horizontalArrangement = Arrangement.spacedBy(12.dp, Alignment.CenterHorizontally),
                verticalAlignment = Alignment.CenterVertically,
            ) {
                if (audioTracks.isNotEmpty()) {
                    Button(
                        onClick = onOpenAudioTracks,
                        modifier = Modifier.focusRequester(audioTrackFocus).widthIn(max = 320.dp),
                    ) {
                        Icon(
                            imageVector = Icons.Filled.VolumeUp,
                            contentDescription = stringResource(R.string.tv_player_audio),
                            modifier = Modifier.size(22.dp),
                        )
                        Text(
                            "  ${trackLabel(audioTracks, selectedAudioTrackId, stringResource(R.string.tv_player_default))}",
                            maxLines = 1,
                        )
                    }
                }

                if (subtitleTracks.isNotEmpty()) {
                    Button(
                        onClick = onOpenSubtitleTracks,
                        modifier = Modifier.focusRequester(subtitleTrackFocus).widthIn(max = 360.dp),
                    ) {
                        Icon(
                            imageVector = Icons.Filled.Subtitles,
                            contentDescription = stringResource(R.string.tv_player_subtitles),
                            modifier = Modifier.size(22.dp),
                        )
                        Text(
                            "  ${trackLabel(subtitleTracks, selectedSubtitleTrackId, stringResource(R.string.tv_player_off))}",
                            maxLines = 1,
                        )
                    }
                }
                if (hasSpeedOptions) {
                    Button(
                        onClick = onOpenSettingsPanel,
                        modifier = Modifier.focusRequester(settingsFocus),
                    ) {
                        Icon(Icons.Filled.Settings, contentDescription = stringResource(R.string.tv_player_settings))
                        Text(" " + stringResource(R.string.tv_player_more))
                    }
                }
            }
            if (canLearn) {
                Row(
                    modifier = Modifier.fillMaxWidth(),
                    horizontalArrangement = Arrangement.spacedBy(12.dp, Alignment.CenterHorizontally),
                    verticalAlignment = Alignment.CenterVertically,
                ) {
                    Button(onClick = onRepeatLine) {
                        Icon(
                            imageVector = Icons.Filled.Replay,
                            contentDescription = "Repeat line",
                            modifier = Modifier.size(22.dp),
                        )
                        Text("  Repeat line")
                    }
                    Button(onClick = onLearn) {
                        Text("Learn this line")
                    }
                }
            }
        }
    }
}

internal fun skipLabelRes(kind: String): Int =
    when (kind.lowercase()) {
        "intro" -> R.string.tv_player_skip_intro
        "recap" -> R.string.tv_player_skip_recap
        "outro" -> R.string.tv_player_skip_outro
        "credits" -> R.string.tv_player_skip_credits
        "preview" -> R.string.tv_player_skip_preview
        else -> R.string.tv_player_skip_section
    }

@Composable
private fun TvSeekScenes(
    descriptor: ClientTrickplayDescriptor,
    durationMs: Long,
    serverOrigin: String,
    requestHeaders: Map<String, String>,
    onSeekTo: (Long) -> Unit,
) {
    val columns = descriptor.columns?.takeIf { it > 0 } ?: return
    val rows = descriptor.rows?.takeIf { it > 0 } ?: return
    val intervalMs = descriptor.intervalMs?.takeIf { it > 0 } ?: return
    val count = descriptor.thumbnailCount?.takeIf { it > 0 } ?: return
    val available = minOf(
        count.toLong(),
        descriptor.spriteUrls.size.toLong() * columns * rows,
    ).toInt()
    val samples = remember(available) { tvSeekSceneIndices(available) }
    if (samples.isEmpty()) return
    var selectedSample by remember { mutableStateOf<Int?>(null) }
    Column(verticalArrangement = Arrangement.spacedBy(6.dp)) {
        selectedSample?.let { sample ->
            Text(
                stringResource(R.string.tv_player_scene_seek_to, formatTime(sample.toLong() * intervalMs)),
                color = Color.White.copy(alpha = 0.8f),
                style = MaterialTheme.typography.labelMedium,
            )
        }
        LazyRow(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            items(samples) { sample ->
                val sheetSize = columns * rows
                val spriteIndex = sample / sheetSize
                val spriteUrl = descriptor.spriteUrls[spriteIndex]
                val column = sample % columns
                val row = (sample / columns) % rows
                var focused by remember(sample) { mutableStateOf(false) }
                val shape = RoundedCornerShape(8.dp)
                Box(
                    modifier = Modifier
                        .width(128.dp)
                        .height(72.dp)
                        .clip(shape)
                        .tvFocusIndication(focused, rememberTvFocusColor(), shape)
                        .clickable {
                            onSeekTo((sample.toLong() * intervalMs).coerceIn(0L, durationMs))
                        }
                        .reportFocus {
                            focused = it
                            selectedSample = if (it) sample else if (selectedSample == sample) null else selectedSample
                        },
                ) {
                    TvArtwork(
                            url = spriteUrl,
                            serverOrigin = serverOrigin,
                            requestHeaders = requestHeaders,
                            contentDescription = formatTime(sample.toLong() * intervalMs),
                            modifier = Modifier
                                .offset(x = (-column * 128).dp, y = (-row * 72).dp)
                                .requiredWidth((columns * 128).dp)
                                .requiredHeight((rows * 72).dp),
                            contentScale = androidx.compose.ui.layout.ContentScale.FillBounds,
                        )
                }
            }
        }
    }
}

internal fun tvSeekSceneIndices(availableCount: Int): List<Int> {
    if (availableCount <= 0) return emptyList()
    val shown = minOf(availableCount, 8)
    return (0 until shown).map { index ->
        if (shown == 1) 0 else index.toLong() * (availableCount - 1) / (shown - 1)
    }.map(Long::toInt)
}

@Composable
internal fun TvSettingsPanel(
    focusRequester: FocusRequester,
    playbackSpeed: Float,
    onOpenSpeed: () -> Unit,
    onBack: () -> Unit,
) {
    Box(
        modifier = Modifier
            .fillMaxSize()
            .background(Color.Black.copy(alpha = 0.24f))
            .padding(end = 38.dp, top = 65.dp, bottom = 65.dp),
        contentAlignment = Alignment.CenterEnd,
    ) {
        Column(
            modifier = Modifier
                .widthIn(min = 380.dp, max = 440.dp)
                .clip(RoundedCornerShape(18.dp))
                .background(MaterialTheme.colorScheme.surface)
                .padding(22.dp),
            verticalArrangement = Arrangement.spacedBy(16.dp),
        ) {
            Text(stringResource(R.string.tv_player_settings), style = MaterialTheme.typography.headlineSmall)
            Button(
                onClick = onOpenSpeed,
                modifier = Modifier.fillMaxWidth().focusRequester(focusRequester),
            ) {
                Text(stringResource(R.string.tv_player_speed_value, playbackSpeed.toString()))
            }
            Button(onClick = onBack) { Text(stringResource(R.string.tv_player_close)) }
        }
    }
}

@Composable
internal fun TvSpeedSelectionPanel(
    playbackSpeed: Float,
    allowedSpeeds: List<Float>,
    focusRequester: FocusRequester,
    onSelect: (Float) -> Unit,
    onBack: () -> Unit,
    modifier: Modifier = Modifier,
) {
    Box(
        modifier = Modifier.fillMaxSize().background(Color.Black.copy(alpha = 0.24f)).padding(end = 38.dp),
        contentAlignment = Alignment.CenterEnd,
    ) {
        Column(
            modifier = modifier
                .widthIn(min = 380.dp, max = 440.dp)
                .clip(RoundedCornerShape(20.dp))
                .background(MaterialTheme.colorScheme.surface)
                .padding(28.dp),
            verticalArrangement = Arrangement.spacedBy(12.dp),
        ) {
            Text(stringResource(R.string.tv_player_speed), style = MaterialTheme.typography.headlineMedium)
            allowedSpeeds.ifEmpty { listOf(playbackSpeed) }.forEachIndexed { index, speed ->
                val selected = speed == playbackSpeed
                Button(
                    modifier = Modifier
                        .fillMaxWidth()
                        .then(if (selected || (index == 0 && playbackSpeed !in allowedSpeeds)) Modifier.focusRequester(focusRequester) else Modifier),
                    onClick = { onSelect(speed) },
                ) {
                    if (selected) Icon(Icons.Filled.Check, contentDescription = null)
                    Text("${speed}x")
                }
            }
            Button(onClick = onBack) { Text("Back") }
        }
    }
}

@Composable
internal fun TvTrackSelectionPanel(
    title: String,
    tracks: List<MediaTrack>,
    selectedId: String?,
    allowOff: Boolean,
    selectionFailed: Boolean,
    focusRequester: FocusRequester,
    onSelect: (String?) -> Unit,
    onBack: () -> Unit,
    modifier: Modifier = Modifier,
) {
    Box(
        modifier = Modifier
            .fillMaxSize()
            .background(Color.Black.copy(alpha = 0.24f))
            .padding(end = 38.dp),
        contentAlignment = Alignment.CenterEnd,
    ) {
        Column(
            modifier = modifier
                .widthIn(min = 380.dp, max = 440.dp)
                .clip(RoundedCornerShape(20.dp))
                .background(MaterialTheme.colorScheme.surface)
                .padding(22.dp),
            verticalArrangement = Arrangement.spacedBy(12.dp),
        ) {
            Text(title, style = MaterialTheme.typography.headlineMedium)
            if (selectionFailed) {
                Text(stringResource(R.string.tv_player_track_unavailable), color = MaterialTheme.colorScheme.error)
            }
            Column(
                modifier = Modifier
                    .fillMaxWidth()
                    .heightIn(max = 440.dp)
                    .verticalScroll(rememberScrollState()),
                verticalArrangement = Arrangement.spacedBy(8.dp),
            ) {
                val matched = selectedId == null || tracks.any { it.id == selectedId }
                if (allowOff) {
                    Button(
                        modifier = Modifier
                            .fillMaxWidth()
                            .then(if (selectedId == null) Modifier.focusRequester(focusRequester) else Modifier),
                        onClick = { onSelect(null) },
                    ) {
                        if (selectedId == null) Icon(Icons.Filled.Check, contentDescription = null)
                        Text(stringResource(R.string.tv_player_off))
                    }
                }
                tracks.forEachIndexed { index, track ->
                    val selected = track.id == selectedId
                    Button(
                        modifier = Modifier
                            .fillMaxWidth()
                            .then(
                                if (selected || (!matched && index == 0) ||
                                    (!allowOff && selectedId == null && index == 0)
                                ) Modifier.focusRequester(focusRequester) else Modifier,
                            ),
                        onClick = { onSelect(track.id) },
                    ) {
                        if (selected) Icon(Icons.Filled.Check, contentDescription = null)
                        Text(track.title ?: track.language ?: track.id, maxLines = 2)
                    }
                }
            }
            Button(onClick = onBack) { Text("Back") }
        }
    }
}

private fun trackLabel(
    tracks: List<MediaTrack>,
    selectedId: String?,
    fallback: String,
): String =
    selectedId?.let { id ->
        tracks.firstOrNull { it.id == id }?.let { it.title ?: it.language ?: it.id }
    } ?: fallback

private fun formatTime(valueMs: Long): String {
    val totalSeconds = valueMs.coerceAtLeast(0) / 1000
    val minutes = totalSeconds / 60
    val seconds = totalSeconds % 60
    val hours = minutes / 60
    return if (hours > 0) "%d:%02d:%02d".format(hours, minutes % 60, seconds) else "%d:%02d".format(minutes, seconds)
}
