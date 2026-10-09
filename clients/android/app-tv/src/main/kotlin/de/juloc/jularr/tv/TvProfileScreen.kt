package de.juloc.jularr.tv

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.height
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.rememberLazyListState
import androidx.compose.foundation.lazy.items
import androidx.compose.ui.focus.FocusRequester
import androidx.compose.ui.focus.focusRequester
import androidx.activity.compose.BackHandler
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.withFrameNanos
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.padding
import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import androidx.compose.ui.text.style.TextOverflow
import androidx.tv.material3.Button
import androidx.tv.material3.MaterialTheme
import androidx.tv.material3.Surface
import androidx.tv.material3.Text
import de.juloc.jularr.core.model.ClientAccount
import de.juloc.jularr.core.model.ClientPlaybackPreferences
import de.juloc.jularr.core.model.ClientPlaybackPreferencesUpdate
import de.juloc.jularr.core.update.UpdateCheckResult

/**
 * The TV sidebar's Profile/Settings destination (#522): account identity, the two
 * account-level actions the TV app exposes today (sign out, change server), and the
 * self-update panel (#490).
 */
@Composable
fun TvProfileScreen(
    account: ClientAccount,
    serverOrigin: String,
    currentVersionName: String,
    updateState: TvUpdateState,
    updateChecksEnabled: Boolean,
    canInstallPackages: Boolean,
    onToggleUpdateChecksEnabled: () -> Unit,
    onCheckForUpdatesNow: () -> Unit,
    onStartUpdateDownload: (UpdateCheckResult.UpdateAvailable) -> Unit,
    onInstallUpdate: () -> Unit,
    onSignOut: () -> Unit,
    onChangeServer: () -> Unit,
    onSwitchAccount: (() -> Unit)? = null,
    onSwitchProfile: (() -> Unit)? = null,
    playbackPreferences: ClientPlaybackPreferences? = null,
    busy: Boolean = false,
    error: String? = null,
    onUpdatePlaybackPreferences: (ClientPlaybackPreferencesUpdate) -> Unit = {},
) {
    var selectedPanel by remember { mutableStateOf<TvSettingPanel?>(null) }
    var lastPanel by remember { mutableStateOf<TvSettingPanel?>(null) }
    val rowFocus = remember { TvSettingPanel.entries.associateWith { FocusRequester() } }
    LaunchedEffect(selectedPanel) {
        if (selectedPanel == null) {
            lastPanel?.let { panel ->
                runCatching { rowFocus.getValue(panel).requestFocus() }
                lastPanel = null
            }
        } else {
            lastPanel = selectedPanel
        }
    }
    val preferencesAvailable = playbackPreferences != null
    val context = LocalContext.current
    val accent = remember(context) { TvPlayerDesignLoader.load(context).accent }
    BackHandler(enabled = selectedPanel != null) { selectedPanel = null }

    Surface(modifier = Modifier.fillMaxSize()) {
        Row(
            modifier = Modifier.fillMaxSize().padding(28.dp),
            horizontalArrangement = Arrangement.spacedBy(24.dp),
        ) {
            LazyColumn(
                modifier = Modifier.weight(1f).fillMaxSize(),
                verticalArrangement = Arrangement.spacedBy(13.dp),
            ) {
                item {
                    Text(
                        stringResource(R.string.tv_sidebar_settings),
                        style = MaterialTheme.typography.displaySmall,
                    )
                    Text(
                        stringResource(R.string.tv_settings_description),
                        style = MaterialTheme.typography.titleMedium,
                        color = MaterialTheme.colorScheme.onSurface.copy(alpha = 0.72f),
                    )
                }
                item {
                    TvSettingSectionTitle(stringResource(R.string.tv_settings_playback))
                }
                if (preferencesAvailable) {
                    item {
                        TvSettingRow(
                            stringResource(R.string.tv_settings_autoplay),
                            if (playbackPreferences.autoplayNext) stringResource(R.string.tv_settings_on)
                                else stringResource(R.string.tv_settings_off),
                            modifier = Modifier.focusRequester(rowFocus.getValue(TvSettingPanel.AUTOPLAY)),
                        ) { selectedPanel = TvSettingPanel.AUTOPLAY }
                    }
                    item {
                        TvSettingRow(
                            stringResource(R.string.tv_settings_subtitles),
                            displayTvLanguage(playbackPreferences.preferredSubtitleLanguage),
                            modifier = Modifier.focusRequester(rowFocus.getValue(TvSettingPanel.SUBTITLE)),
                        ) { selectedPanel = TvSettingPanel.SUBTITLE }
                    }
                    item {
                        TvSettingRow(
                            stringResource(R.string.tv_settings_audio),
                            displayTvLanguage(playbackPreferences.preferredAudioLanguage),
                            modifier = Modifier.focusRequester(rowFocus.getValue(TvSettingPanel.AUDIO)),
                        ) { selectedPanel = TvSettingPanel.AUDIO }
                    }
                    item {
                        TvSettingRow(
                            stringResource(R.string.tv_settings_speed),
                            "${playbackPreferences.defaultPlaybackSpeed}x",
                            modifier = Modifier.focusRequester(rowFocus.getValue(TvSettingPanel.SPEED)),
                        ) { selectedPanel = TvSettingPanel.SPEED }
                    }
                } else {
                    item {
                        Text(
                            error ?: stringResource(R.string.tv_settings_playback_unavailable),
                            color = MaterialTheme.colorScheme.onSurface.copy(alpha = 0.72f),
                        )
                    }
                }
                item {
                    TvSettingSectionTitle(stringResource(R.string.tv_settings_section_account))
                }
                item { TvSettingRow(stringResource(R.string.tv_profile_server_label), serverOrigin, onClick = onChangeServer) }
                if (onSwitchProfile != null) {
                    item {
                        TvSettingRow(stringResource(R.string.tv_profile_switch_profile), null, onClick = onSwitchProfile)
                    }
                }
                if (onSwitchAccount != null) {
                    item {
                        TvSettingRow(
                            stringResource(R.string.tv_profile_switch_account),
                            account.userName,
                            onClick = onSwitchAccount,
                        )
                    }
                }
                item { TvSettingRow(stringResource(R.string.tv_profile_sign_out), null, onClick = onSignOut) }
                item {
                    TvSettingSectionTitle(stringResource(R.string.tv_settings_section_info))
                }
                item {
                    TvUpdateSection(
                        currentVersionName = currentVersionName,
                        state = updateState,
                        checksEnabled = updateChecksEnabled,
                        canInstallPackages = canInstallPackages,
                        onToggleChecksEnabled = onToggleUpdateChecksEnabled,
                        onCheckNow = onCheckForUpdatesNow,
                        onStartDownload = onStartUpdateDownload,
                        onInstall = onInstallUpdate,
                    )
                }
            }
            if (selectedPanel != null && playbackPreferences != null) {
                TvSettingOptions(
                    selected = selectedPanel!!,
                    preferences = playbackPreferences,
                    accent = accent,
                    busy = busy,
                    error = error,
                    onSelect = onUpdatePlaybackPreferences,
                    onClose = { selectedPanel = null },
                    modifier = Modifier.weight(0.7f),
                )
            }
        }
    }
}

