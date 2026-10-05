package de.juloc.jularr.tv

import android.net.Uri
import android.os.SystemClock
import androidx.activity.compose.BackHandler
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.annotation.OptIn
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.padding
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableLongStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import androidx.tv.material3.Button
import androidx.tv.material3.MaterialTheme
import androidx.tv.material3.Surface
import androidx.tv.material3.Text
import de.juloc.jularr.core.api.TvServerDiscoveryClient
import de.juloc.jularr.core.model.DiscoveredJularrServer
import de.juloc.jularr.core.player.JularrMedia3Player
import de.juloc.jularr.core.player.PlaybackTransport
import de.juloc.jularr.core.player.toPlaybackMetadata
import de.juloc.jularr.core.session.PlaybackCommand
import de.juloc.jularr.core.session.PlaybackPairing
import de.juloc.jularr.core.session.PlaybackSessionToken
import de.juloc.jularr.core.session.PlaybackSessionUpdate
import de.juloc.jularr.core.update.AppVariant
import de.juloc.jularr.core.update.UpdateCheckResult
import de.juloc.jularr.core.update.UpdateDownloadResult
import de.juloc.jularr.core.update.UpdateInstall
import de.juloc.jularr.core.update.UpdateManager
import de.juloc.jularr.core.update.UpdatePreferences
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import androidx.media3.common.util.UnstableApi
import java.io.File
import java.net.URI

