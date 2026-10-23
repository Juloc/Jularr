package de.juloc.jularr.tv

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.LazyRow
import androidx.compose.foundation.lazy.items
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import androidx.tv.material3.Button
import androidx.tv.material3.MaterialTheme
import androidx.tv.material3.Surface
import androidx.tv.material3.Text
import de.juloc.jularr.core.model.AnimeDetail
import de.juloc.jularr.core.model.ClientAccount
import de.juloc.jularr.core.model.DevicePairingPollResult
import de.juloc.jularr.core.model.DevicePairingSession
import de.juloc.jularr.core.model.DiscoveredJularrServer
import de.juloc.jularr.core.model.EpisodeSummary
import kotlinx.coroutines.delay
import kotlinx.coroutines.isActive

@Composable
fun TvSetupScreen(
    initialOrigin: String,
    error: String?,
    busy: Boolean,
    discovering: Boolean = false,
    discovered: List<DiscoveredJularrServer> = emptyList(),
    onConnect: (String) -> Unit,
) {
    var origin by rememberSaveable(initialOrigin) { mutableStateOf(initialOrigin) }
    var localError by remember { mutableStateOf<String?>(null) }

    TvCenteredPanel(
        title = stringResource(R.string.tv_setup_title),
        description = stringResource(R.string.tv_setup_description),
    ) {
        if (discovering) {
            Text(
                text = stringResource(R.string.tv_setup_discovering),
                style = MaterialTheme.typography.bodyMedium,
            )
        } else if (discovered.isNotEmpty()) {
            Column(verticalArrangement = Arrangement.spacedBy(10.dp)) {
                Text(
                    text = stringResource(R.string.tv_setup_discovered_heading),
                    style = MaterialTheme.typography.titleMedium,
                )
                for (server in discovered) {
                    Row(
                        modifier = Modifier.fillMaxWidth(),
                        horizontalArrangement = Arrangement.spacedBy(12.dp),
                        verticalAlignment = Alignment.CenterVertically,
                    ) {
                        Column(modifier = Modifier.weight(1f)) {
                            Text(server.name, style = MaterialTheme.typography.bodyLarge)
                            Text(
                                text = server.origin,
                                style = MaterialTheme.typography.bodySmall,
                                color = MaterialTheme.colorScheme.onSurface.copy(alpha = 0.7f),
                            )
                        }
                        Button(enabled = !busy, onClick = { onConnect(server.origin) }) {
                            Text(stringResource(R.string.tv_setup_discovered_connect))
                        }
                    }
                }
            }
            Text(
                text = stringResource(R.string.tv_setup_manual_heading),
                style = MaterialTheme.typography.titleMedium,
            )
        }

        TvInput(
            value = origin,
            onValueChange = {
                origin = it
                localError = null
            },
            label = stringResource(R.string.tv_setup_label_server),
            placeholder = stringResource(R.string.tv_setup_placeholder_server),
        )

        (localError ?: error)?.let {
            Text(
                text = it,
                color = MaterialTheme.colorScheme.error,
                style = MaterialTheme.typography.bodyMedium,
            )
        }

        Button(
            enabled = !busy,
            onClick = {
                val normalized = runCatching { TvServerOrigin.normalize(origin) }
                if (normalized.isFailure) {
                    localError = normalized.exceptionOrNull()?.message
                } else {
                    localError = null
                    onConnect(normalized.getOrThrow())
                }
            },
        ) {
            Text(
                if (busy) {
                    stringResource(R.string.tv_setup_button_connecting)
                } else {
                    stringResource(R.string.tv_setup_button_continue)
                },
            )
        }
    }
}

