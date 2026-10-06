package de.juloc.jularr.mobile

import androidx.annotation.OptIn
import android.Manifest
import android.content.Context
import android.content.pm.PackageManager
import android.os.Build
import androidx.activity.ComponentActivity
import androidx.activity.compose.BackHandler
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.core.content.ContextCompat
import androidx.core.view.WindowCompat
import androidx.core.view.WindowInsetsCompat
import androidx.core.view.WindowInsetsControllerCompat
import de.juloc.jularr.mobile.offline.DownloadState
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.gestures.detectTapGestures
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.safeDrawing
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.layout.windowInsetsPadding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Close
import androidx.compose.material.icons.filled.Forward30
import androidx.compose.material.icons.filled.Pause
import androidx.compose.material.icons.filled.PlayArrow
import androidx.compose.material.icons.filled.Replay
import androidx.compose.material.icons.filled.Replay10
import androidx.compose.material.icons.filled.Settings
import androidx.compose.material.icons.filled.Subtitles
import androidx.compose.material3.Button
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Slider
import androidx.compose.material3.SliderDefaults
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableFloatStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.runtime.collectAsState
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.viewinterop.AndroidView
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.LifecycleEventObserver
import androidx.media3.common.util.UnstableApi
import androidx.media3.ui.AspectRatioFrameLayout
import androidx.media3.ui.PlayerView
import de.juloc.jularr.core.api.HttpJularrClientApi
import de.juloc.jularr.core.model.MediaAvailability
import de.juloc.jularr.core.model.MediaTrack
import de.juloc.jularr.core.model.SubtitleCue
import de.juloc.jularr.core.player.PlaybackTransport
import kotlinx.coroutines.launch

