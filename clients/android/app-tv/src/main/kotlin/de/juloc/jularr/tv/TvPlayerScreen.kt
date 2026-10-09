package de.juloc.jularr.tv

import android.view.SurfaceView
import androidx.activity.compose.BackHandler
import androidx.annotation.OptIn
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.focusable
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
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.offset
import androidx.compose.foundation.layout.requiredWidth
import androidx.compose.foundation.layout.requiredHeight
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.verticalScroll
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.lazy.LazyRow
import androidx.compose.foundation.lazy.itemsIndexed
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.Icon
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material.icons.filled.Forward30
import androidx.compose.material.icons.filled.Pause
import androidx.compose.material.icons.filled.PlayArrow
import androidx.compose.material.icons.filled.Replay
import androidx.compose.material.icons.filled.Replay10
import androidx.compose.material.icons.filled.SkipPrevious
import androidx.compose.material.icons.filled.SkipNext
import androidx.compose.material.icons.filled.Subtitles
import androidx.compose.material.icons.filled.VolumeUp
import androidx.compose.material.icons.filled.Check
import androidx.compose.material.icons.filled.Settings
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.focus.FocusRequester
import androidx.compose.ui.focus.onFocusChanged
import androidx.compose.ui.focus.focusRequester
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.Brush
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
                onPositionChanged(positionMs, durationMs, value)
            }

            override fun onPlaybackParametersChanged(playbackParameters: PlaybackParameters) {
                playbackSpeed = playbackParameters.speed
                onPlaybackSpeedChanged()
            }

            override fun onPlaybackStateChanged(playbackState: Int) {
                positionMs = player.player.currentPosition.coerceAtLeast(0)
                durationMs = player.player.duration.takeIf { it > 0 } ?: durationMs
                bufferedPositionMs = player.player.bufferedPosition.coerceAtLeast(0L)
                if (playbackState == Player.STATE_ENDED) {
                    playbackEnded = true
                    uiState = uiState.copy(controlsVisible = true)
                    onPlaybackEnded(positionMs, durationMs)
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
                onSeeked(positionMs, durationMs, player.player.isPlaying)
            }

            override fun onPlayerError(error: PlaybackException) {
                onPlaybackFailure(player.player.currentPosition.coerceAtLeast(0))
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
            onPositionChanged(current, duration, true)
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
                    title = if (trackPanel == TvPlayerPanel.AUDIO) "Audio" else "Subtitles",
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
private fun PlayerControls(
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
    design: TvPlayerDesign,
    primaryControlFocus: FocusRequester,
    audioTrackFocus: FocusRequester,
    subtitleTrackFocus: FocusRequester,
    settingsFocus: FocusRequester,
    onOpenSettingsPanel: () -> Unit,
    onOpenAudioTracks: () -> Unit,
    onOpenSubtitleTracks: () -> Unit,
    onPlayPause: () -> Unit,
    onSeekTo: (Long) -> Unit,
    onRepeatLine: () -> Unit,
    onLearn: () -> Unit,
    onExit: () -> Unit,
    modifier: Modifier = Modifier,
) {
    val focusColor = rememberTvFocusColor()
    var scrubPreviewMs by remember(durationMs) { mutableStateOf<Long?>(null) }
    androidx.activity.compose.BackHandler(enabled = scrubPreviewMs != null) { scrubPreviewMs = null }
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
                    contentDescription = "Back",
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
                    text = "Seek to ${formatTime(scrubPreviewMs ?: positionMs)} · OK to confirm · Back to cancel",
                    color = design.subtitleText,
                    style = MaterialTheme.typography.bodyLarge,
                )
            }
            if (playbackEnded) {
                Text(
                    text = if (nextEpisodeTitle == null) "Episode finished" else "Episode finished · Next: $nextEpisodeTitle",
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
                            contentDescription = "Audio",
                            modifier = Modifier.size(22.dp),
                        )
                        Text(
                            "  ${trackLabel(audioTracks, selectedAudioTrackId, "Default")}",
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
                            contentDescription = "Subtitles",
                            modifier = Modifier.size(22.dp),
                        )
                        Text(
                            "  ${trackLabel(subtitleTracks, selectedSubtitleTrackId, "Off")}",
                            maxLines = 1,
                        )
                    }
                }
                Button(
                    onClick = onOpenSettingsPanel,
                    modifier = Modifier.focusRequester(settingsFocus),
                ) {
                    Icon(Icons.Filled.Settings, contentDescription = "Settings")
                    Text(" Mehr")
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

private fun skipLabelRes(kind: String): Int =
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
    if (descriptor.spriteUrls.isEmpty()) return

    val sampleCount = minOf(count, 8)
    val samples = remember(count, sampleCount) {
        (0 until sampleCount).map { index ->
            if (sampleCount == 1) 0 else index * (count - 1) / (sampleCount - 1)
        }
    }
    var selectedSample by remember { mutableStateOf<Int?>(null) }
    Column(verticalArrangement = Arrangement.spacedBy(6.dp)) {
        Text(
            if (selectedSample == null) "Szenen · nach unten für Zeitpunkte"
            else "Springen zu ${formatTime(selectedSample!!.toLong() * intervalMs)}",
            color = Color.White.copy(alpha = 0.8f),
            style = MaterialTheme.typography.labelMedium,
        )
        LazyRow(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            items(samples) { sample ->
                val sheetSize = columns * rows
                val spriteIndex = sample / sheetSize
                val spriteUrl = descriptor.spriteUrls.getOrNull(spriteIndex)
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
                    if (spriteUrl != null) {
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
}

@Composable
private fun TvSettingsPanel(
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
            Text("Einstellungen", style = MaterialTheme.typography.headlineSmall)
            Button(
                onClick = onOpenSpeed,
                modifier = Modifier.fillMaxWidth().focusRequester(focusRequester),
            ) {
                Text("Wiedergabetempo · ${playbackSpeed}x")
            }
            Button(onClick = onBack) { Text("Schließen") }
        }
    }
}

@Composable
private fun TvSpeedSelectionPanel(
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
            Text("Playback speed", style = MaterialTheme.typography.headlineMedium)
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
private fun TvTrackSelectionPanel(
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
                Text("Track unavailable on this device", color = MaterialTheme.colorScheme.error)
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
                        Text("Off")
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
