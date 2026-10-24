package de.juloc.jularr.tv

import android.net.Uri
import androidx.media3.common.C
import androidx.media3.common.MediaItem
import androidx.media3.common.MimeTypes
import androidx.media3.common.Player
import java.io.File
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
import de.juloc.jularr.core.api.playback.HttpTvCompanionV1Client
import de.juloc.jularr.core.api.playback.TvCompanionV1Client
import de.juloc.jularr.core.api.playback.TvCompanionInvitationV1
import de.juloc.jularr.core.api.playback.TvCompanionStateV1
import de.juloc.jularr.core.api.tvlegacy.HttpTvV1LegacyFeaturesClient
import de.juloc.jularr.core.api.tvlegacy.TvV1LegacyFeaturesClient
import de.juloc.jularr.core.api.tvlegacy.TvV1PlaybackAssetInfo
import de.juloc.jularr.core.api.playbackextras.HttpTvV1PlaybackExtrasClient
import de.juloc.jularr.core.api.playbackextras.TvV1PlaybackExtrasClient
import de.juloc.jularr.core.api.playbackextras.TvV1PlaybackChapter
import de.juloc.jularr.core.api.playbackextras.TvV1PlaybackMarker
import de.juloc.jularr.core.api.playbackextras.TvV1PlaybackPreferences
import de.juloc.jularr.core.api.playbackextras.TvV1PlaybackTrack
import de.juloc.jularr.core.model.ClientTrickplayDescriptor
import java.net.URLEncoder
import java.math.BigDecimal
import java.math.RoundingMode
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
    onNavigateAsset: (TvV1PlaybackAssetInfo) -> Unit,
    onUnauthorized: () -> Unit,
    createClient: (String, DeviceTokenStore) -> TvPlaybackV1Client = { server, tokens ->
        HttpTvPlaybackV1Client(server, tokens)
    },
) {
    val context = LocalContext.current
    val player = remember(context) { JularrMedia3Player(context.applicationContext) }
    val api = remember(origin, tokenStore) { createClient(origin, tokenStore) }
    val extras: TvV1PlaybackExtrasClient = remember(origin, tokenStore) {
        HttpTvV1PlaybackExtrasClient(origin, tokenStore)
    }
    val legacy: TvV1LegacyFeaturesClient = remember(origin, tokenStore) {
        HttpTvV1LegacyFeaturesClient(origin, tokenStore)
    }
    val companion: TvCompanionV1Client = remember(origin, tokenStore) {
        HttpTvCompanionV1Client(origin, tokenStore)
    }
    val scope = rememberCoroutineScope()
    var plan by remember(mediaAssetId) { mutableStateOf<TvPlaybackV1Plan?>(null) }
    var loading by remember(mediaAssetId) { mutableStateOf(true) }
    var error by remember(mediaAssetId) { mutableStateOf<String?>(null) }
    var storageWakeMessage by remember(mediaAssetId) { mutableStateOf<String?>(null) }
    var retry by remember(mediaAssetId) { mutableStateOf(0) }
    var finishing by remember(mediaAssetId) { mutableStateOf(false) }
    var positionMs by remember(mediaAssetId) { mutableStateOf(0L) }
    var durationMs by remember(mediaAssetId) { mutableStateOf<Long?>(null) }
    var companionVisible by remember(mediaAssetId) { mutableStateOf(false) }
    var companionEnabled by remember(mediaAssetId) { mutableStateOf(false) }
    var companionBusy by remember(mediaAssetId) { mutableStateOf(false) }
    var companionInvitation by remember(mediaAssetId) { mutableStateOf<TvCompanionInvitationV1?>(null) }
    var companionError by remember(mediaAssetId) { mutableStateOf<String?>(null) }
    var markers by remember(mediaAssetId) { mutableStateOf<List<TvV1PlaybackMarker>>(emptyList()) }
    var chapters by remember(mediaAssetId) { mutableStateOf<List<TvV1PlaybackChapter>>(emptyList()) }
    var playbackPreferences by remember(mediaAssetId) { mutableStateOf<TvV1PlaybackPreferences?>(null) }
    var seekPreview by remember(mediaAssetId) { mutableStateOf<ClientTrickplayDescriptor?>(null) }
    var previousAsset by remember(mediaAssetId) { mutableStateOf<TvV1PlaybackAssetInfo?>(null) }
    var nextAsset by remember(mediaAssetId) { mutableStateOf<TvV1PlaybackAssetInfo?>(null) }
    var availableTracks by remember(mediaAssetId) { mutableStateOf<List<TvV1PlaybackTrack>>(emptyList()) }
    var selectedAudioKey by remember(mediaAssetId) { mutableStateOf<String?>(null) }
    var audioStreamIndex by remember(mediaAssetId) { mutableStateOf<Int?>(null) }
    var selectedSubtitleKey by remember(mediaAssetId) { mutableStateOf<String?>(null) }
    var selectedSubtitleConfig by remember(mediaAssetId) {
        mutableStateOf<MediaItem.SubtitleConfiguration?>(null)
    }
    // Keep sidecars until the Media3 player is disposed, even across autoplay-next.
    val subtitleFiles = remember(player) { mutableListOf<File>() }

    DisposableEffect(player) {
        onDispose {
            player.player.stop()
            player.close()
            subtitleFiles.forEach { it.delete() }
            subtitleFiles.clear()
        }
    }

    LaunchedEffect(mediaAssetId, retry) {
        loading = true
        error = null
        storageWakeMessage = null
        try {
            val oldPlan = plan
            if (oldPlan != null) {
                val previousPosition = player.player.currentPosition.coerceAtLeast(0L)
                val previousDuration = player.player.duration.takeIf { it > 0 } ?: oldPlan.durationMs
                player.player.pause()
                api.update(
                    oldPlan.playbackSessionId,
                    previousPosition,
                    previousDuration,
                    isEnded = true,
                    isCompleted = false,
                )
                plan = null
            }
            val current = retryWhileStorageWaking(
                start = { api.start(mediaAssetId, TvV1PlaybackCapabilities.read(), audioStreamIndex) },
                onWaiting = { attempt, maximum ->
                    storageWakeMessage = "NAS wird geweckt – Versuch $attempt von $maximum …"
                },
            )
            storageWakeMessage = null
            // The server may rotate credentials in the session-creation response.
            val credential = checkNotNull(tokenStore.current) { "The device token is no longer valid." }
            val uri = Uri.parse(current.streamUrl)
            check(uri.scheme == "http" || uri.scheme == "https")
            // Every playback session has its own invitation and command revision.
            // A retry or audio-track change must not reuse a remote from the old session.
            companionEnabled = false
            companionVisible = false
            companionInvitation = null
            companionError = null
            plan = current
            positionMs = current.resumePositionMs
            durationMs = current.durationMs
            player.open(
                uri = uri,
                startPositionMs = current.resumePositionMs,
                requestHeaders = mapOf("Authorization" to "Bearer $credential"),
                metadata = JularrPlaybackMetadata(mediaAssetId.toString(), title),
                subtitleConfigurations = selectedSubtitleConfig?.let(::listOf).orEmpty(),
            )
        } catch (cancellation: CancellationException) {
            throw cancellation
        } catch (failure: AppApiException) {
            when {
                failure.statusCode == 401 -> onUnauthorized()
                failure.code == "storage_waking" -> error =
                    "Der Medienspeicher wird vom Server aufgeweckt. Versuche es anschließend erneut."
                failure.code == "storage_unavailable" -> error =
                    "Der Medienspeicher ist derzeit nicht erreichbar. NAS-Verbindung prüfen und erneut versuchen."
                else -> error = "Jularr Playback: ${failure.code} (HTTP ${failure.statusCode})"
            }
        } catch (failure: Exception) {
            error = failure.message ?: "Die Wiedergabe konnte nicht gestartet werden."
        } finally {
            loading = false
        }
    }

    // Optional real neighbour IDs are fetched and validated independently of the stream.
    // A missing/forbidden route must not be replaced by fake episode-number increments.
    LaunchedEffect(mediaAssetId, legacy) {
        previousAsset = null
        nextAsset = null
        suspend fun findAsset(id: Long?): TvV1PlaybackAssetInfo? {
            if (id == null) return null
            val value = legacy.playbackAsset(id)
            return value.takeIf { it.mediaAssetId == id }
        }
        try {
            val current = findAsset(mediaAssetId)
            if (current != null) {
                previousAsset = try {
                    findAsset(current.previousMediaAssetId)
                } catch (cancel: CancellationException) {
                    throw cancel
                } catch (failure: AppApiException) {
                    if (failure.statusCode == 401) onUnauthorized()
                    null
                } catch (_: Exception) {
                    null
                }
                nextAsset = try {
                    findAsset(current.nextMediaAssetId)
                } catch (cancel: CancellationException) {
                    throw cancel
                } catch (failure: AppApiException) {
                    if (failure.statusCode == 401) onUnauthorized()
                    null
                } catch (_: Exception) {
                    null
                }
            }
        } catch (cancel: CancellationException) {
            throw cancel
        } catch (failure: AppApiException) {
            if (failure.statusCode == 401) onUnauthorized()
        } catch (_: Exception) {
            // No next/previous controls when actual V1 identity cannot be verified.
        }
    }

    // Optional extra endpoints are independent. Missing metadata never prevents video playback.
    LaunchedEffect(mediaAssetId, extras) {
        suspend fun loadedOrUnauthorized(block: suspend () -> Unit) {
            try {
                block()
            } catch (cancel: CancellationException) {
                throw cancel
            } catch (failure: AppApiException) {
                if (failure.statusCode == 401) onUnauthorized()
                // 403/404 are genuine unavailable feature states; no fake metadata.
            } catch (_: Exception) {
                // Optional UI metadata failure must not prevent the real stream.
            }
        }
        loadedOrUnauthorized { playbackPreferences = extras.preferences() }
        loadedOrUnauthorized { markers = extras.markers(mediaAssetId) }
        loadedOrUnauthorized { chapters = extras.chapters(mediaAssetId) }
        loadedOrUnauthorized {
            val fetched = extras.tracks(mediaAssetId)
            availableTracks = fetched
            if (selectedAudioKey == null) selectedAudioKey =
                fetched.firstOrNull { it.isAudio && it.isSelected }?.key
            if (selectedSubtitleKey == null) selectedSubtitleKey =
                fetched.firstOrNull { it.isSubtitle && it.isSelected }?.key
        }
        loadedOrUnauthorized {
            seekPreview = TvV1PlayerUiMapping.trickplay(
                mediaAssetId, extras.trickplay(mediaAssetId),
            )
        }
    }

    LaunchedEffect(plan, markers, playbackPreferences) {
        if (plan == null) return@LaunchedEffect
        while (isActive) {
            delay(600L)
            if (player.player.isPlaying) {
                val target = TvV1PlayerUiMapping.automaticSkipTarget(
                    player.player.currentPosition.coerceAtLeast(0L),
                    markers, playbackPreferences,
                )
                if (target != null) player.player.seekTo(target)
            }
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
                // Auth middleware may rotate the bearer with every API response.
                tokenStore.current?.let { credential ->
                    player.updateRequestHeaders(mapOf("Authorization" to "Bearer $credential"))
                }
            } catch (cancel: CancellationException) {
                throw cancel
            } catch (failure: AppApiException) {
                if (failure.statusCode == 401) {
                    onUnauthorized()
                    return@LaunchedEffect
                }
                // Transient progress failures must not replace an otherwise healthy video
                // with an error panel. Retry at the next heartbeat and again on exit.
            } catch (_: Exception) {
                // No alternate/legacy progress endpoint or fabricated success.
            }
        }
    }

    fun openCompanion() {
        val active = plan ?: return
        if (finishing) return
        companionVisible = true
        if (companionInvitation != null || companionBusy) return
        companionBusy = true
        companionError = null
        scope.launch {
            try {
                val issued = companion.createInvitation(active.playbackSessionId)
                if (plan?.playbackSessionId == active.playbackSessionId && !finishing) {
                    companionInvitation = issued
                    companionEnabled = true
                }
            } catch (cancel: CancellationException) {
                throw cancel
            } catch (failure: AppApiException) {
                if (failure.statusCode == 401) onUnauthorized()
                else companionError = "Fernbedienung: ${failure.code} (HTTP ${failure.statusCode})"
            } catch (failure: Exception) {
                companionError = failure.message ?: "Die Fernbedienung konnte nicht gekoppelt werden."
            } finally {
                companionBusy = false
            }
        }
    }

    fun renewCompanion() {
        if (companionBusy) return
        companionInvitation = null
        openCompanion()
    }

    fun disconnectCompanion() {
        val active = plan ?: return
        if (companionBusy) return
        companionBusy = true
        companionError = null
        scope.launch {
            try {
                check(companion.revoke(active.playbackSessionId)) {
                    "Der Server hat die Trennung nicht bestätigt."
                }
                if (plan?.playbackSessionId == active.playbackSessionId) {
                    companionEnabled = false
                    companionInvitation = null
                    companionVisible = false
                }
            } catch (cancel: CancellationException) {
                throw cancel
            } catch (failure: AppApiException) {
                if (failure.statusCode == 401) onUnauthorized()
                else companionError = "Fernbedienung: ${failure.code} (HTTP ${failure.statusCode})"
            } catch (failure: Exception) {
                companionError = failure.message ?: "Die Verbindung konnte nicht getrennt werden."
            } finally {
                companionBusy = false
            }
        }
    }

    // The server owns grants/commands; the TV alone executes them on the real Media3 player.
    // Each command is dispatched at most once and acknowledged only after the player action.
    LaunchedEffect(plan?.playbackSessionId, companionEnabled, finishing) {
        val active = plan ?: return@LaunchedEffect
        if (!companionEnabled || finishing) return@LaunchedEffect
        while (isActive && plan?.playbackSessionId == active.playbackSessionId && !finishing) {
            try {
                val state = companion.readState(active.playbackSessionId)
                val command = companion.nextCommand(active.playbackSessionId)
                if (command == null) {
                    companion.reportState(
                        active.playbackSessionId,
                        TvCompanionStateV1(
                            state.revision,
                            player.player.isPlaying,
                            player.player.currentPosition.coerceAtLeast(0),
                            player.player.duration.takeIf { it > 0 } ?: active.durationMs,
                        ),
                    )
                } else if (plan?.playbackSessionId == active.playbackSessionId && !finishing) {
                    val succeeded = try {
                        when (command.action) {
                            "play" -> {
                                if (player.player.playbackState == Player.STATE_ENDED) player.player.seekTo(0)
                                player.player.play()
                                // Real TV decoders can buffer for seconds after play().
                                // A requested start is confirmed only once Media3 reports playback.
                                var attempts = 0
                                while (!player.player.isPlaying && attempts < 40 && isActive && !finishing) {
                                    delay(100L)
                                    attempts++
                                }
                                player.player.isPlaying
                            }
                            "pause" -> {
                                player.player.pause()
                                !player.player.isPlaying && !player.player.playWhenReady
                            }
                            "seek" -> {
                                val target = command.positionMs
                                val duration = player.player.duration.takeIf { it > 0 } ?: active.durationMs
                                if (target == null || target < 0 || (duration != null && target > duration)) {
                                    false
                                } else {
                                    player.player.seekTo(target)
                                    // HLS/remote seeks can be asynchronous. Wait for the actual
                                    // player position rather than rejecting a still-pending seek.
                                    var confirmed = false
                                    var attempts = 0
                                    while (!confirmed && attempts < 30 && isActive && !finishing) {
                                        confirmed = kotlin.math.abs(player.player.currentPosition - target) <= 2_000L
                                        if (!confirmed) delay(100L)
                                        attempts++
                                    }
                                    confirmed
                                }
                            }
                            else -> false
                        }
                    } catch (cancel: CancellationException) {
                        throw cancel
                    } catch (_: Exception) {
                        // Report execution failure instead of leaving a command indefinitely pending.
                        false
                    }
                    if (plan?.playbackSessionId != active.playbackSessionId || finishing) return@LaunchedEffect
                    // If a response is lost, do not retry: the backend intentionally never
                    // re-dispatches a delivered command to prevent delayed repeat actions.
                    companion.acknowledge(
                        active.playbackSessionId, command, succeeded,
                        TvCompanionStateV1(
                            command.revision,
                            player.player.isPlaying,
                            player.player.currentPosition.coerceAtLeast(0),
                            player.player.duration.takeIf { it > 0 } ?: active.durationMs,
                        ),
                    )
                }
                companionError = null
            } catch (cancel: CancellationException) {
                throw cancel
            } catch (failure: AppApiException) {
                if (failure.statusCode == 401) {
                    onUnauthorized()
                    return@LaunchedEffect
                }
                if (failure.statusCode == 403 || failure.statusCode == 404 || failure.statusCode == 410) {
                    // A revoked/ended session must not keep polling or display a stale QR.
                    companionEnabled = false
                    companionInvitation = null
                    companionError = "Diese Fernbedienungs-Sitzung ist nicht mehr gültig. Erneut koppeln."
                    return@LaunchedEffect
                }
                // Revision conflicts and temporary connection problems are recoverable.
                // Never substitute a legacy cookie-based companion endpoint.
                companionError = "Fernbedienung nicht erreichbar (HTTP ${failure.statusCode})."
            } catch (failure: Exception) {
                companionError = failure.message ?: "Fernbedienung vorübergehend nicht erreichbar."
            }
            delay(2_000L)
        }
    }

    fun selectAudioTrack(key: String): Boolean {
        val track = availableTracks.firstOrNull { it.key == key && it.isAudio && it.isSupported }
            ?: return false
        if (!key.startsWith("s")) return false
        val index = key.drop(1).toIntOrNull() ?: return false
        if (index < 0 || finishing) return false
        if (key != selectedAudioKey) {
            selectedAudioKey = key
            audioStreamIndex = index
            retry += 1 // Server chooses the audio stream when creating a new session.
        }
        return true
    }

    fun selectSubtitleTrack(key: String?): Boolean {
        if (finishing) return false
        val track = key?.let { requested ->
            availableTracks.firstOrNull {
                it.key == requested && it.isSubtitle && it.isSupported
            } ?: return false
        }
        scope.launch {
            try {
                val configuration = if (track == null) null else {
                    val vtt = extras.subtitleVtt(mediaAssetId, track.key)
                    val file = File.createTempFile("jularr-tv-sub-$mediaAssetId-", ".vtt", context.cacheDir)
                    file.writeText(vtt, Charsets.UTF_8)
                    subtitleFiles.add(file)
                    MediaItem.SubtitleConfiguration.Builder(Uri.fromFile(file))
                        .setMimeType(MimeTypes.TEXT_VTT)
                        .setLanguage(track.language)
                        .setSelectionFlags(C.SELECTION_FLAG_DEFAULT)
                        .build()
                }
                selectedSubtitleConfig = configuration
                selectedSubtitleKey = key
                val current = plan ?: return@launch
                val position = player.player.currentPosition.coerceAtLeast(0L)
                val wasPlaying = player.player.isPlaying
                val credential = tokenStore.current ?: return@launch
                player.open(
                    uri = Uri.parse(current.streamUrl),
                    startPositionMs = position,
                    playWhenReady = wasPlaying,
                    requestHeaders = mapOf("Authorization" to "Bearer $credential"),
                    metadata = JularrPlaybackMetadata(mediaAssetId.toString(), title),
                    subtitleConfigurations = configuration?.let(::listOf).orEmpty(),
                )
            } catch (cancel: CancellationException) {
                throw cancel
            } catch (failure: AppApiException) {
                if (failure.statusCode == 401) onUnauthorized()
                else error = "Untertitel: ${failure.code} (HTTP ${failure.statusCode})"
            } catch (failure: Exception) {
                error = failure.message ?: "Der Untertitel konnte nicht geladen werden."
            }
        }
        return true
    }

    fun completePlayback(completed: Boolean, navigateTo: TvV1PlaybackAssetInfo? = null) {
        if (finishing) return
        finishing = true
        val current = plan
        val finalPosition = player.player.currentPosition.coerceAtLeast(0L)
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
            if (navigateTo != null) onNavigateAsset(navigateTo) else onExit()
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
            Button(onClick = {
                if (plan != null) completePlayback(completed = false) else onExit()
            }) { Text("Zurück") }
            if (plan != null) {
                Button(onClick = {
                    // Explicit escape route when session close is blocked by a network error.
                    // Do not mark the media completed or claim that progress was saved.
                    player.player.stop()
                    onExit()
                }) { Text("Ohne Speicherung beenden") }
            }
        }
        loading || plan == null -> TvCenteredPanel(
            title = "Jularr TV",
            description = storageWakeMessage ?: "Wiedergabesession wird vorbereitet …",
        ) {
            Text(title, style = MaterialTheme.typography.titleLarge)
            Button(onClick = { completePlayback(completed = false) }) { Text("Abbrechen") }
        }
        else -> TvPlayerScreen(
            player = player,
            episodeTitle = title,
            currentCue = null,
            skipSegments = TvV1PlayerUiMapping.skipSegments(markers, playbackPreferences),
            chapters = chapters,
            trickplay = seekPreview,
            defaultPlaybackSpeed = playbackPreferences?.speed?.toFloat() ?: 1f,
            preferredAudioLanguage = playbackPreferences?.audioLanguage,
            preferredSubtitleLanguage = playbackPreferences?.subtitleLanguage,
            serverOrigin = origin,
            requestHeaders = tokenStore.current?.let { mapOf("Authorization" to "Bearer $it") }.orEmpty(),
            audioTracks = availableTracks.filter { it.isAudio }
                .map(TvV1PlayerUiMapping::trackForDisplay),
            subtitleTracks = availableTracks.filter { it.isSubtitle }
                .map(TvV1PlayerUiMapping::trackForDisplay),
            selectedAudioTrackId = selectedAudioKey,
            selectedSubtitleTrackId = selectedSubtitleKey,
            onSelectAudioTrack = ::selectAudioTrack,
            onSelectSubtitleTrack = ::selectSubtitleTrack,
            playbackSpeeds = listOf(0.75f, 1f, 1.25f, 1.5f, 2f),
            onPositionChanged = { position, duration, _ ->
                positionMs = position
                if (duration > 0) durationMs = duration
            },
            onSeeked = { position, duration, _ ->
                positionMs = position
                if (duration > 0) durationMs = duration
            },
            previousEpisodeTitle = previousAsset?.let(::tvV1PlaybackAssetTitle),
            nextEpisodeTitle = nextAsset?.let(::tvV1PlaybackAssetTitle),
            onPreviousEpisode = {
                previousAsset?.let { completePlayback(completed = false, navigateTo = it) }
            },
            onNextEpisode = {
                nextAsset?.let { completePlayback(completed = false, navigateTo = it) }
            },
            onPlaybackEnded = { position, duration ->
                positionMs = position
                if (duration > 0) durationMs = duration
                val autoplayTarget = if (playbackPreferences?.autoplayNext == true) nextAsset else null
                completePlayback(completed = true, navigateTo = autoplayTarget)
            },
            onPlaybackFailure = { position ->
                positionMs = position
                error = "Media3 konnte diesen Stream nicht wiedergeben."
            },
            onPlaybackSpeedChanged = {
                val speed = BigDecimal.valueOf(player.player.playbackParameters.speed.toDouble())
                    .setScale(2, RoundingMode.HALF_UP)
                scope.launch {
                    try {
                        if (extras.saveSpeed(speed)) {
                            playbackPreferences = playbackPreferences?.copy(speed = speed)
                        }
                    } catch (failure: AppApiException) {
                        if (failure.statusCode == 401) onUnauthorized()
                    } catch (_: Exception) {
                        // The local playback speed remains effective for this session.
                    }
                }
            },
            onRetryPlayback = { retry += 1 },
            onExit = { completePlayback(completed = false) },
            onOpenCompanion = ::openCompanion,
            companionVisible = companionVisible,
            onCloseCompanion = { companionVisible = false },
            companionOverlay = if (companionVisible) {{
                TvV1CompanionOverlay(
                    url = companionInvitation?.let { issued ->
                        origin.trimEnd('/') + "/companion-pair.html#invite=" +
                            URLEncoder.encode(issued.token, Charsets.UTF_8.name())
                    },
                    expiresAt = companionInvitation?.expiresAt ?: "",
                    error = companionError,
                    busy = companionBusy,
                    onRenew = ::renewCompanion,
                    onDisconnect = ::disconnectCompanion,
                    onClose = { companionVisible = false },
                )
            }} else null,
            onSetTermState = { _, _ -> },
            onOpenOnPhone = { _, _ -> },
        )
    }
}

/** Only actual server labels from the next/previous playback asset may be shown. */
internal fun tvV1PlaybackAssetTitle(asset: TvV1PlaybackAssetInfo): String =
    asset.unitTitle?.takeIf(String::isNotBlank) ?: asset.canonicalTitle