@OptIn(UnstableApi::class)
@Composable
fun NativePlayerScreen(
    episodeId: String,
    origin: ServerOrigin,
    api: HttpJularrClientApi,
    sessionHeaders: () -> Map<String, String>,
    onOpenDownloads: () -> Unit,
    onOpenTtsSettings: () -> Unit,
    onClose: () -> Unit,
) {
    val context = LocalContext.current
    val activity = context as? ComponentActivity
    val scope = rememberCoroutineScope()
    val design = remember { MobilePlayerDesignLoader.load(context) }
    val controller = remember(episodeId, origin.value) {
        NativePlayerController(
            context = context,
            episodeId = episodeId,
            origin = origin,
            api = api,
            sessionHeaders = sessionHeaders,
        )
    }
    val ui by controller.state.collectAsState()

    // The learning subtitle/vocabulary in the native player is always Japanese (see
    // README); the TTS coordinator is scoped to this screen and closed with it.
    val tts = remember(origin.value) { TtsCoordinator(context, api) }
    val ttsState by tts.state.collectAsState()
    DisposableEffect(tts) {
        onDispose { tts.close() }
    }

    var controlsVisible by remember { mutableStateOf(true) }
    var audioMenuOpen by remember { mutableStateOf(false) }
    var subtitleMenuOpen by remember { mutableStateOf(false) }
    var settingsOpen by remember { mutableStateOf(false) }
    var playbackSpeed by remember(episodeId) { mutableFloatStateOf(1f) }
    var learningState by remember { mutableStateOf<LearningPlaybackState?>(null) }
    var scrubValue by remember { mutableFloatStateOf(-1f) }

    LaunchedEffect(controller) {
        controller.start()
    }

    // The download notification needs POST_NOTIFICATIONS on Android 13+; the download
    // itself starts either way.
    val notificationPermission = rememberLauncherForActivityResult(
        ActivityResultContracts.RequestPermission(),
    ) {
        controller.requestDownload()
    }

    fun startDownload() {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU &&
            ContextCompat.checkSelfPermission(context, Manifest.permission.POST_NOTIFICATIONS) !=
            PackageManager.PERMISSION_GRANTED
        ) {
            notificationPermission.launch(Manifest.permission.POST_NOTIFICATIONS)
        } else {
            controller.requestDownload()
        }
    }

    LaunchedEffect(ui.notice) {
        if (ui.notice != null) {
            kotlinx.coroutines.delay(5_000)
            controller.clearNotice()
        }
    }

    LaunchedEffect(controlsVisible, ui.isPlaying, learningState, settingsOpen) {
        if (controlsVisible && ui.isPlaying && learningState == null && !settingsOpen) {
            kotlinx.coroutines.delay(design.controlsAutoHideMs)
            controlsVisible = false
        }
    }

    DisposableEffect(controller) {
        onDispose {
            controller.close()
        }
    }

    DisposableEffect(activity, controller) {
        if (activity == null) {
            onDispose { }
        } else {
            val observer = LifecycleEventObserver { _, event ->
                if (event == Lifecycle.Event.ON_STOP) {
                    controller.onBackgrounded()
                }
            }
            activity.lifecycle.addObserver(observer)
            onDispose {
                activity.lifecycle.removeObserver(observer)
            }
        }
    }

    DisposableEffect(activity) {
        if (activity == null) {
            onDispose { }
        } else {
            val systemBars = WindowCompat.getInsetsController(
                activity.window,
                activity.window.decorView,
            )
            systemBars.systemBarsBehavior =
                WindowInsetsControllerCompat.BEHAVIOR_SHOW_TRANSIENT_BARS_BY_SWIPE
            systemBars.hide(WindowInsetsCompat.Type.systemBars())
            onDispose {
                systemBars.show(WindowInsetsCompat.Type.systemBars())
            }
        }
    }

    fun closeLearningSheet() {
        val previous = learningState
        tts.stop()
        controller.clearSelectedTerm()
        learningState = null
        if (previous != null &&
            LearningSheetPolicy.shouldResumeOnClose(previous) &&
            !controller.player.isPlaying
        ) {
            controller.resumeAfterLearning()
        }
    }

    BackHandler {
        when {
            ui.selectedTerm != null -> controller.clearSelectedTerm()
            learningState != null -> closeLearningSheet()
            settingsOpen -> settingsOpen = false
            else -> scope.launch {
                controller.persistBeforeClose()
                onClose()
            }
        }
    }

    Surface(
        modifier = Modifier.fillMaxSize(),
        color = Color.Black,
    ) {
        Box(modifier = Modifier.fillMaxSize()) {
            AndroidView(
                factory = { viewContext: Context ->
                    PlayerView(viewContext).apply {
                        player = controller.player
                        useController = false
                        resizeMode = AspectRatioFrameLayout.RESIZE_MODE_FIT
                        setShutterBackgroundColor(android.graphics.Color.BLACK)
                        keepScreenOn = true
                    }
                },
                update = { it.player = controller.player },
                modifier = Modifier.fillMaxSize(),
            )

            Box(
                modifier = Modifier
                    .fillMaxSize()
                    .pointerInput(controller) {
                        detectTapGestures(
                            onTap = {
                                controlsVisible = !controlsVisible
                            },
                            onDoubleTap = { offset ->
                                controlsVisible = true
                                if (offset.x < size.width / 2f) {
                                    controller.seekBy(-design.seek.backMs)
                                } else {
                                    controller.seekBy(design.seek.forwardMs)
                                }
                            },
                        )
                    },
            )

            ui.currentCue?.let { cue ->
                Surface(
                    color = design.subtitleBackground,
                    shape = RoundedCornerShape(design.controlRadiusDp.dp),
                    modifier = Modifier
                        .align(Alignment.BottomCenter)
                        .padding(
                            start = 16.dp,
                            end = 16.dp,
                            bottom = if (controlsVisible) 156.dp else 56.dp,
                        )
                        .clickable {
                            learningState = LearningSheetPolicy.open(ui.isPlaying)
                            if (ui.isPlaying) {
                                controller.pauseForLearning()
                            }
                            controlsVisible = true
                        },
                ) {
                    Text(
                        text = cue.text,
                        color = design.subtitleText,
                        fontSize = design.subtitlePreferredSp.sp,
                        modifier = Modifier.padding(horizontal = 12.dp, vertical = 8.dp),
                    )
                }
            }

            if (controlsVisible || !ui.isPlaying || learningState != null) {
                PlayerControls(
                    ui = ui,
                    design = design,
                    scrubValue = scrubValue,
                    onScrubValue = { scrubValue = it },
                    onScrubFinished = {
                        if (scrubValue >= 0f) {
                            controller.seekTo(scrubValue.toLong())
                        }
                        scrubValue = -1f
                    },
                    onClose = {
                        scope.launch {
                            controller.persistBeforeClose()
                            onClose()
                        }
                    },
                    onPlayPause = controller::togglePlayPause,
                    onSeekBack = { controller.seekBy(-design.seek.backMs) },
                    onSeekForward = { controller.seekBy(design.seek.forwardMs) },
                    onRepeatCurrentCue = controller::repeatCurrentCue,
                    audioMenuOpen = audioMenuOpen,
                    onAudioMenuOpen = { audioMenuOpen = it },
                    subtitleMenuOpen = subtitleMenuOpen,
                    onSubtitleMenuOpen = { subtitleMenuOpen = it },
                    settingsOpen = settingsOpen,
                    onSettingsOpen = {
                        settingsOpen = it
                        if (it) controlsVisible = true
                    },
                    playbackSpeed = playbackSpeed,
                    onPlaybackSpeed = { speed ->
                        playbackSpeed = speed
                        controller.player.setPlaybackSpeed(speed)
                    },
                    onAudioTrack = controller::selectAudioTrack,
                    onSubtitleTrack = controller::selectSubtitleTrack,
                    onDownload = { startDownload() },
                    onOpenDownloads = {
                        scope.launch {
                            controller.persistBeforeClose()
                            onOpenDownloads()
                        }
                    },
                )
            }

            ui.notice?.let { notice ->
                Surface(
                    color = design.sheet,
                    shape = RoundedCornerShape(design.controlRadiusDp.dp),
                    modifier = Modifier
                        .align(Alignment.TopCenter)
                        .padding(top = 72.dp, start = 16.dp, end = 16.dp),
                ) {
                    Text(
                        text = notice,
                        color = design.subtitleText,
                        style = MaterialTheme.typography.bodyMedium,
                        modifier = Modifier.padding(horizontal = 12.dp, vertical = 8.dp),
                    )
                }
            }

            ui.storage?.let { storage ->
                StorageRecoveryOverlay(
                    modifier = Modifier.align(Alignment.Center),
                    availability = storage,
                    exhausted = ui.storageRetryExhausted,
                    wakeInProgress = ui.wakeInProgress,
                    design = design,
                    onRetry = controller::retryStorage,
                    onWake = controller::wakeStorage,
                )
            }

            if (ui.loading && ui.storage == null) {
                StatusOverlay(
                    modifier = Modifier.align(Alignment.Center),
                    title = "Starting player",
                    detail = "Loading Jularr playback state…",
                    design = design,
                    showProgress = true,
                )
            }

            ui.updateRequired?.let {
                StatusOverlay(
                    modifier = Modifier.align(Alignment.Center),
                    title = "Update required",
                    detail = it,
                    design = design,
                    showProgress = false,
                    actionLabel = "Back",
                    onAction = onClose,
                )
            }

            ui.error?.let {
                StatusOverlay(
                    modifier = Modifier.align(Alignment.Center),
                    title = "Playback unavailable",
                    detail = it,
                    design = design,
                    showProgress = false,
                    actionLabel = "Try again",
                    onAction = controller::retryPlayback,
                    secondaryLabel = "Back",
                    onSecondary = onClose,
                )
            }

            if (learningState != null) {
                LearningSheet(
                    modifier = Modifier.align(Alignment.BottomCenter),
                    cue = ui.currentCue,
                    selectedTerm = ui.selectedTerm,
                    termLoading = ui.termLoading,
                    design = design,
                    ttsSpeaking = ttsState.isSpeaking,
                    ttsSpeakingKey = ttsState.speakingKey,
                    onToken = controller::loadTerm,
                    onRepeat = controller::repeatCurrentCue,
                    onKnown = { controller.setSelectedTermState("known") },
                    onLearning = { controller.setSelectedTermState("learning") },
                    onBackFromTerm = controller::clearSelectedTerm,
                    onClose = ::closeLearningSheet,
                    onSpeakLine = { text -> tts.speak("line", text, "ja-JP") },
                    onSpeakWord = { text -> tts.speak("word", text, "ja-JP") },
                    onStopSpeaking = tts::stop,
                    onOpenVoiceSettings = onOpenTtsSettings,
                )
            }
        }
    }
}