@Composable
fun TvLoginScreen(
    serverOrigin: String,
    error: String?,
    busy: Boolean,
    onLogin: (userName: String, password: String) -> Unit,
    onChangeServer: () -> Unit,
    pairingEnabled: Boolean = false,
    onStartPairing: suspend () -> DevicePairingSession = {
        throw IllegalStateException("Pairing is not enabled.")
    },
    onPollPairing: suspend (String) -> DevicePairingPollResult = {
        throw IllegalStateException("Pairing is not enabled.")
    },
    onPaired: (ClientAccount) -> Unit = {},
) {
    // A code-pairing device install never needs a password typed with a remote, so pairing is
    // the default entry point whenever the server supports it (#489); "Sign in with password"
    // is one click away for accounts/servers that need it.
    var usePassword by rememberSaveable(pairingEnabled) { mutableStateOf(!pairingEnabled) }

    if (pairingEnabled && !usePassword) {
        TvPairingPanel(
            serverOrigin = serverOrigin,
            onStartPairing = onStartPairing,
            onPollPairing = onPollPairing,
            onPaired = onPaired,
            onUsePassword = { usePassword = true },
            onChangeServer = onChangeServer,
        )
        return
    }

    var userName by rememberSaveable { mutableStateOf("") }
    var password by rememberSaveable { mutableStateOf("") }

    TvCenteredPanel(
        title = stringResource(R.string.tv_login_title),
        description = serverOrigin,
    ) {
        TvInput(
            value = userName,
            onValueChange = { userName = it },
            label = stringResource(R.string.tv_login_label_username),
            placeholder = stringResource(R.string.tv_login_placeholder_username),
        )
        TvInput(
            value = password,
            onValueChange = { password = it },
            label = stringResource(R.string.tv_login_label_password),
            placeholder = stringResource(R.string.tv_login_placeholder_password),
            password = true,
        )

        error?.let {
            Text(
                text = it,
                color = MaterialTheme.colorScheme.error,
                style = MaterialTheme.typography.bodyMedium,
            )
        }

        Row(horizontalArrangement = Arrangement.spacedBy(12.dp)) {
            Button(
                enabled = !busy && userName.isNotBlank() && password.isNotEmpty(),
                onClick = { onLogin(userName.trim(), password) },
            ) {
                Text(
                    if (busy) {
                        stringResource(R.string.tv_login_button_signing_in)
                    } else {
                        stringResource(R.string.tv_login_button_sign_in)
                    },
                )
            }
            if (pairingEnabled) {
                Button(
                    enabled = !busy,
                    onClick = { usePassword = false },
                ) {
                    Text(stringResource(R.string.tv_pairing_use_code))
                }
            }
            Button(
                enabled = !busy,
                onClick = onChangeServer,
            ) {
                Text(stringResource(R.string.tv_login_button_change_server))
            }
        }
    }
}

/**
 * Device-code pairing panel (#489): starts a pairing, shows the user code, and polls until it is
 * approved from a signed-in phone/browser session or expires (in which case it silently starts a
 * fresh one — the TV never forces the viewer to notice an expired code and act before it can show
 * a usable one again). Cancelled automatically when this leaves composition (Back, "Sign in with
 * password" or "Change server"), so no stray polling continues in the background.
 */
@Composable
private fun TvPairingPanel(
    serverOrigin: String,
    onStartPairing: suspend () -> DevicePairingSession,
    onPollPairing: suspend (String) -> DevicePairingPollResult,
    onPaired: (ClientAccount) -> Unit,
    onUsePassword: () -> Unit,
    onChangeServer: () -> Unit,
) {
    var session by remember { mutableStateOf<DevicePairingSession?>(null) }
    var startFailed by remember { mutableStateOf(false) }

    LaunchedEffect(Unit) {
        while (isActive) {
            session = null
            val started = runCatching { onStartPairing() }.getOrNull()
            if (started == null) {
                startFailed = true
                delay(RetryAfterStartFailureMs)
                continue
            }

            startFailed = false
            session = started

            while (isActive) {
                delay(started.intervalSeconds * 1_000L)
                when (val result = runCatching { onPollPairing(started.deviceCode) }.getOrNull()) {
                    is DevicePairingPollResult.Approved -> {
                        onPaired(result.account)
                        return@LaunchedEffect
                    }
                    DevicePairingPollResult.Expired -> break
                    is DevicePairingPollResult.Pending, null -> Unit
                }
            }
        }
    }

    TvCenteredPanel(
        title = stringResource(R.string.tv_pairing_title),
        description = serverOrigin,
    ) {
        val userCode = session?.userCode
        when {
            userCode != null -> {
                Text(text = userCode, style = MaterialTheme.typography.displayMedium)
                Text(
                    text = stringResource(R.string.tv_pairing_instructions, serverOrigin),
                    style = MaterialTheme.typography.bodyMedium,
                )
                Text(
                    text = stringResource(R.string.tv_pairing_waiting),
                    style = MaterialTheme.typography.bodyMedium,
                )
            }

            startFailed -> Text(
                text = stringResource(R.string.tv_pairing_start_failed),
                color = MaterialTheme.colorScheme.error,
                style = MaterialTheme.typography.bodyMedium,
            )

            else -> Text(
                text = stringResource(R.string.tv_pairing_starting),
                style = MaterialTheme.typography.bodyMedium,
            )
        }

        Row(horizontalArrangement = Arrangement.spacedBy(12.dp)) {
            Button(onClick = onUsePassword) {
                Text(stringResource(R.string.tv_pairing_use_password))
            }
            Button(onClick = onChangeServer) {
                Text(stringResource(R.string.tv_login_button_change_server))
            }
        }
    }
}

private const val RetryAfterStartFailureMs = 5_000L

