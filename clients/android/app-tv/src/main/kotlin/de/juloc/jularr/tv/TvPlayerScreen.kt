package de.juloc.jularr.tv

import android.view.SurfaceView
import androidx.activity.compose.BackHandler
import androidx.annotation.OptIn
import androidx.compose.foundation.background
import androidx.compose.foundation.focusable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.lazy.LazyRow
import androidx.compose.foundation.lazy.itemsIndexed
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberUpdatedState
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.focus.FocusRequester
import androidx.compose.ui.focus.onFocusChanged
import androidx.compose.ui.focus.focusRequester
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.input.key.Key
import androidx.compose.ui.input.key.KeyEventType
import androidx.compose.ui.input.key.key
import androidx.compose.ui.input.key.onPreviewKeyEvent
import androidx.compose.ui.input.key.type
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.viewinterop.AndroidView
import androidx.media3.common.PlaybackException
import androidx.media3.common.Player
import androidx.media3.common.PlaybackParameters
import androidx.media3.common.util.UnstableApi
import androidx.tv.material3.Button
import androidx.tv.material3.MaterialTheme
import androidx.tv.material3.Surface
import androidx.tv.material3.Text
import de.juloc.jularr.core.model.MediaTrack
import de.juloc.jularr.core.model.ClientMediaSegment
import de.juloc.jularr.core.model.ClientTrickplayDescriptor
import de.juloc.jularr.core.model.SubtitleCue
import de.juloc.jularr.core.player.JularrMedia3Player
import de.juloc.jularr.core.session.PlaybackCommand
import kotlinx.coroutines.delay

private enum class TvPlayerPanel { AUDIO, SUBTITLES, SETTINGS, SPEED }