private enum class TvSettingPanel {
    AUTOPLAY, AUDIO, SUBTITLE, SPEED
}

@Composable
private fun TvSettingSectionTitle(title: String) {
    Text(title, style = MaterialTheme.typography.titleLarge)
}

@Composable
private fun TvSettingRow(
    label: String,
    value: String?,
    modifier: Modifier = Modifier,
    onClick: () -> Unit,
) {
    Button(onClick = onClick, modifier = modifier.fillMaxWidth()) {
        Row(
            modifier = Modifier.fillMaxWidth(),
            horizontalArrangement = Arrangement.SpaceBetween,
            verticalAlignment = Alignment.CenterVertically,
        ) {
            Text(
                label,
                modifier = Modifier.weight(1f),
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
            )
            value?.let {
                Text(
                    it,
                    modifier = Modifier.weight(0.9f),
                    color = MaterialTheme.colorScheme.onSurface.copy(alpha = 0.7f),
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis,
                )
            }
        }
    }
}

private fun displayTvLanguage(language: String?): String = when (language?.lowercase()) {
    null, "" -> "Auto"
    "off" -> "Off"
    "de" -> "Deutsch"
    "en" -> "English"
    "ja" -> "日本語"
    "es" -> "Español"
    "fr" -> "Français"
    "it" -> "Italiano"
    "ko" -> "한국어"
    else -> language
}