@Composable
private fun PlayerControls(
    ui: NativePlayerUiState,
    design: MobilePlayerDesign,
    scrubValue: Float,
    onScrubValue: (Float) -> Unit,
    onScrubFinished: () -> Unit,
    onClose: () -> Unit,
    onPlayPause: () -> Unit,
    onSeekBack: () -> Unit,
    onSeekForward: () -> Unit,
    onRepeatCurrentCue: () -> Unit,
    audioMenuOpen: Boolean,
    onAudioMenuOpen: (Boolean) -> Unit,
    subtitleMenuOpen: Boolean,
    onSubtitleMenuOpen: (Boolean) -> Unit,
    settingsOpen: Boolean,
    onSettingsOpen: (Boolean) -> Unit,
    playbackSpeed: Float,
    onPlaybackSpeed: (Float) -> Unit,
    onAudioTrack: (MediaTrack) -> Unit,
    onSubtitleTrack: (MediaTrack?) -> Unit,
    onDownload: () -> Unit,
    onOpenDownloads: () -> Unit,
) {
    val bootstrap = ui.bootstrap
    val duration = ui.durationMs.coerceAtLeast(1)
    val sliderPosition = if (scrubValue >= 0f) {
        scrubValue
    } else {
        ui.positionMs.coerceIn(0, duration).toFloat()
    }

    Box(
        modifier = Modifier
            .fillMaxSize()
            .background(design.overlay)
            .windowInsetsPadding(WindowInsets.safeDrawing),
    ) {
        Row(
            modifier = Modifier
                .align(Alignment.TopCenter)
                .fillMaxWidth()
                .padding(
                    horizontal = design.spacingLargeDp.dp,
                    vertical = design.spacingMediumDp.dp,
                ),
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.spacedBy(design.spacingSmallDp.dp),
        ) {
            PlayerIconButton(
                icon = Icons.Filled.Close,
                label = "Close",
                design = design,
                onClick = onClose,
            )
            Column(modifier = Modifier.weight(1f)) {
                Text(
                    text = bootstrap?.episode?.animeTitle ?: "Jularr",
                    color = design.subtitleText,
                    style = MaterialTheme.typography.titleMedium,
                    maxLines = 1,
                )
                Text(
                    text = bootstrap?.episode?.let {
                        "S%02d E%02d · %s".format(
                            it.seasonNumber,
                            it.number,
                            it.title,
                        )
                    } ?: "Episode",
                    color = design.muted,
                    style = MaterialTheme.typography.bodySmall,
                    maxLines = 1,
                )
            }

            PlayerIconButton(
                icon = Icons.Filled.Subtitles,
                label = "Subtitles",
                design = design,
                selected = ui.selectedSubtitleTrackId != null,
                onClick = {
                    onSettingsOpen(true)
                    onSubtitleMenuOpen(true)
                },
            )

            PlayerIconButton(
                icon = Icons.Filled.Settings,
                label = "Settings",
                design = design,
                selected = settingsOpen,
                onClick = { onSettingsOpen(!settingsOpen) },
            )
        }

        Row(
            modifier = Modifier.align(Alignment.Center),
            horizontalArrangement = Arrangement.spacedBy(24.dp),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            PlayerIconButton(
                icon = Icons.Filled.Replay10,
                label = "Back ${design.seek.backSeconds} seconds",
                design = design,
                large = true,
                onClick = onSeekBack,
            )
            PlayerIconButton(
                icon = if (ui.isPlaying) Icons.Filled.Pause else Icons.Filled.PlayArrow,
                label = if (ui.isPlaying) "Pause" else "Play",
                design = design,
                prominent = true,
                onClick = onPlayPause,
            )
            PlayerIconButton(
                icon = Icons.Filled.Forward30,
                label = "Forward ${design.seek.forwardSeconds} seconds",
                design = design,
                large = true,
                onClick = onSeekForward,
            )
        }

        Column(
            modifier = Modifier
                .align(Alignment.BottomCenter)
                .fillMaxWidth()
                .padding(
                    start = design.spacingLargeDp.dp,
                    end = design.spacingLargeDp.dp,
                    bottom = design.spacingLargeDp.dp,
                ),
            verticalArrangement = Arrangement.spacedBy(2.dp),
        ) {
            Slider(
                value = sliderPosition,
                onValueChange = onScrubValue,
                onValueChangeFinished = onScrubFinished,
                valueRange = 0f..duration.toFloat(),
                colors = SliderDefaults.colors(
                    thumbColor = design.accent,
                    activeTrackColor = design.accent,
                    inactiveTrackColor = design.muted.copy(alpha = 0.45f),
                ),
            )
            Row(
                modifier = Modifier.fillMaxWidth(),
                verticalAlignment = Alignment.CenterVertically,
            ) {
                Text(
                    text = "${formatTime(ui.positionMs)} / ${formatTime(ui.durationMs)}",
                    color = design.subtitleText,
                    style = MaterialTheme.typography.bodySmall,
                    modifier = Modifier.weight(1f),
                )
                if (ui.currentCue != null) {
                    PlayerIconButton(
                        icon = Icons.Filled.Replay,
                        label = "Repeat line",
                        design = design,
                        onClick = onRepeatCurrentCue,
                    )
                }
            }
        }

        if (settingsOpen) {
            PlayerSettingsPanel(
                ui = ui,
                design = design,
                playbackSpeed = playbackSpeed,
                onPlaybackSpeed = onPlaybackSpeed,
                audioMenuOpen = audioMenuOpen,
                onAudioMenuOpen = onAudioMenuOpen,
                subtitleMenuOpen = subtitleMenuOpen,
                onSubtitleMenuOpen = onSubtitleMenuOpen,
                onAudioTrack = onAudioTrack,
                onSubtitleTrack = onSubtitleTrack,
                onDownload = onDownload,
                onOpenDownloads = onOpenDownloads,
                onClose = { onSettingsOpen(false) },
                modifier = Modifier.align(Alignment.CenterEnd),
            )
        }
    }
}