@Composable
fun TvEpisodeScreen(
    anime: AnimeDetail,
    page: TvEpisodePageData,
    serverOrigin: String,
    requestHeaders: Map<String, String>,
    busy: Boolean,
    error: String?,
    onPlay: () -> Unit,
    onBack: () -> Unit,
) {
    val episode = page.detail
    val progress = page.progress
    val progressFraction = progress.percent.coerceIn(0, 100) / 100f

    Surface(modifier = Modifier.fillMaxSize()) {
        Column(
            modifier = Modifier
                .fillMaxSize()
                .padding(horizontal = 52.dp, vertical = 34.dp),
            verticalArrangement = Arrangement.spacedBy(22.dp),
        ) {
            Row(
                modifier = Modifier.fillMaxWidth(),
                horizontalArrangement = Arrangement.spacedBy(18.dp),
                verticalAlignment = Alignment.CenterVertically,
            ) {
                Button(onClick = onBack) { Text(stringResource(R.string.tv_action_back)) }
                Column(verticalArrangement = Arrangement.spacedBy(3.dp)) {
                    Text(
                        text = anime.title,
                        style = MaterialTheme.typography.titleLarge,
                    )
                    Text(
                        text = stringResource(
                            R.string.tv_episode_season_and_number,
                            episode.seasonNumber,
                            episode.number,
                        ),
                        style = MaterialTheme.typography.bodyMedium,
                    )
                }
            }

            Row(
                modifier = Modifier.fillMaxWidth(),
                horizontalArrangement = Arrangement.spacedBy(28.dp),
                verticalAlignment = Alignment.Top,
            ) {
                TvArtwork(
                    url = anime.coverImageUrl,
                    serverOrigin = serverOrigin,
                    requestHeaders = requestHeaders,
                    contentDescription = anime.title,
                    modifier = Modifier
                        .width(180.dp)
                        .aspectRatio(2f / 3f)
                        .clip(MaterialTheme.shapes.medium),
                )

                Column(
                    modifier = Modifier
                        .weight(1f)
                        .background(
                            MaterialTheme.colorScheme.surfaceVariant,
                            MaterialTheme.shapes.medium,
                        )
                        .padding(28.dp),
                    verticalArrangement = Arrangement.spacedBy(16.dp),
                ) {
                    Text(
                        text = episode.title,
                        style = MaterialTheme.typography.headlineLarge,
                    )

                    Row(horizontalArrangement = Arrangement.spacedBy(10.dp)) {
                        TvInfoPill(
                            text = stringResource(
                                if (episode.hasMedia) {
                                    R.string.tv_episode_status_ready
                                } else {
                                    R.string.tv_episode_status_media_unavailable
                                },
                            ),
                        )
                        if (episode.activeLearningSubtitleTrackId != null) {
                            TvInfoPill(text = stringResource(R.string.tv_episode_status_japanese_subtitles))
                        }
                        if (progress.isCompleted) {
                            TvInfoPill(text = stringResource(R.string.tv_episode_status_watched))
                        }
                    }

                    if (progress.percent > 0 && !progress.isCompleted) {
                        Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
                            Row(
                                modifier = Modifier.fillMaxWidth(),
                                horizontalArrangement = Arrangement.SpaceBetween,
                            ) {
                                Text(
                                    stringResource(
                                        R.string.tv_episode_continue_at,
                                        formatEpisodePosition(progress.positionMs),
                                    ),
                                    style = MaterialTheme.typography.bodyLarge,
                                )
                                Text(
                                    stringResource(R.string.tv_episode_progress_percent, progress.percent),
                                    style = MaterialTheme.typography.bodyMedium,
                                )
                            }
                            Box(
                                modifier = Modifier
                                    .fillMaxWidth()
                                    .height(6.dp)
                                    .background(
                                        MaterialTheme.colorScheme.onSurface.copy(alpha = 0.20f),
                                    ),
                            ) {
                                Box(
                                    modifier = Modifier
                                        .fillMaxWidth(progressFraction)
                                        .height(6.dp)
                                        .background(MaterialTheme.colorScheme.primary),
                                )
                            }
                        }
                    }

                    val learning = episode.learning
                    Text(
                        text = stringResource(
                            R.string.tv_episode_vocabulary,
                            learning.knownTerms,
                            learning.learningTerms,
                            learning.newTerms,
                        ),
                        style = MaterialTheme.typography.bodyMedium,
                    )

                    error?.let {
                        Text(
                            text = it,
                            color = MaterialTheme.colorScheme.error,
                            style = MaterialTheme.typography.bodyMedium,
                        )
                    }

                    Row(horizontalArrangement = Arrangement.spacedBy(14.dp)) {
                        Button(
                            enabled = episode.hasMedia && !busy,
                            onClick = onPlay,
                        ) {
                            Text(
                                when {
                                    busy -> stringResource(R.string.tv_episode_loading)
                                    progress.positionMs > 0 && !progress.isCompleted ->
                                        stringResource(R.string.tv_episode_resume)
                                    else -> stringResource(R.string.tv_episode_play)
                                },
                            )
                        }
                        Button(
                            enabled = !busy,
                            onClick = onBack,
                        ) {
                            Text(stringResource(R.string.tv_episode_episodes_button))
                        }
                    }
                }
            }
        }
    }
}