@OptIn(UnstableApi::class)
@Composable
fun TvPlayerScreen(
    player: JularrMedia3Player,
    episodeTitle: String,
    currentCue: SubtitleCue?,
    skipSegments: List<ClientMediaSegment> = emptyList(),
    trickplay: ClientTrickplayDescriptor? = null,
    playbackSpeeds: List<Float> = emptyList(),
    serverOrigin: String = "",
    requestHeaders: Map<String, String> = emptyMap(),
    audioTracks: List<MediaTrack> = emptyList(),
    subtitleTracks: List<MediaTrack> = emptyList(),
    selectedAudioTrackId: String? = null,
    selectedSubtitleTrackId: String? = null,
    onSelectAudioTrack: (String) -> Boolean = { false },
    onSelectSubtitleTrack: (String?) -> Boolean = { false },
    onPlaybackSpeedChanged: () -> Unit = {},
    onPositionChanged: (positionMs: Long, durationMs: Long, isPlaying: Boolean) -> Unit = { _, _, _ -> },
    onSeeked: (positionMs: Long, durationMs: Long, isPlaying: Boolean) -> Unit = { _, _, _ -> },
    onPlaybackEnded: (positionMs: Long, durationMs: Long) -> Unit = { _, _ -> },
    previousEpisodeTitle: String? = null,
    nextEpisodeTitle: String? = null,
    onPreviousEpisode: () -> Unit = {},
    onNextEpisode: () -> Unit = {},
    onPlaybackFailure: (positionMs: Long) -> Unit = {},
    playbackError: String? = null,
    onRetryPlayback: () -> Unit = {},
    canOpenOnPhone: Boolean = false,
    remoteCommand: PlaybackCommand? = null,
    companionVisible: Boolean = false,
    onCloseCompanion: () -> Unit = {},
    companionOverlay: (@Composable () -> Unit)? = null,
    onExit: () -> Unit,
    onSetTermState: (termId: String, state: String) -> Unit,
    onSelectedTermChanged: (String?) -> Unit = {},
    onOpenOnPhone: (cueId: Long?, termId: String?) -> Unit,
    modifier: Modifier = Modifier,
) {
    val context = LocalContext.current
    val design = remember { TvPlayerDesignLoader.load(context) }
    val currentOnPositionChanged by rememberUpdatedState(onPositionChanged)
    val currentOnSeeked by rememberUpdatedState(onSeeked)
    val currentOnPlaybackEnded by rememberUpdatedState(onPlaybackEnded)
    val currentOnPlaybackFailure by rememberUpdatedState(onPlaybackFailure)
    val currentOnPlaybackSpeedChanged by rememberUpdatedState(onPlaybackSpeedChanged)
    var uiState by remember { mutableStateOf(TvPlayerUiState(controlsVisible = true)) }
    var controlsInteractionRevision by remember { mutableIntStateOf(0) }
    val playerFocus = remember { FocusRequester() }
    val primaryControlFocus = remember { FocusRequester() }
    val learningOverlayFocus = remember { FocusRequester() }
    val nextEpisodeFocus = remember { FocusRequester() }
    val audioTrackFocus = remember { FocusRequester() }
    val subtitleTrackFocus = remember { FocusRequester() }
    val speedControlFocus = remember { FocusRequester() }
    val trackPanelFocus = remember { FocusRequester() }
    val retryFocus = remember { FocusRequester() }
    val skipFocus = remember { FocusRequester() }
    val settingsFocus = remember { FocusRequester() }
    var trackPanel by remember { mutableStateOf<TvPlayerPanel?>(null) }
    var returnToTrackPanel by remember { mutableStateOf<TvPlayerPanel?>(null) }
    var trackSelectionError by remember { mutableStateOf(false) }
    var isPlaying by remember { mutableStateOf(player.player.isPlaying) }
    var playbackEnded by remember { mutableStateOf(false) }
    var focusedLearningWord by remember { mutableStateOf(true) }
    var positionMs by remember { mutableStateOf(player.player.currentPosition.coerceAtLeast(0)) }
    var durationMs by remember { mutableStateOf(player.player.duration.takeIf { it > 0 } ?: 0L) }
    var bufferedPositionMs by remember { mutableStateOf(player.player.bufferedPosition.coerceAtLeast(0L)) }
    var playbackSpeed by remember { mutableStateOf(player.player.playbackParameters.speed) }
    val skipSegment = TvPlayerInteraction.activeSkipSegment(skipSegments, positionMs)

    DisposableEffect(player) {
        val listener = object : Player.Listener {
            override fun onIsPlayingChanged(value: Boolean) {
                isPlaying = value
                positionMs = player.player.currentPosition.coerceAtLeast(0)
                durationMs = player.player.duration.takeIf { it > 0 } ?: durationMs
                currentOnPositionChanged(positionMs, durationMs, value)
            }

            override fun onPlaybackParametersChanged(playbackParameters: PlaybackParameters) {
                playbackSpeed = playbackParameters.speed
                currentOnPlaybackSpeedChanged()
            }

            override fun onPlaybackStateChanged(playbackState: Int) {
                positionMs = player.player.currentPosition.coerceAtLeast(0)
                durationMs = player.player.duration.takeIf { it > 0 } ?: durationMs
                bufferedPositionMs = player.player.bufferedPosition.coerceAtLeast(0L)
                if (playbackState == Player.STATE_ENDED) {
                    playbackEnded = true
                    uiState = uiState.copy(controlsVisible = true)
                    currentOnPlaybackEnded(positionMs, durationMs)
                } else if (playbackState == Player.STATE_READY) {
                    playbackEnded = false
                }
            }

            override fun onPositionDiscontinuity(
                oldPosition: Player.PositionInfo,
                newPosition: Player.PositionInfo,
                reason: Int,
            ) {
                positionMs = newPosition.positionMs.coerceAtLeast(0)
                durationMs = player.player.duration.takeIf { it > 0 } ?: durationMs
                currentOnSeeked(positionMs, durationMs, player.player.isPlaying)
            }

            override fun onPlayerError(error: PlaybackException) {
                currentOnPlaybackFailure(player.player.currentPosition.coerceAtLeast(0))
            }
        }
        player.player.addListener(listener)
        onDispose { player.player.removeListener(listener) }
    }

    LaunchedEffect(player, isPlaying) {
        while (isPlaying) {
            val current = player.player.currentPosition.coerceAtLeast(0)
            val duration = player.player.duration.takeIf { it > 0 } ?: durationMs
            positionMs = current
            durationMs = duration
            bufferedPositionMs = player.player.bufferedPosition.coerceAtLeast(0L)
            currentOnPositionChanged(current, duration, true)
            delay(500)
        }
    }

    fun apply(transition: TvPlayerTransition) {
        if (transition.state.learningLayer != TvLearningLayer.CLOSED &&
            transition.state.focusedWordIndex != uiState.focusedWordIndex
        ) {
            onSelectedTermChanged(currentCue?.tokens?.getOrNull(transition.state.focusedWordIndex)?.termId)
        }
        uiState = transition.state
        applyEffects(
            effects = transition.effects,
            player = player,
            onExit = onExit,
            cue = currentCue,
            focusedWordIndex = transition.state.focusedWordIndex,
            onOpenOnPhone = onOpenOnPhone,
        )
    }

    LaunchedEffect(uiState.controlsVisible, uiState.learningLayer, uiState.focusedWordIndex, companionVisible, trackPanel, playbackError, skipSegment?.kind) {
        if (companionVisible) return@LaunchedEffect
        val target = when {
            playbackError != null -> retryFocus
            trackPanel != null -> trackPanelFocus
            uiState.learningLayer != TvLearningLayer.CLOSED -> learningOverlayFocus
            uiState.controlsVisible && returnToTrackPanel == TvPlayerPanel.AUDIO -> audioTrackFocus
            uiState.controlsVisible && returnToTrackPanel == TvPlayerPanel.SUBTITLES -> subtitleTrackFocus
            uiState.controlsVisible && returnToTrackPanel == TvPlayerPanel.SPEED -> speedControlFocus
            uiState.controlsVisible && returnToTrackPanel == TvPlayerPanel.SETTINGS -> settingsFocus
            uiState.controlsVisible -> primaryControlFocus
            skipSegment != null -> skipFocus
            else -> playerFocus
        }
        runCatching { target.requestFocus() }
    }

    LaunchedEffect(
        uiState.controlsVisible,
        uiState.learningLayer,
        isPlaying,
        companionVisible,
        controlsInteractionRevision,
        trackPanel,
        playbackError,
    ) {
        if (uiState.controlsVisible &&
            uiState.learningLayer == TvLearningLayer.CLOSED &&
            !companionVisible &&
            trackPanel == null &&
            playbackError == null &&
            isPlaying
        ) {
            delay(design.controlsAutoHideMs)
            apply(TvPlayerInteraction.autoHide(uiState, isPlaying, companionVisible))
        }
    }

    LaunchedEffect(playbackEnded, nextEpisodeTitle, uiState.controlsVisible) {
        if (playbackEnded && nextEpisodeTitle != null && uiState.controlsVisible) {
            runCatching { nextEpisodeFocus.requestFocus() }
        }
    }

    LaunchedEffect(remoteCommand?.commandId) {
        val command = remoteCommand ?: return@LaunchedEffect
        when (command.type) {
            "playPause" -> apply(TvPlayerInteraction.mediaPlayPause(uiState))
            "seekBack10" -> apply(
                TvPlayerTransition(
                    uiState,
                    listOf(TvPlayerEffect.SeekBy(-design.seek.backMs)),
                ),
            )
            "seekForward10" -> apply(
                TvPlayerTransition(
                    uiState,
                    listOf(TvPlayerEffect.SeekBy(design.seek.forwardMs)),
                ),
            )
            "seekTo" -> command.payload["positionMs"]
                ?.toLongOrNull()
                ?.coerceAtLeast(0)
                ?.let(player.player::seekTo)

            "selectAudioTrack" -> command.payload["trackId"]
                ?.let(onSelectAudioTrack)

            "selectSubtitleTrack" -> onSelectSubtitleTrack(
                command.payload["trackId"]
                    ?.takeUnless { it.equals("off", ignoreCase = true) },
            )

            "repeatCurrentCue" -> currentCue?.let { cue ->
                player.player.seekTo(cue.startMs.toLong())
                player.player.play()
            }

            "learnCurrentCue" -> apply(
                TvPlayerInteraction.learnCurrentLine(
                    state = uiState,
                    isPlaying = player.player.isPlaying,
                    wordCount = currentCue?.tokens?.size ?: 0,
                ),
            )

            "openWord" -> {
                val termId = command.payload["termId"]
                val index = currentCue?.tokens
                    ?.indexOfFirst { it.termId == termId }
                    ?: -1
                if (index >= 0) {
                    if (player.player.isPlaying) {
                        player.player.pause()
                    }
                    uiState = uiState.copy(
                        controlsVisible = false,
                        learningLayer = TvLearningLayer.WORD,
                        focusedWordIndex = index,
                        resumeAfterLearning = false,
                    )
                    onSelectedTermChanged(termId)
                }
            }

            "markKnown" -> command.payload["termId"]
                ?.let { onSetTermState(it, "known") }

            "addToLearning" -> command.payload["termId"]
                ?.let { onSetTermState(it, "learning") }

            "openCompanion" -> {
                val termId = command.payload["termId"]
                onSelectedTermChanged(termId)
                onOpenOnPhone(currentCue?.id, termId)
            }

            "closeOverlay" -> {
                if (uiState.learningLayer != TvLearningLayer.CLOSED ||
                    uiState.controlsVisible
                ) {
                    apply(TvPlayerInteraction.back(uiState))
                }
            }

            "exitPlayer" -> onExit()
        }
    }

    BackHandler {
        if (companionVisible) {
            onCloseCompanion()
        } else if (playbackError != null) {
            onExit()
        } else if (trackPanel != null) {
            trackPanel = if (trackPanel == TvPlayerPanel.SPEED) TvPlayerPanel.SETTINGS else null
        } else {
            apply(TvPlayerInteraction.back(uiState))
        }
    }

    Surface(
        modifier = modifier
            .fillMaxSize()
            .onPreviewKeyEvent { event ->
                if (companionVisible || playbackError != null) {
                    return@onPreviewKeyEvent false
                }
                if (event.type != KeyEventType.KeyDown) {
                    return@onPreviewKeyEvent false
                }

                if (trackPanel != null && event.key != Key.MediaPlayPause &&
                    event.key != Key.MediaPlay && event.key != Key.MediaPause
                ) {
                    return@onPreviewKeyEvent false
                }

                if (uiState.controlsVisible &&
                    uiState.learningLayer == TvLearningLayer.CLOSED
                ) {
                    controlsInteractionRevision += 1
                }

                val transition = when (event.key) {
                    Key.MediaPlayPause -> TvPlayerInteraction.mediaPlayPause(uiState)
                    Key.MediaPlay -> TvPlayerInteraction.mediaPlay(uiState)
                    Key.MediaPause -> TvPlayerInteraction.mediaPause(uiState)

                    Key.DirectionCenter,
                    Key.Enter,
                    -> {
                        if (uiState.controlsVisible ||
                            uiState.learningLayer != TvLearningLayer.CLOSED ||
                            skipSegment != null
                        ) {
                            return@onPreviewKeyEvent false
                        }
                        TvPlayerInteraction.ok(
                            uiState,
                            currentCue?.tokens?.size ?: 0,
                        )
                    }

                    Key.DirectionLeft -> {
                        if ((uiState.controlsVisible &&
                                uiState.learningLayer == TvLearningLayer.CLOSED) ||
                            (uiState.learningLayer != TvLearningLayer.CLOSED &&
                                (!focusedLearningWord || currentCue?.tokens.isNullOrEmpty()))
                        ) {
                            return@onPreviewKeyEvent false
                        }
                        TvPlayerInteraction.left(
                            uiState,
                            currentCue?.tokens?.size ?: 0,
                            design.seek,
                        )
                    }

                    Key.DirectionRight -> {
                        if ((uiState.controlsVisible &&
                                uiState.learningLayer == TvLearningLayer.CLOSED) ||
                            (uiState.learningLayer != TvLearningLayer.CLOSED &&
                                (!focusedLearningWord || currentCue?.tokens.isNullOrEmpty()))
                        ) {
                            return@onPreviewKeyEvent false
                        }
                        TvPlayerInteraction.right(
                            uiState,
                            currentCue?.tokens?.size ?: 0,
                            design.seek,
                        )
                    }

                    Key.DirectionDown, Key.DirectionUp -> {
                        if (uiState.learningLayer != TvLearningLayer.CLOSED || uiState.controlsVisible) {
                            return@onPreviewKeyEvent false
                        }
                        TvPlayerTransition(uiState.copy(controlsVisible = true))
                    }

                    else -> return@onPreviewKeyEvent false
                }

                apply(transition)
                true
            }
            .focusRequester(playerFocus)
            .focusable(),
    ) {
        Box(modifier = Modifier.fillMaxSize()) {
            VideoSurface(player)

            currentCue?.let { cue ->
                if (uiState.learningLayer == TvLearningLayer.CLOSED) {
                    Text(
                        text = cue.text,
                        modifier = Modifier
                            .align(Alignment.BottomCenter)
                            .padding(bottom = if (uiState.controlsVisible) 340.dp else 48.dp)
                            .background(design.subtitleBackground)
                            .padding(horizontal = 20.dp, vertical = 10.dp),
                        color = design.subtitleText,
                        fontSize = design.subtitlePreferredSp.sp,
                        style = MaterialTheme.typography.headlineSmall,
                    )
                }
            }

            if (uiState.controlsVisible &&
                uiState.learningLayer == TvLearningLayer.CLOSED &&
                trackPanel == null &&
                playbackError == null
            ) {
                PlayerControls(
                    episodeTitle = episodeTitle,
                    isPlaying = isPlaying,
                    playbackEnded = playbackEnded,
                    skipSegment = skipSegment,
                    onSkipSegment = { segment -> player.player.seekTo(segment.endMs) },
                    previousEpisodeTitle = previousEpisodeTitle,
                    nextEpisodeTitle = nextEpisodeTitle,
                    onPreviousEpisode = onPreviousEpisode,
                    onNextEpisode = onNextEpisode,
                    nextEpisodeFocus = nextEpisodeFocus,
                    canLearn = currentCue != null,
                    positionMs = positionMs,
                    durationMs = durationMs,
                    bufferedPositionMs = bufferedPositionMs,
                    trickplay = trickplay,
                    serverOrigin = serverOrigin,
                    requestHeaders = requestHeaders,
                    audioTracks = audioTracks,
                    subtitleTracks = subtitleTracks,
                    selectedAudioTrackId = selectedAudioTrackId,
                    selectedSubtitleTrackId = selectedSubtitleTrackId,
                    playbackSpeed = playbackSpeed,
                    design = design,
                    primaryControlFocus = primaryControlFocus,
                    audioTrackFocus = audioTrackFocus,
                    subtitleTrackFocus = subtitleTrackFocus,
                    settingsFocus = settingsFocus,
                    onOpenSettingsPanel = {
                        returnToTrackPanel = TvPlayerPanel.SETTINGS
                        trackPanel = TvPlayerPanel.SETTINGS
                    },
                    onOpenAudioTracks = {
                        returnToTrackPanel = TvPlayerPanel.AUDIO
                        trackSelectionError = false
                        trackPanel = TvPlayerPanel.AUDIO
                    },
                    onOpenSubtitleTracks = {
                        returnToTrackPanel = TvPlayerPanel.SUBTITLES
                        trackSelectionError = false
                        trackPanel = TvPlayerPanel.SUBTITLES
                    },
                    onPlayPause = {
                        if (player.player.isPlaying) {
                            player.player.pause()
                        } else {
                            if (player.player.playbackState == Player.STATE_ENDED) player.player.seekTo(0)
                            player.player.play()
                        }
                    },
                    onSeekTo = { target ->
                        if (durationMs > 0) {
                            positionMs = target.coerceIn(0L, durationMs)
                            player.player.seekTo(positionMs)
                        }
                    },
                    onRepeatLine = {
                        currentCue?.let { cue ->
                            player.player.seekTo(cue.startMs.toLong())
                            player.player.play()
                        }
                    },
                    onLearn = {
                        apply(
                            TvPlayerInteraction.learnCurrentLine(
                                state = uiState,
                                isPlaying = player.player.isPlaying,
                                wordCount = currentCue?.tokens?.size ?: 0,
                            ),
                        )
                    },
                    onExit = onExit,
                    modifier = Modifier.fillMaxSize(),
                )
            }

            if (trackPanel == TvPlayerPanel.SETTINGS) {
                TvSettingsPanel(
                    focusRequester = trackPanelFocus,
                    playbackSpeed = playbackSpeed,
                    onOpenSpeed = { trackPanel = TvPlayerPanel.SPEED },
                    onBack = { trackPanel = null },
                )
            } else if (trackPanel == TvPlayerPanel.SPEED) {
                TvSpeedSelectionPanel(
                    playbackSpeed = playbackSpeed,
                    allowedSpeeds = playbackSpeeds,
                    focusRequester = trackPanelFocus,
                    onSelect = { speed ->
                        player.player.setPlaybackParameters(PlaybackParameters(speed))
                        playbackSpeed = speed
                        onPlaybackSpeedChanged()
                        trackPanel = TvPlayerPanel.SETTINGS
                    },
                    onBack = { trackPanel = TvPlayerPanel.SETTINGS },
                    modifier = Modifier.align(Alignment.Center),
                )
            } else if (trackPanel != null) {
                TvTrackSelectionPanel(
                    title = if (trackPanel == TvPlayerPanel.AUDIO) stringResource(R.string.tv_player_audio)
                        else stringResource(R.string.tv_player_subtitles),
                    tracks = if (trackPanel == TvPlayerPanel.AUDIO) audioTracks else subtitleTracks,
                    selectedId = if (trackPanel == TvPlayerPanel.AUDIO) {
                        selectedAudioTrackId
                    } else {
                        selectedSubtitleTrackId
                    },
                    allowOff = trackPanel == TvPlayerPanel.SUBTITLES,
                    selectionFailed = trackSelectionError,
                    focusRequester = trackPanelFocus,
                    onSelect = { id ->
                        val selected = if (trackPanel == TvPlayerPanel.AUDIO) {
                            id != null && onSelectAudioTrack(id)
                        } else {
                            onSelectSubtitleTrack(id)
                        }
                        if (selected) {
                            trackPanel = null
                            trackSelectionError = false
                        } else {
                            trackSelectionError = true
                        }
                    },
                    onBack = { trackPanel = null },
                    modifier = Modifier.align(Alignment.Center),
                )
            }

            if (uiState.learningLayer != TvLearningLayer.CLOSED && currentCue != null) {
                LearningOverlay(
                    cue = currentCue,
                    layer = uiState.learningLayer,
                    focusedWordIndex = uiState.focusedWordIndex,
                    onFocusWord = { index ->
                        uiState = uiState.copy(focusedWordIndex = index)
                        onSelectedTermChanged(
                            currentCue.tokens.getOrNull(index)?.termId,
                        )
                    },
                    onOpenWord = { index ->
                        uiState = uiState.copy(
                            learningLayer = TvLearningLayer.WORD,
                            focusedWordIndex = index,
                        )
                        onSelectedTermChanged(
                            currentCue.tokens.getOrNull(index)?.termId,
                        )
                    },
                    onRepeatLine = {
                        player.player.seekTo(currentCue.startMs.toLong())
                        player.player.play()
                    },
                    onMarkKnown = {
                        currentCue.tokens
                            .getOrNull(uiState.focusedWordIndex)
                            ?.termId
                            ?.let { onSetTermState(it, "known") }
                    },
                    onAddToLearning = {
                        currentCue.tokens
                            .getOrNull(uiState.focusedWordIndex)
                            ?.termId
                            ?.let { onSetTermState(it, "learning") }
                    },
                    onOpenOnPhone = if (canOpenOnPhone) {
                        {
                            val termId = currentCue.tokens
                                .getOrNull(uiState.focusedWordIndex)
                                ?.termId
                            onOpenOnPhone(currentCue.id, termId)
                        }
                    } else {
                        null
                    },
                    wordFocusRequester = learningOverlayFocus,
                    onWordFocusChanged = { focusedLearningWord = it },
                    modifier = Modifier.align(Alignment.Center),
                )
            }

            if (playbackError != null) {
                Column(
                    modifier = Modifier
                        .fillMaxSize()
                        .background(Color.Black.copy(alpha = 0.86f))
                        .padding(72.dp),
                    horizontalAlignment = Alignment.CenterHorizontally,
                    verticalArrangement = Arrangement.Center,
                ) {
                    Text(
                        text = "Playback failed",
                        color = Color.White,
                        style = MaterialTheme.typography.headlineMedium,
                    )
                    Spacer(Modifier.height(14.dp))
                    Text(
                        text = playbackError,
                        color = Color.White,
                        style = MaterialTheme.typography.bodyLarge,
                    )
                    Spacer(Modifier.height(24.dp))
                    Row(horizontalArrangement = Arrangement.spacedBy(18.dp)) {
                        Button(
                            onClick = onRetryPlayback,
                            modifier = Modifier.focusRequester(retryFocus),
                        ) { Text("Retry") }
                        Button(onClick = onExit) { Text("Back") }
                    }
                }
            }

            if (skipSegment != null && !uiState.controlsVisible &&
                uiState.learningLayer == TvLearningLayer.CLOSED &&
                trackPanel == null && playbackError == null && !companionVisible
            ) {
                Button(
                    onClick = { player.player.seekTo(skipSegment.endMs) },
                    modifier = Modifier
                        .align(Alignment.BottomEnd)
                        .padding(end = 52.dp, bottom = 48.dp)
                        .focusRequester(skipFocus),
                ) {
                    Text(stringResource(skipLabelRes(skipSegment.kind)))
                }
            }

            companionOverlay?.let { overlay ->
                Box(modifier = Modifier.align(Alignment.Center)) {
                    overlay()
                }
            }
        }
    }
}