@Composable
private fun PlayerSettingsPanel(
    ui: NativePlayerUiState,
    design: MobilePlayerDesign,
    playbackSpeed: Float,
    onPlaybackSpeed: (Float) -> Unit,
    audioMenuOpen: Boolean,
    onAudioMenuOpen: (Boolean) -> Unit,
    subtitleMenuOpen: Boolean,
    onSubtitleMenuOpen: (Boolean) -> Unit,
    onAudioTrack: (MediaTrack) -> Unit,
    onSubtitleTrack: (MediaTrack?) -> Unit,
    onDownload: () -> Unit,
    onOpenDownloads: () -> Unit,
    onClose: () -> Unit,
    modifier: Modifier = Modifier,
) {
    val bootstrap = ui.bootstrap
    var speedMenuOpen by remember { mutableStateOf(false) }
    val speeds = remember { listOf(0.5f, 0.75f, 1f, 1.25f, 1.5f, 1.75f, 2f) }

    Surface(
        color = design.sheet,
        shape = RoundedCornerShape(design.sheetRadiusDp.dp),
        shadowElevation = 12.dp,
        modifier = modifier
            .padding(end = design.spacingLargeDp.dp)
            .fillMaxWidth(0.82f)
            .widthIn(max = 380.dp),
    ) {
        Column(
            modifier = Modifier
                .padding(design.spacingLargeDp.dp)
                .verticalScroll(rememberScrollState()),
            verticalArrangement = Arrangement.spacedBy(design.spacingMediumDp.dp),
        ) {
            Row(
                modifier = Modifier.fillMaxWidth(),
                verticalAlignment = Alignment.CenterVertically,
            ) {
                Text(
                    text = "Player settings",
                    color = design.subtitleText,
                    style = MaterialTheme.typography.titleMedium,
                    modifier = Modifier.weight(1f),
                )
                IconButton(onClick = onClose) {
                    Icon(
                        imageVector = Icons.Filled.Close,
                        contentDescription = "Close settings",
                        tint = design.subtitleText,
                    )
                }
            }

            PlayerSettingRow(label = "Speed", design = design) {
                Box {
                    TextButton(onClick = { speedMenuOpen = true }) {
                        Text(
                            text = formatSpeed(playbackSpeed),
                            color = design.subtitleText,
                        )
                    }
                    DropdownMenu(
                        expanded = speedMenuOpen,
                        onDismissRequest = { speedMenuOpen = false },
                    ) {
                        speeds.forEach { speed ->
                            DropdownMenuItem(
                                text = {
                                    Text(
                                        if (speed == playbackSpeed) {
                                            "✓ ${formatSpeed(speed)}"
                                        } else {
                                            formatSpeed(speed)
                                        },
                                    )
                                },
                                onClick = {
                                    speedMenuOpen = false
                                    onPlaybackSpeed(speed)
                                },
                            )
                        }
                    }
                }
            }

            if (bootstrap?.audioTracks?.isNotEmpty() == true) {
                PlayerSettingRow(label = "Audio", design = design) {
                    Box {
                        TextButton(onClick = { onAudioMenuOpen(true) }) {
                            Text(
                                text = selectedTrackLabel(
                                    bootstrap.audioTracks,
                                    ui.selectedAudioTrackId,
                                    "Default",
                                ),
                                color = design.subtitleText,
                                maxLines = 1,
                            )
                        }
                        DropdownMenu(
                            expanded = audioMenuOpen,
                            onDismissRequest = { onAudioMenuOpen(false) },
                        ) {
                            bootstrap.audioTracks.forEach { track ->
                                DropdownMenuItem(
                                    text = {
                                        Text(
                                            trackLabel(
                                                track,
                                                selected = track.id == ui.selectedAudioTrackId,
                                            ),
                                        )
                                    },
                                    onClick = {
                                        onAudioTrack(track)
                                        onAudioMenuOpen(false)
                                    },
                                )
                            }
                        }
                    }
                }
            }

            PlayerSettingRow(label = "Subtitles", design = design) {
                Box {
                    TextButton(onClick = { onSubtitleMenuOpen(true) }) {
                        Text(
                            text = selectedTrackLabel(
                                bootstrap?.subtitleTracks.orEmpty(),
                                ui.selectedSubtitleTrackId,
                                "Off",
                            ),
                            color = design.subtitleText,
                            maxLines = 1,
                        )
                    }
                    SubtitleMenu(
                        expanded = subtitleMenuOpen,
                        tracks = bootstrap?.subtitleTracks.orEmpty(),
                        selectedTrackId = ui.selectedSubtitleTrackId,
                        onDismiss = { onSubtitleMenuOpen(false) },
                        onSelect = onSubtitleTrack,
                    )
                }
            }

            PlayerSettingRow(label = "Playback", design = design) {
                Text(
                    text = playbackStatusLabel(ui),
                    color = design.muted,
                    style = MaterialTheme.typography.bodyMedium,
                )
            }

            PlayerSettingRow(label = "Offline", design = design) {
                DownloadControl(
                    ui = ui,
                    onDownload = onDownload,
                    onOpenDownloads = onOpenDownloads,
                )
            }
        }
    }
}