@OptIn(UnstableApi::class)
@Composable
fun TvAppHost(
    controller: TvAppController,
    settings: TvServerSettings,
    cookies: TvSessionCookieStore,
    player: JularrMedia3Player,
    onFinish: () -> Unit,
    sessionStore: TvSessionStore? = null,
    discovery: TvServerDiscoveryClient = TvServerDiscoveryClient(),
) {
    val scope = rememberCoroutineScope()
    var snapshot by remember { mutableStateOf(controller.snapshot) }
    var openedEpisodeId by remember { mutableStateOf<String?>(null) }
    var playbackGeneration by remember { mutableIntStateOf(0) }
    var forceFallback by remember { mutableStateOf(false) }
    var currentTransport by remember { mutableStateOf<PlaybackTransport?>(null) }
    var resumePositionMs by remember { mutableLongStateOf(0L) }
    var resumeShouldPlay by remember { mutableStateOf(true) }
    var currentPositionMs by remember { mutableLongStateOf(0L) }
    var currentDurationMs by remember { mutableLongStateOf(0L) }
    var selectedAudioTrackId by remember { mutableStateOf<String?>(null) }
    var selectedSubtitleTrackId by remember { mutableStateOf<String?>(null) }
    var cueRefreshRunning by remember { mutableStateOf(false) }
    var companionRuntime by remember { mutableStateOf<TvCompanionRuntime?>(null) }
    var companionPairing by remember { mutableStateOf<PlaybackPairing?>(null) }
    var companionBusy by remember { mutableStateOf(false) }
    var remoteCommand by remember { mutableStateOf<PlaybackCommand?>(null) }
    var companionSelectedTermId by remember { mutableStateOf<String?>(null) }
    var lastCompanionPushAt by remember { mutableLongStateOf(0L) }
    var lastCompanionCueId by remember { mutableLongStateOf(0L) }
    var lastCompanionPlaying by remember { mutableStateOf<Boolean?>(null) }
    var discovering by remember { mutableStateOf(false) }
    var discoveredServers by remember { mutableStateOf<List<DiscoveredJularrServer>>(emptyList()) }

    // Self-update (#490): the TV app detects releases independently of the phone app,
    // using the same shared core-update module.
    val context = LocalContext.current
    val updateManager = remember { UpdateManager() }
    val updatePreferences = remember { UpdatePreferences(context.applicationContext) }
    var updateChecksEnabled by remember { mutableStateOf(updatePreferences.checksEnabled) }
    var updateState by remember { mutableStateOf<TvUpdateState>(TvUpdateState.Idle) }
    var updatePromptInfo by remember { mutableStateOf<UpdateCheckResult.UpdateAvailable?>(null) }
    val updateChecksumMismatchMessage = stringResource(R.string.tv_update_failed_checksum)

    fun checkForUpdatesNow() {
        scope.launch {
            updateState = TvUpdateState.Checking
            updatePreferences.lastCheckedAtMillis = System.currentTimeMillis()
            val result = withContext(Dispatchers.IO) {
                updateManager.checkForUpdate(BuildConfig.VERSION_NAME, AppVariant.Tv)
            }
            updateState = when (result) {
                UpdateCheckResult.UpToDate -> TvUpdateState.UpToDate
                is UpdateCheckResult.Error -> TvUpdateState.Failed(result.message)
                is UpdateCheckResult.UpdateAvailable -> TvUpdateState.Available(result)
            }
        }
    }

    fun startUpdateDownload(info: UpdateCheckResult.UpdateAvailable) {
        scope.launch {
            updateState = TvUpdateState.Downloading(0L, info.apkAsset.sizeBytes)
            val destination = File(context.cacheDir, "updates/${info.apkAsset.name}")
            val result = withContext(Dispatchers.IO) {
                updateManager.downloadAndVerify(info, destination) { bytesRead, totalBytes ->
                    updateState = TvUpdateState.Downloading(bytesRead, totalBytes)
                }
            }
            updateState = when (result) {
                is UpdateDownloadResult.Success -> TvUpdateState.ReadyToInstall(info, result.file)
                UpdateDownloadResult.ChecksumMismatch ->
                    TvUpdateState.Failed(updateChecksumMismatchMessage)
                is UpdateDownloadResult.Failed -> TvUpdateState.Failed(result.message)
            }
        }
    }

    val requestUpdateInstallPermission = rememberLauncherForActivityResult(
        contract = ActivityResultContracts.StartActivityForResult(),
    ) {
        // Returning from "allow unknown sources" does not itself confirm the grant; the
        // user selects Install again, which re-checks canInstallPackages().
    }

    fun installUpdate() {
        val ready = updateState as? TvUpdateState.ReadyToInstall ?: return
        if (UpdateInstall.canInstallPackages(context)) {
            context.startActivity(
                UpdateInstall.installIntent(context, ready.apkFile, TvUpdateFileProviderAuthority),
            )
        } else {
            requestUpdateInstallPermission.launch(UpdateInstall.manageUnknownAppSourcesIntent(context))
        }
    }

    // Automatic check on app start: only when enabled and stale, and never blocks normal
    // TV navigation. A network/API failure here is silently ignored; the user can always
    // use the manual "Check for updates" action in Profile instead.
    LaunchedEffect(Unit) {
        if (!updatePreferences.checksEnabled ||
            !UpdatePreferences.isCheckDue(updatePreferences.lastCheckedAtMillis)
        ) {
            return@LaunchedEffect
        }

        updatePreferences.lastCheckedAtMillis = System.currentTimeMillis()
        val result = try {
            withContext(Dispatchers.IO) {
                updateManager.checkForUpdate(BuildConfig.VERSION_NAME, AppVariant.Tv)
            }
        } catch (exception: Exception) {
            null
        }

        if (result is UpdateCheckResult.UpdateAvailable && updatePreferences.shouldPrompt(result.version)) {
            updatePromptInfo = result
        }
    }

    val playerRoute = snapshot.navigation.route as? TvRoute.Player
    val episodeBundle = snapshot.episode
    val episodeId = playerRoute?.episodeId
    val progressPolicy = remember(episodeId) { TvProgressPolicy() }

    fun launchSnapshot(block: suspend () -> TvAppSnapshot) {
        snapshot = snapshot.copy(busy = true, error = null)
        scope.launch {
            snapshot = block()
        }
    }

    fun currentCompanionUpdate(): PlaybackSessionUpdate? {
        val bundle = snapshot.episode ?: return null
        val cue = TvCueTimeline.currentCue(
            bundle.cues,
            currentPositionMs,
        )
        val selectedTermId = companionSelectedTermId?.takeIf { selected ->
            cue?.tokens?.any { it.termId == selected } == true
        }

        return PlaybackSessionUpdate(
            animeTitle = bundle.bootstrap.episode.animeTitle,
            episodeTitle = bundle.bootstrap.episode.title,
            positionMs = currentPositionMs.coerceAtLeast(0),
            durationMs = currentDurationMs.takeIf { it > 0 },
            isPlaying = player.player.isPlaying,
            playbackRate = player.player.playbackParameters.speed.toDouble(),
            audioTrackId = selectedAudioTrackId,
            subtitleTrackId = selectedSubtitleTrackId,
            currentCueId = cue?.id,
            currentCueText = cue?.text,
            currentCueTokens = cue?.tokens?.map { token ->
                PlaybackSessionToken(
                    surface = token.surface,
                    termId = token.termId,
                    canonical = token.canonical,
                    reading = token.reading,
                    meaning = token.meaning,
                    state = token.state,
                )
            }.orEmpty(),
            selectedTermId = selectedTermId,
        )
    }

    fun endCompanionSession() {
        val runtime = companionRuntime ?: return
        companionRuntime = null
        companionPairing = null
        companionSelectedTermId = null
        remoteCommand = null
        lastCompanionPushAt = 0
        lastCompanionCueId = 0
        lastCompanionPlaying = null

        scope.launch(Dispatchers.IO) {
            runCatching { runtime.end() }
        }
    }

    fun pushCompanionState(force: Boolean = false) {
        val runtime = companionRuntime ?: return
        val update = currentCompanionUpdate() ?: return
        val cueId = update.currentCueId ?: 0
        val playing = update.isPlaying
        val now = SystemClock.elapsedRealtime()
        val importantChange =
            cueId != lastCompanionCueId ||
                playing != lastCompanionPlaying

        if (!force &&
            !importantChange &&
            now - lastCompanionPushAt < 1_000
        ) {
            return
        }

        lastCompanionPushAt = now
        lastCompanionCueId = cueId
        lastCompanionPlaying = playing

        scope.launch {
            runCatching {
                withContext(Dispatchers.IO) {
                    runtime.update(update)
                }
            }
        }
    }

    fun resetPlaybackRuntime() {
        endCompanionSession()
        player.player.stop()
        openedEpisodeId = null
        currentTransport = null
        forceFallback = false
        resumePositionMs = 0
        resumeShouldPlay = true
        currentPositionMs = 0
        currentDurationMs = 0
        selectedAudioTrackId = null
        selectedSubtitleTrackId = null
        cueRefreshRunning = false
    }

    fun persist(
        event: TvProgressEvent,
        positionMs: Long,
        durationMs: Long,
    ) {
        val write = progressPolicy.evaluate(
            event = event,
            nowMs = SystemClock.elapsedRealtime(),
            positionMs = positionMs,
            durationMs = durationMs.takeIf { it > 0 },
        ) ?: return

        scope.launch {
            controller.saveProgress(write)
        }
    }

    fun refreshCueWindowIfNeeded(positionMs: Long) {
        val bundle = snapshot.episode ?: return
        if (cueRefreshRunning ||
            bundle.bootstrap.activeLearningSubtitleTrackId == null ||
            !TvCueTimeline.shouldRefresh(bundle.cues, positionMs)
        ) {
            return
        }

        cueRefreshRunning = true
        scope.launch {
            snapshot = controller.refreshCueWindow(positionMs)
            cueRefreshRunning = false
            pushCompanionState(force = true)
        }
    }

    LaunchedEffect(Unit) {
        if (settings.origin != null &&
            snapshot.capabilities == null &&
            snapshot.navigation.route == TvRoute.Login
        ) {
            snapshot = controller.restoreConnection()
        }
    }

    // Best-effort LAN discovery (#489): only relevant while Setup has no server yet. Cancelled
    // automatically once the route changes (Setup is a one-shot screen, never revisited with
    // stale results) or the app moves on to Login/Home.
    LaunchedEffect(snapshot.navigation.route) {
        if (snapshot.navigation.route != TvRoute.Setup) {
            return@LaunchedEffect
        }

        discovering = true
        discoveredServers = runCatching { discovery.discover() }.getOrDefault(emptyList())
        discovering = false
    }

    LaunchedEffect(episodeId) {
        if (episodeId == null) {
            resetPlaybackRuntime()
            return@LaunchedEffect
        }

        val bundle = snapshot.episode ?: return@LaunchedEffect
        if (bundle.bootstrap.episode.id == episodeId) {
            resumePositionMs = bundle.progress.positionMs
            currentPositionMs = bundle.progress.positionMs
            currentDurationMs = bundle.progress.durationMs ?: 0
            selectedAudioTrackId = bundle.bootstrap.defaultAudioTrackId
            selectedSubtitleTrackId = bundle.bootstrap.defaultSubtitleTrackId
            forceFallback = false
            openedEpisodeId = null
            currentTransport = null
        }

        val supportsCompanion =
            snapshot.capabilities?.features?.playbackSessions == true &&
                snapshot.capabilities?.features?.companionPairing == true &&
                snapshot.capabilities?.features?.companionControl == true
        val origin = settings.origin
        val initial = currentCompanionUpdate()

        if (supportsCompanion &&
            origin != null &&
            initial != null
        ) {
            endCompanionSession()
            val runtime = TvCompanionRuntime(
                serverOrigin = origin,
                requestHeaders = cookies::requestHeaders,
            )

            val started = runCatching {
                withContext(Dispatchers.IO) {
                    runtime.start(
                        episodeId = episodeId,
                        initial = initial,
                        onState = { },
                        onCommand = { command ->
                            scope.launch {
                                remoteCommand = command
                            }
                        },
                        onEnded = {
                            scope.launch {
                                companionRuntime = null
                                companionPairing = null
                                remoteCommand = null
                            }
                        },
                    )
                }
            }

            if (started.isSuccess) {
                companionRuntime = runtime
                lastCompanionPushAt = SystemClock.elapsedRealtime()
                lastCompanionCueId = started.getOrNull()?.currentCueId ?: 0
                lastCompanionPlaying = started.getOrNull()?.isPlaying
            } else {
                runCatching {
                    withContext(Dispatchers.IO) {
                        runtime.close()
                    }
                }
            }
        }
    }

    LaunchedEffect(
        episodeId,
        snapshot.storageDecision,
        playbackGeneration,
    ) {
        val route = playerRoute ?: return@LaunchedEffect
        val bundle = snapshot.episode ?: return@LaunchedEffect
        val capabilities = snapshot.capabilities ?: return@LaunchedEffect
        val origin = settings.origin ?: return@LaunchedEffect

        if (snapshot.storageDecision != null ||
            openedEpisodeId == route.episodeId
        ) {
            return@LaunchedEffect
        }

        val directSupported = !forceFallback &&
            TvDeviceCodecSupport.supportsDirectPlayback(bundle.bootstrap)

        val plan = runCatching {
            TvPlaybackPlanner.plan(
                serverOrigin = origin,
                capabilities = capabilities,
                bootstrap = bundle.bootstrap,
                directSupported = directSupported,
                startPositionMs = resumePositionMs,
            )
        }.getOrElse {
            snapshot = controller.reportError(it)
            return@LaunchedEffect
        }

        currentTransport = plan.transport
        player.open(
            uri = Uri.parse(plan.uri),
            startPositionMs = plan.startPositionMs,
            playWhenReady = resumeShouldPlay,
            requestHeaders = cookies.requestHeaders(),
            metadata = bundle.bootstrap.episode.toPlaybackMetadata(),
        )
        openedEpisodeId = route.episodeId
    }

    LaunchedEffect(
        episodeId,
        snapshot.storageDecision?.state,
        snapshot.storageDecision?.retryAfterMs,
    ) {
        var decision = snapshot.storageDecision ?: return@LaunchedEffect
        if (decision.primaryAction != TvStorageAction.RETRY) {
            return@LaunchedEffect
        }

        val startedAt = SystemClock.elapsedRealtime()
        while (decision.primaryAction == TvStorageAction.RETRY) {
            delay(decision.retryAfterMs.toLong().coerceAtLeast(250L))
            val elapsed = SystemClock.elapsedRealtime() - startedAt
            val updated = controller.refreshEpisodeStorage(elapsed)
            snapshot = updated

            val next = updated.storageDecision
            if (next == null) {
                openedEpisodeId = null
                playbackGeneration += 1
                break
            }

            decision = next
        }
    }

    if (playerRoute == null) {
        BackHandler(enabled = updatePromptInfo == null) {
            val next = controller.back()
            if (next == null) {
                onFinish()
            } else {
                snapshot = next
            }
        }
    }

    // The update prompt is dismissed by Back before Back does anything else (#490: TV
    // update UI must be Back-safe and never trap or bypass normal navigation).
    BackHandler(enabled = updatePromptInfo != null) {
        updatePromptInfo = null
    }

    val focusMemory = remember { TvFocusMemory() }

    when (val route = snapshot.navigation.route) {
        TvRoute.Setup -> TvSetupScreen(
            initialOrigin = settings.origin.orEmpty(),
            error = snapshot.error,
            busy = snapshot.busy,
            discovering = discovering,
            discovered = discoveredServers,
            onConnect = { origin ->
                launchSnapshot {
                    cookies.clear()
                    controller.connect(origin)
                }
            },
        )

        TvRoute.Login -> TvLoginScreen(
            serverOrigin = settings.origin.orEmpty(),
            error = snapshot.error,
            busy = snapshot.busy,
            onLogin = { userName, password ->
                launchSnapshot {
                    controller.login(userName, password)
                }
            },
            onChangeServer = {
                cookies.clear()
                snapshot = controller.changeServer()
            },
            pairingEnabled = snapshot.capabilities?.features?.devicePairing == true,
            onStartPairing = { controller.startDevicePairing() },
            onPollPairing = { deviceCode -> controller.pollDevicePairing(deviceCode) },
            onPaired = { account ->
                launchSnapshot { controller.completeDevicePairing(account) }
            },
        )

        TvRoute.ProfileSelect -> {
            val sessions = sessionStore?.getSessions().orEmpty()
            TvProfileSelectScreen(
                sessions = sessions,
                onSelectSession = { session ->
                    launchSnapshot {
                        controller.selectSavedSession(session)
                    }
                },
                onAddAccount = {
                    cookies.clear()
                    snapshot = controller.changeServer()
                },
            )
        }

        TvRoute.Home, TvRoute.Watchlist, TvRoute.Activity, TvRoute.Profile -> Box(modifier = Modifier.fillMaxSize()) {
            Row(modifier = Modifier.fillMaxSize()) {
                TvSidebar(
                    selected = route,
                    focusMemory = focusMemory,
                    onSelect = { selected ->
                        if (selected != route) {
                            launchSnapshot { controller.selectSidebarRoute(selected) }
                        }
                    },
                )
                Box(modifier = Modifier.weight(1f).fillMaxHeight()) {
                    when (route) {
                        TvRoute.Home -> {
                            val library = snapshot.library
                            if (library == null) {
                                TvMessageScreen(
                                    title = stringResource(R.string.tv_message_home_unavailable_title),
                                    message = snapshot.error
                                        ?: stringResource(R.string.tv_message_home_unavailable_body),
                                    action = stringResource(R.string.tv_action_sign_in),
                                    onAction = {
                                        cookies.clear()
                                        snapshot = controller.changeServer()
                                    },
                                )
                            } else {
                                TvHomeScreen(
                                    library = library,
                                    continueWatching = snapshot.continueWatching,
                                    serverOrigin = settings.origin.orEmpty(),
                                    requestHeaders = cookies.requestHeaders(),
                                    error = snapshot.error,
                                    focusMemory = focusMemory,
                                    onOpenSearch = { snapshot = controller.openSearch() },
                                    onAnime = { anime ->
                                        launchSnapshot { controller.openAnime(anime.id) }
                                    },
                                    onContinueWatching = { item ->
                                        launchSnapshot {
                                            controller.playEpisode(
                                                episodeId = item.episodeId,
                                                animeId = item.animeId,
                                            )
                                        }
                                    },
                                )
                            }
                        }

                        TvRoute.Watchlist -> TvWatchlistScreen(
                            entries = snapshot.watchlist,
                            supported = snapshot.capabilities?.features?.watchlist == true,
                            serverOrigin = settings.origin.orEmpty(),
                            requestHeaders = cookies.requestHeaders(),
                            focusMemory = focusMemory,
                            onOpenAnime = { animeId ->
                                launchSnapshot { controller.openAnime(animeId) }
                            },
                        )

                        TvRoute.Activity -> TvActivityScreen(
                            history = snapshot.activity,
                            continueWatchingFallback = snapshot.continueWatching,
                            usesContinueWatchingFallback = snapshot.activityUsesContinueWatchingFallback,
                            serverOrigin = settings.origin.orEmpty(),
                            requestHeaders = cookies.requestHeaders(),
                            focusMemory = focusMemory,
                            onOpenEpisode = { episodeId, animeId ->
                                launchSnapshot {
                                    controller.openEpisode(episodeId = episodeId, animeId = animeId)
                                }
                            },
                        )

                        TvRoute.Profile -> {
                            val account = snapshot.account
                            if (account == null) {
                                TvMessageScreen(
                                    title = stringResource(R.string.tv_message_home_unavailable_title),
                                    message = snapshot.error
                                        ?: stringResource(R.string.tv_message_home_unavailable_body),
                                    action = stringResource(R.string.tv_action_sign_in),
                                    onAction = {
                                        cookies.clear()
                                        snapshot = controller.changeServer()
                                    },
                                )
                            } else {
                                TvProfileScreen(
                                    account = account,
                                    serverOrigin = settings.origin.orEmpty(),
                                    currentVersionName = BuildConfig.VERSION_NAME,
                                    updateState = updateState,
                                    updateChecksEnabled = updateChecksEnabled,
                                    canInstallPackages = UpdateInstall.canInstallPackages(context),
                                    onToggleUpdateChecksEnabled = {
                                        updateChecksEnabled = !updateChecksEnabled
                                        updatePreferences.checksEnabled = updateChecksEnabled
                                    },
                                    onCheckForUpdatesNow = ::checkForUpdatesNow,
                                    onStartUpdateDownload = ::startUpdateDownload,
                                    onInstallUpdate = ::installUpdate,
                                    onSwitchAccount = {
                                        snapshot = controller.openProfileSelect()
                                    },
                                    onSignOut = {
                                        launchSnapshot {
                                            val next = controller.signOut()
                                            cookies.clear()
                                            next
                                        }
                                    },
                                    onChangeServer = {
                                        cookies.clear()
                                        snapshot = controller.changeServer()
                                    },
                                )
                            }
                        }

                        else -> Unit
                    }
                }
            }

            updatePromptInfo?.let { info ->
                TvUpdatePromptOverlay(
                    info = info,
                    onUpdateNow = {
                        updatePromptInfo = null
                        startUpdateDownload(info)
                        launchSnapshot { controller.selectSidebarRoute(TvRoute.Profile) }
                    },
                    onLater = {
                        updatePreferences.dismissedVersion = info.version
                        updatePromptInfo = null
                    },
                    modifier = Modifier.align(Alignment.BottomEnd).padding(32.dp),
                )
            }
        }

        TvRoute.Search -> {
            val library = snapshot.library
            if (library == null) {
                controller.back()?.let { snapshot = it }
            } else {
                TvSearchScreen(
                    library = library,
                    serverOrigin = settings.origin.orEmpty(),
                    requestHeaders = cookies.requestHeaders(),
                    focusMemory = focusMemory,
                    onAnime = { anime ->
                        launchSnapshot { controller.openAnime(anime.id) }
                    },
                    onBack = {
                        controller.back()?.let { snapshot = it }
                    },
                )
            }
        }

        is TvRoute.Anime -> {
            val anime = snapshot.anime
            if (anime == null) {
                TvMessageScreen(
                    title = stringResource(R.string.tv_message_anime_unavailable_title),
                    message = snapshot.error
                        ?: stringResource(R.string.tv_message_anime_unavailable_body),
                    action = stringResource(R.string.tv_action_back),
                    onAction = {
                        controller.back()?.let { snapshot = it }
                    },
                )
            } else {
                TvAnimeScreen(
                    anime = anime,
                    serverOrigin = settings.origin.orEmpty(),
                    requestHeaders = cookies.requestHeaders(),
                    onEpisode = { episode ->
                        launchSnapshot {
                            controller.openEpisode(
                                episodeId = episode.id,
                                animeId = anime.id,
                            )
                        }
                    },
                    onBack = {
                        controller.back()?.let { snapshot = it }
                    },
                )
            }
        }

        is TvRoute.Episode -> {
            val anime = snapshot.anime
            val page = snapshot.episodePage
            if (anime == null || page == null) {
                TvMessageScreen(
                    title = stringResource(R.string.tv_message_episode_unavailable_title),
                    message = snapshot.error
                        ?: stringResource(R.string.tv_message_episode_unavailable_body),
                    action = stringResource(R.string.tv_action_back),
                    onAction = {
                        controller.back()?.let { snapshot = it }
                    },
                )
            } else {
                TvEpisodeScreen(
                    anime = anime,
                    page = page,
                    serverOrigin = settings.origin.orEmpty(),
                    requestHeaders = cookies.requestHeaders(),
                    busy = snapshot.busy,
                    error = snapshot.error,
                    onPlay = {
                        launchSnapshot {
                            controller.playEpisode(
                                episodeId = route.episodeId,
                                animeId = route.animeId,
                            )
                        }
                    },
                    onBack = {
                        controller.back()?.let { snapshot = it }
                    },
                )
            }
        }

        is TvRoute.Player -> {
            val bundle = episodeBundle
            if (bundle == null) {
                TvMessageScreen(
                    title = stringResource(R.string.tv_message_episode_unavailable_title),
                    message = snapshot.error
                        ?: stringResource(R.string.tv_message_episode_unavailable_body),
                    action = stringResource(R.string.tv_action_back),
                    onAction = {
                        controller.back()?.let { snapshot = it }
                    },
                )
            } else if (snapshot.storageDecision != null) {
                TvStorageRecoveryScreen(
                    decision = snapshot.storageDecision!!,
                    busy = snapshot.busy,
                    onRetry = {
                        scope.launch {
                            val updated = controller.refreshEpisodeStorage(0)
                            snapshot = updated
                            if (updated.storageDecision == null) {
                                openedEpisodeId = null
                                playbackGeneration += 1
                            }
                        }
                    },
                    onWake = {
                        scope.launch {
                            snapshot = controller.wakeEpisodeStorage()
                        }
                    },
                    onBack = {
                        resetPlaybackRuntime()
                        controller.back()?.let { snapshot = it }
                    },
                )
            } else {
                val cue = TvCueTimeline.currentCue(
                    bundle.cues,
                    currentPositionMs,
                )

                TvPlayerScreen(
                    player = player,
                    episodeTitle = bundle.bootstrap.episode.title,
                    currentCue = cue,
                    audioTracks = bundle.bootstrap.audioTracks,
                    subtitleTracks = bundle.bootstrap.subtitleTracks,
                    selectedAudioTrackId = selectedAudioTrackId,
                    selectedSubtitleTrackId = selectedSubtitleTrackId,
                    onSelectAudioTrack = { id ->
                        bundle.bootstrap.audioTracks
                            .firstOrNull { it.id == id }
                            ?.let { track ->
                                if (TvMediaTrackSelector.selectAudio(
                                        player.player,
                                        track,
                                    )
                                ) {
                                    selectedAudioTrackId = id
                                    pushCompanionState(force = true)
                                }
                            }
                    },
                    onSelectSubtitleTrack = { id ->
                        val track = id?.let { selected ->
                            bundle.bootstrap.subtitleTracks
                                .firstOrNull { it.id == selected }
                        }
                        if (TvMediaTrackSelector.selectSubtitle(
                                player.player,
                                track,
                            )
                        ) {
                            selectedSubtitleTrackId = id
                            pushCompanionState(force = true)
                        }
                    },
                    onPositionChanged = { position, duration, isPlaying ->
                        currentPositionMs = position
                        currentDurationMs = duration
                        resumePositionMs = position
                        resumeShouldPlay = isPlaying
                        persist(
                            event = if (isPlaying) {
                                TvProgressEvent.HEARTBEAT
                            } else {
                                TvProgressEvent.PAUSE
                            },
                            positionMs = position,
                            durationMs = duration,
                        )
                        refreshCueWindowIfNeeded(position)
                        pushCompanionState()
                    },
                    onPlaybackEnded = { position, duration ->
                        persist(
                            TvProgressEvent.ENDED,
                            position,
                            duration,
                        )
                    },
                    onSeeked = { position, duration, isPlaying ->
                        currentPositionMs = position
                        currentDurationMs = duration
                        resumePositionMs = position
                        resumeShouldPlay = isPlaying
                        persist(
                            TvProgressEvent.SEEK,
                            position,
                            duration,
                        )
                        refreshCueWindowIfNeeded(position)
                        pushCompanionState(force = true)
                    },
                    onPlaybackFailure = { position ->
                        resumePositionMs = position
                        resumeShouldPlay = true
                        player.player.stop()
                        openedEpisodeId = null

                        scope.launch {
                            val storage = controller.refreshEpisodeStorage(0)
                            snapshot = storage
                            if (storage.storageDecision != null) {
                                return@launch
                            }

                            if (currentTransport == PlaybackTransport.DIRECT) {
                                forceFallback = true
                                playbackGeneration += 1
                            } else {
                                snapshot = controller.reportError(
                                    IllegalStateException(
                                        "Server compatibility playback failed.",
                                    ),
                                )
                            }
                        }
                    },
                    canOpenOnPhone =
                        companionRuntime != null &&
                            snapshot.capabilities?.features?.companionControl == true &&
                            snapshot.capabilities?.features?.playbackSessions == true,
                    remoteCommand = remoteCommand,
                    companionVisible = companionPairing != null,
                    onCloseCompanion = {
                        companionPairing = null
                    },
                    companionOverlay = companionPairing?.let { pairing ->
                        {
                            val origin = settings.origin.orEmpty()
                            val base = URI(origin.trimEnd('/') + "/")
                            val companionUrl = base
                                .resolve(pairing.companionUrl.removePrefix("/"))
                                .toString()

                            TvCompanionPairingOverlay(
                                pairing = pairing,
                                companionUrl = companionUrl,
                                busy = companionBusy,
                                onNewCode = {
                                    val runtime = companionRuntime
                                    if (runtime != null) {
                                        scope.launch {
                                            companionBusy = true
                                            companionPairing = runCatching {
                                                withContext(Dispatchers.IO) {
                                                    runtime.createPairing()
                                                }
                                            }.getOrNull()
                                            companionBusy = false
                                        }
                                    }
                                },
                                onRevoke = {
                                    val runtime = companionRuntime
                                    if (runtime != null) {
                                        scope.launch {
                                            companionBusy = true
                                            runCatching {
                                                withContext(Dispatchers.IO) {
                                                    runtime.revoke()
                                                }
                                            }
                                            companionPairing = null
                                            companionBusy = false
                                        }
                                    }
                                },
                                onClose = {
                                    companionPairing = null
                                },
                            )
                        }
                    },
                    onSetTermState = { termId, state ->
                        scope.launch {
                            snapshot = controller.setTermState(termId, state)
                            pushCompanionState(force = true)
                        }
                    },
                    onSelectedTermChanged = { termId ->
                        companionSelectedTermId = termId
                        pushCompanionState(force = true)
                    },
                    onOpenOnPhone = { _, termId ->
                        companionSelectedTermId = termId
                        pushCompanionState(force = true)
                        val runtime = companionRuntime
                        if (runtime != null) {
                            scope.launch {
                                companionBusy = true
                                companionPairing = runCatching {
                                    withContext(Dispatchers.IO) {
                                        runtime.createPairing()
                                    }
                                }.getOrNull()
                                companionBusy = false
                            }
                        }
                    },
                    onExit = {
                        val write = progressPolicy.evaluate(
                            event = TvProgressEvent.CLOSE,
                            nowMs = SystemClock.elapsedRealtime(),
                            positionMs = currentPositionMs,
                            durationMs = currentDurationMs.takeIf { it > 0 },
                        )

                        scope.launch {
                            if (write != null) {
                                controller.saveProgress(write)
                            }
                            resetPlaybackRuntime()
                            controller.back()?.let { snapshot = it }
                        }
                    },
                )
            }
        }
    }
}

@Composable
private fun TvMessageScreen(
    title: String,
    message: String,
    action: String,
    onAction: () -> Unit,
) {
    Surface(modifier = Modifier.fillMaxSize()) {
        Column(
            modifier = Modifier.padding(56.dp),
            verticalArrangement = Arrangement.spacedBy(18.dp),
        ) {
            Text(title, style = MaterialTheme.typography.headlineLarge)
            Text(message, style = MaterialTheme.typography.bodyLarge)
            Button(onClick = onAction) {
                Text(action)
            }
        }
    }
}