@OptIn(UnstableApi::class)
@Composable
private fun VideoSurface(player: JularrMedia3Player) {
    val context = LocalContext.current
    val surfaceView = remember { SurfaceView(context) }

    DisposableEffect(player, surfaceView) {
        player.player.setVideoSurfaceView(surfaceView)
        onDispose { player.player.clearVideoSurfaceView(surfaceView) }
    }

    AndroidView(
        factory = { surfaceView },
        modifier = Modifier.fillMaxSize(),
    )
}

@Composable
private fun LearningOverlay(
    cue: SubtitleCue,
    layer: TvLearningLayer,
    focusedWordIndex: Int,
    wordFocusRequester: FocusRequester,
    onWordFocusChanged: (Boolean) -> Unit,
    onFocusWord: (Int) -> Unit,
    onOpenWord: (Int) -> Unit,
    onRepeatLine: () -> Unit,
    onMarkKnown: () -> Unit,
    onAddToLearning: () -> Unit,
    onOpenOnPhone: (() -> Unit)?,
    modifier: Modifier = Modifier,
) {
    val focused = cue.tokens.getOrNull(focusedWordIndex)

    Column(
        modifier = modifier
            .fillMaxWidth(0.86f)
            .background(MaterialTheme.colorScheme.surface.copy(alpha = 0.96f))
            .padding(32.dp),
        verticalArrangement = Arrangement.spacedBy(18.dp),
    ) {
        Text(
            text = cue.text,
            style = MaterialTheme.typography.headlineMedium,
        )

        LazyRow(horizontalArrangement = Arrangement.spacedBy(10.dp)) {
            itemsIndexed(cue.tokens) { index, token ->
                Button(
                    modifier = (if (index == focusedWordIndex) {
                        Modifier.focusRequester(wordFocusRequester)
                    } else {
                        Modifier
                    }).onFocusChanged { if (it.isFocused) onWordFocusChanged(true) },
                    onClick = {
                        onFocusWord(index)
                        onOpenWord(index)
                    },
                ) {
                    Text(
                        if (index == focusedWordIndex) "› ${token.surface} ‹" else token.surface,
                    )
                }
            }
        }

        if (layer == TvLearningLayer.WORD && focused != null) {
            Spacer(Modifier.height(4.dp))
            Text(
                text = focused.canonical ?: focused.surface,
                style = MaterialTheme.typography.titleLarge,
            )
            focused.reading?.let { Text(it, style = MaterialTheme.typography.bodyLarge) }
            focused.meaning?.let { Text(it, style = MaterialTheme.typography.bodyLarge) }
            Text(
                text = "Learning: ${focused.state}",
                style = MaterialTheme.typography.bodyMedium,
            )
        }

        Row(horizontalArrangement = Arrangement.spacedBy(10.dp)) {
            Button(
                modifier = (if (cue.tokens.isEmpty()) Modifier.focusRequester(wordFocusRequester) else Modifier)
                    .onFocusChanged { if (it.isFocused) onWordFocusChanged(false) },
                onClick = onRepeatLine,
            ) {
                Text("Repeat line")
            }
            if (layer == TvLearningLayer.WORD && focused?.termId != null) {
                Button(
                    modifier = Modifier.onFocusChanged { if (it.isFocused) onWordFocusChanged(false) },
                    onClick = onMarkKnown,
                ) {
                    Text("Known")
                }
                Button(
                    modifier = Modifier.onFocusChanged { if (it.isFocused) onWordFocusChanged(false) },
                    onClick = onAddToLearning,
                ) {
                    Text("Learning")
                }
            }
            if (onOpenOnPhone != null) {
                Button(
                    modifier = Modifier.onFocusChanged { if (it.isFocused) onWordFocusChanged(false) },
                    onClick = onOpenOnPhone,
                ) {
                    Text("Open on phone")
                }
            }
        }
    }
}