@Composable
private fun PlayerSettingRow(
    label: String,
    design: MobilePlayerDesign,
    content: @Composable () -> Unit,
) {
    Row(
        modifier = Modifier.fillMaxWidth(),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(design.spacingMediumDp.dp),
    ) {
        Text(
            text = label,
            color = design.muted,
            style = MaterialTheme.typography.bodyMedium,
            modifier = Modifier.weight(1f),
        )
        content()
    }
}

@Composable
private fun PlayerIconButton(
    icon: ImageVector,
    label: String,
    design: MobilePlayerDesign,
    onClick: () -> Unit,
    selected: Boolean = false,
    prominent: Boolean = false,
    large: Boolean = false,
    enabled: Boolean = true,
) {
    val size = when {
        prominent -> 64.dp
        large -> 52.dp
        else -> 44.dp
    }
    Surface(
        color = when {
            prominent -> design.accent
            selected -> design.focus.copy(alpha = 0.22f)
            else -> design.sheet
        },
        shape = CircleShape,
        modifier = Modifier.size(size),
    ) {
        IconButton(
            onClick = onClick,
            enabled = enabled,
        ) {
            Icon(
                imageVector = icon,
                contentDescription = label,
                tint = when {
                    !enabled -> design.muted.copy(alpha = 0.45f)
                    prominent -> Color.Black
                    selected -> design.focus
                    else -> design.subtitleText
                },
                modifier = Modifier.size(if (prominent) 34.dp else 26.dp),
            )
        }
    }
}