@Composable
private fun TvSettingOptions(
    selected: TvSettingPanel,
    preferences: ClientPlaybackPreferences,
    accent: Color,
    busy: Boolean,
    error: String?,
    onSelect: (ClientPlaybackPreferencesUpdate) -> Unit,
    onClose: () -> Unit,
    modifier: Modifier,
) {
    val context = LocalContext.current
    val playbackSpeeds = remember(context) { TvPlayerDesignLoader.load(context).playbackSpeeds }
    val options: List<Pair<String, ClientPlaybackPreferencesUpdate>> = when (selected) {
        TvSettingPanel.AUTOPLAY -> listOf(
            stringResource(R.string.tv_settings_on) to ClientPlaybackPreferencesUpdate(autoplayNext = true),
            stringResource(R.string.tv_settings_off) to ClientPlaybackPreferencesUpdate(autoplayNext = false),
        )
        TvSettingPanel.AUDIO -> tvLanguageOptions(includeOff = false).map { (language, label) ->
            label to ClientPlaybackPreferencesUpdate(preferredAudioLanguage = language)
        }
        TvSettingPanel.SUBTITLE -> tvLanguageOptions(includeOff = true).map { (language, label) ->
            label to ClientPlaybackPreferencesUpdate(preferredSubtitleLanguage = language)
        }
        TvSettingPanel.SPEED -> playbackSpeeds.map {
            "${it}x" to ClientPlaybackPreferencesUpdate(defaultPlaybackSpeed = it)
        }
    }
    val currentIndex = options.indexOfFirst { (_, value) ->
        when (selected) {
            TvSettingPanel.AUTOPLAY -> value.autoplayNext == preferences.autoplayNext
            TvSettingPanel.AUDIO -> value.preferredAudioLanguage == (preferences.preferredAudioLanguage ?: "")
            TvSettingPanel.SUBTITLE -> value.preferredSubtitleLanguage == (preferences.preferredSubtitleLanguage ?: "")
            TvSettingPanel.SPEED -> value.defaultPlaybackSpeed == preferences.defaultPlaybackSpeed
        }
    }.coerceAtLeast(0)
    val firstFocus = remember(selected) { FocusRequester() }
    val optionsState = rememberLazyListState()
    LaunchedEffect(selected, currentIndex) {
        optionsState.scrollToItem(currentIndex)
        withFrameNanos { }
        runCatching { firstFocus.requestFocus() }
    }

    Column(
        modifier = modifier
            .clip(RoundedCornerShape(18.dp))
            .background(MaterialTheme.colorScheme.surfaceVariant)
            .padding(18.dp),
        verticalArrangement = Arrangement.spacedBy(12.dp),
    ) {
        Text(
            stringResource(
                when (selected) {
                    TvSettingPanel.AUTOPLAY -> R.string.tv_settings_autoplay
                    TvSettingPanel.AUDIO -> R.string.tv_settings_audio
                    TvSettingPanel.SUBTITLE -> R.string.tv_settings_subtitles
                    TvSettingPanel.SPEED -> R.string.tv_settings_speed
                },
            ),
            style = MaterialTheme.typography.headlineSmall,
        )
        if (busy) Text(stringResource(R.string.tv_settings_saving))
        if (!busy && error != null) Text(error, color = MaterialTheme.colorScheme.error)
        LazyColumn(
            state = optionsState,
            verticalArrangement = Arrangement.spacedBy(8.dp),
            modifier = Modifier.weight(1f, fill = false),
        ) {
            items(options.size) { index ->
                val (title, value) = options[index]
                Button(
                    enabled = !busy,
                    onClick = { onSelect(value) },
                    modifier = Modifier.fillMaxWidth()
                        .then(if (index == currentIndex) Modifier.focusRequester(firstFocus) else Modifier),
                ) {
                    Text(if (index == currentIndex) "✓  $title" else title)
                }
            }
        }
        Button(onClick = onClose) { Text(stringResource(R.string.tv_action_back)) }
    }
}

@Composable
private fun tvLanguageOptions(includeOff: Boolean): List<Pair<String, String>> = buildList {
    add("" to "Auto")
    listOf("de", "en", "ja", "es", "fr", "it", "ko").forEach {
        add(it to displayTvLanguage(it))
    }
    if (includeOff) add("off" to "Off")
}