@OptIn(UnstableApi::class)
private fun applyEffects(
    effects: List<TvPlayerEffect>,
    player: JularrMedia3Player,
    onExit: () -> Unit,
    cue: SubtitleCue?,
    focusedWordIndex: Int,
    onOpenOnPhone: (cueId: Long?, termId: String?) -> Unit,
) {
    for (effect in effects) {
        when (effect) {
            TvPlayerEffect.TogglePlayback -> if (player.player.isPlaying) player.player.pause() else {
                if (player.player.playbackState == Player.STATE_ENDED) player.player.seekTo(0)
                player.player.play()
            }
            TvPlayerEffect.PlayPlayback -> {
                if (player.player.playbackState == Player.STATE_ENDED) player.player.seekTo(0)
                player.player.play()
            }
            TvPlayerEffect.PauseMediaPlayback -> player.player.pause()
            TvPlayerEffect.PausePlayback -> player.player.pause()
            TvPlayerEffect.ResumePlayback -> player.player.play()
            is TvPlayerEffect.SeekBy -> {
                val target = (player.player.currentPosition + effect.deltaMs).coerceAtLeast(0)
                val duration = player.player.duration
                player.player.seekTo(if (duration > 0) target.coerceAtMost(duration) else target)
            }
            TvPlayerEffect.ExitPlayer -> onExit()
            TvPlayerEffect.OpenOnPhone -> {
                val termId = cue?.tokens?.getOrNull(focusedWordIndex)?.termId
                onOpenOnPhone(cue?.id, termId)
            }
        }
    }
}