@Composable
private fun SubtitleMenu(
    expanded: Boolean,
    tracks: List<MediaTrack>,
    selectedTrackId: String?,
    onDismiss: () -> Unit,
    onSelect: (MediaTrack?) -> Unit,
) {
    DropdownMenu(
        expanded = expanded,
        onDismissRequest = onDismiss,
    ) {
        DropdownMenuItem(
            text = { Text(if (selectedTrackId == null) "✓ Off" else "Off") },
            onClick = {
                onSelect(null)
                onDismiss()
            },
        )
        tracks.forEach { track ->
            DropdownMenuItem(
                text = {
                    Text(
                        trackLabel(
                            track,
                            selected = track.id == selectedTrackId,
                        ),
                    )
                },
                onClick = {
                    onSelect(track)
                    onDismiss()
                },
            )
        }
    }
}

private fun playbackStatusLabel(ui: NativePlayerUiState): String =
    when {
        ui.offlineMode -> "Offline"
        ui.playingDownload -> "On device"
        else -> when (ui.transport) {
            PlaybackTransport.DIRECT -> "Direct Play"
            PlaybackTransport.HLS_FALLBACK -> "Server"
            PlaybackTransport.LIVE_MP4_FALLBACK -> "Server"
            null -> "Checking"
        }
    }

private fun selectedTrackLabel(
    tracks: List<MediaTrack>,
    selectedId: String?,
    fallback: String,
): String {
    val track = tracks.firstOrNull { it.id == selectedId } ?: return fallback
    return track.title?.takeIf { it.isNotBlank() }
        ?: track.language?.takeIf { it.isNotBlank() }?.uppercase()
        ?: track.codec?.takeIf { it.isNotBlank() }?.uppercase()
        ?: "Track ${track.streamIndex}"
}

private fun formatSpeed(speed: Float): String =
    if (speed % 1f == 0f) {
        "${speed.toInt()}×"
    } else {
        "${speed}×"
    }

/**
 * Explicit per-episode download action. Only shown while the server is
 * reachable and advertises offline downloads, or when a download exists.
 */
@Composable
private fun DownloadControl(
    ui: NativePlayerUiState,
    onDownload: () -> Unit,
    onOpenDownloads: () -> Unit,
) {
    val download = ui.download
    val label = when {
        ui.downloadBusy -> "Preparing…"
        download == null -> if (ui.downloadsSupported) "Download" else null
        else -> when (download.state) {
            DownloadState.QUEUED -> "Queued"
            DownloadState.DOWNLOADING -> "${download.progressPercent}%"
            DownloadState.PAUSED -> "Paused"
            DownloadState.READY -> "Downloaded"
            DownloadState.FAILED -> if (ui.downloadsSupported) "Retry download" else "Download failed"
        }
    } ?: return

    val startsDownload = download == null ||
        (download.state == DownloadState.FAILED && ui.downloadsSupported)

    TextButton(
        onClick = if (startsDownload) onDownload else onOpenDownloads,
        enabled = !ui.downloadBusy,
    ) {
        Text(label, color = Color.White)
    }
}

@Composable
private fun StorageRecoveryOverlay(
    modifier: Modifier = Modifier,
    availability: MediaAvailability,
    exhausted: Boolean,
    wakeInProgress: Boolean,
    design: MobilePlayerDesign,
    onRetry: () -> Unit,
    onWake: () -> Unit,
) {
    val detail = when (availability.state) {
        "file_missing" -> "The episode file is missing from the configured media storage."
        "source_unreachable" -> "The media storage cannot currently be read."
        "source_starting" -> "The NAS is starting. Jularr is waiting for media storage."
        "source_offline" -> "The media storage is offline."
        else -> "Jularr is waiting for media storage."
    }

    StatusOverlay(
        modifier = modifier,
        title = if (exhausted) "Storage still unavailable" else "Waiting for storage",
        detail = detail,
        design = design,
        showProgress = !exhausted &&
            StorageRecoveryPolicy.shouldAutomaticallyRetry(availability),
        actionLabel = "Try again",
        onAction = onRetry,
        secondaryLabel = if (StorageRecoveryPolicy.canOfferWake(availability)) {
            if (wakeInProgress) "Waking…" else "Wake NAS"
        } else {
            null
        },
        onSecondary = if (StorageRecoveryPolicy.canOfferWake(availability) && !wakeInProgress) {
            onWake
        } else {
            null
        },
    )
}

@Composable
private fun StatusOverlay(
    modifier: Modifier = Modifier,
    title: String,
    detail: String,
    design: MobilePlayerDesign,
    showProgress: Boolean,
    actionLabel: String? = null,
    onAction: (() -> Unit)? = null,
    secondaryLabel: String? = null,
    onSecondary: (() -> Unit)? = null,
) {
    Surface(
        color = design.sheet,
        shape = RoundedCornerShape(design.sheetRadiusDp.dp),
        modifier = modifier.padding(24.dp),
    ) {
        Column(
            modifier = Modifier.padding(20.dp),
            horizontalAlignment = Alignment.CenterHorizontally,
            verticalArrangement = Arrangement.spacedBy(12.dp),
        ) {
            if (showProgress) {
                CircularProgressIndicator()
            }
            Text(
                text = title,
                color = design.subtitleText,
                style = MaterialTheme.typography.titleLarge,
            )
            Text(
                text = detail,
                color = design.muted,
                style = MaterialTheme.typography.bodyMedium,
            )
            if (actionLabel != null && onAction != null) {
                Button(onClick = onAction) {
                    Text(actionLabel)
                }
            }
            if (secondaryLabel != null && onSecondary != null) {
                TextButton(onClick = onSecondary) {
                    Text(secondaryLabel, color = design.subtitleText)
                }
            }
        }
    }
}

@Composable
private fun LearningSheet(
    modifier: Modifier = Modifier,
    cue: SubtitleCue?,
    selectedTerm: de.juloc.jularr.core.model.TermDetail?,
    termLoading: Boolean,
    design: MobilePlayerDesign,
    ttsSpeaking: Boolean,
    ttsSpeakingKey: String?,
    onToken: (String) -> Unit,
    onRepeat: () -> Unit,
    onKnown: () -> Unit,
    onLearning: () -> Unit,
    onBackFromTerm: () -> Unit,
    onClose: () -> Unit,
    onSpeakLine: (String) -> Unit,
    onSpeakWord: (String) -> Unit,
    onStopSpeaking: () -> Unit,
    onOpenVoiceSettings: () -> Unit,
) {
    Surface(
        color = design.sheet,
        shape = RoundedCornerShape(
            topStart = design.sheetRadiusDp.dp,
            topEnd = design.sheetRadiusDp.dp,
        ),
        modifier = modifier.fillMaxWidth(),
    ) {
        Column(
            modifier = Modifier.padding(18.dp),
            verticalArrangement = Arrangement.spacedBy(12.dp),
        ) {
            Row(
                modifier = Modifier.fillMaxWidth(),
                verticalAlignment = Alignment.CenterVertically,
            ) {
                Text(
                    text = if (selectedTerm == null) "Learn this line" else "Word",
                    color = design.subtitleText,
                    style = MaterialTheme.typography.titleMedium,
                    modifier = Modifier.weight(1f),
                )
                if (selectedTerm != null) {
                    TextButton(onClick = onBackFromTerm) {
                        Text("Sentence")
                    }
                    TextButton(
                        onClick = {
                            if (ttsSpeaking && ttsSpeakingKey == "word") {
                                onStopSpeaking()
                            } else {
                                onSpeakWord(selectedTerm.canonical)
                            }
                        },
                    ) {
                        Text(if (ttsSpeaking && ttsSpeakingKey == "word") "Stop" else "Speak")
                    }
                }
                TextButton(onClick = onOpenVoiceSettings) {
                    Text("Voice")
                }
                TextButton(onClick = onClose) {
                    Text("Close")
                }
            }

            cue?.let {
                Text(
                    text = it.text,
                    color = design.subtitleText,
                    fontSize = 22.sp,
                )

                Row(
                    modifier = Modifier
                        .fillMaxWidth()
                        .horizontalScroll(rememberScrollState()),
                    horizontalArrangement = Arrangement.spacedBy(4.dp),
                ) {
                    it.tokens.forEach { token ->
                        val termId = token.termId
                        Text(
                            text = token.surface,
                            color = if (termId != null) design.focus else design.muted,
                            modifier = if (termId != null) {
                                Modifier
                                    .background(
                                        color = design.subtitleBackground,
                                        shape = RoundedCornerShape(design.controlRadiusDp.dp),
                                    )
                                    .clickable { onToken(termId) }
                                    .padding(horizontal = 8.dp, vertical = 6.dp)
                            } else {
                                Modifier.padding(horizontal = 2.dp, vertical = 6.dp)
                            },
                        )
                    }
                }
            }

            if (termLoading) {
                CircularProgressIndicator()
            }

            selectedTerm?.let { term ->
                Text(
                    text = term.canonical,
                    color = design.focus,
                    fontSize = 28.sp,
                )
                term.reading?.let {
                    Text(text = it, color = design.subtitleText)
                }
                term.meaning?.let {
                    Text(text = it, color = design.subtitleText)
                }
                Text(
                    text = "Learning state: ${term.state}",
                    color = design.muted,
                )
                Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    Button(onClick = onLearning) {
                        Text("Learning")
                    }
                    Button(onClick = onKnown) {
                        Text("Known")
                    }
                }
            }

            Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                Button(onClick = onRepeat) {
                    Text("Repeat line")
                }
                cue?.let {
                    TextButton(
                        onClick = {
                            if (ttsSpeaking && ttsSpeakingKey == "line") {
                                onStopSpeaking()
                            } else {
                                onSpeakLine(it.text)
                            }
                        },
                    ) {
                        Text(if (ttsSpeaking && ttsSpeakingKey == "line") "Stop" else "Speak line")
                    }
                }
            }
        }
    }
}

private fun trackLabel(
    track: MediaTrack,
    selected: Boolean,
): String {
    val name = track.title?.takeIf { it.isNotBlank() }
        ?: track.language?.takeIf { it.isNotBlank() }
        ?: "Track ${track.streamIndex}"
    return if (selected) "✓ $name" else name
}

private fun formatTime(milliseconds: Long): String {
    val totalSeconds = (milliseconds.coerceAtLeast(0) / 1000)
    val minutes = totalSeconds / 60
    val seconds = totalSeconds % 60
    return "%d:%02d".format(minutes, seconds)
}
