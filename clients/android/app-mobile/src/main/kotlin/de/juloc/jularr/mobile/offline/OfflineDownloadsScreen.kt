package de.juloc.jularr.mobile.offline

import androidx.activity.compose.BackHandler
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Surface
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import de.juloc.jularr.core.api.JularrOfflineApi
import kotlinx.coroutines.launch

/**
 * Downloads management: storage usage against the device limit, per-episode
 * state with pause/resume/cancel/remove, and playback of ready episodes.
 */
@Composable
fun OfflineDownloadsScreen(
    origin: String,
    downloads: OfflineDownloads,
    api: JularrOfflineApi,
    serverReachable: Boolean,
    onPlay: (String) -> Unit,
    onClose: () -> Unit,
) {
    val snapshot by downloads.state.collectAsState()
    val scope = rememberCoroutineScope()
    var message by remember { mutableStateOf<String?>(null) }
    var pendingRemoval by remember { mutableStateOf<OfflineDownload?>(null) }
    var limitMenuOpen by remember { mutableStateOf(false) }

    val account = snapshot.account?.takeIf { it.origin == origin }
    val visible = snapshot.accessibleDownloads(origin)
        .sortedWith(compareBy({ it.animeTitle }, { it.seasonNumber }, { it.episodeNumber }))
    val usedBytes = snapshot.downloads.sumOf { it.bytesOnDevice }
    val limitBytes = snapshot.settings.limitBytes
    val freeBytes = remember(snapshot) { downloads.store.root.usableSpace }

    BackHandler(onBack = onClose)

    Surface(modifier = Modifier.fillMaxSize()) {
        Column(
            modifier = Modifier
                .fillMaxSize()
                .padding(16.dp),
            verticalArrangement = Arrangement.spacedBy(12.dp),
        ) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Text(
                    text = "Downloads",
                    style = MaterialTheme.typography.headlineSmall,
                    modifier = Modifier.weight(1f),
                )
                TextButton(onClick = onClose) {
                    Text("Close")
                }
            }

            Text(
                text = "${OfflineStoragePolicy.formatBytes(usedBytes)} of " +
                    "${OfflineStoragePolicy.formatBytes(limitBytes)} used · " +
                    "${OfflineStoragePolicy.formatBytes(freeBytes)} free on this device",
                style = MaterialTheme.typography.bodyMedium,
            )
            LinearProgressIndicator(
                progress = {
                    if (limitBytes <= 0) 0f else (usedBytes.toFloat() / limitBytes).coerceIn(0f, 1f)
                },
                modifier = Modifier.fillMaxWidth(),
            )

            Row(verticalAlignment = Alignment.CenterVertically) {
                Text(
                    text = "Storage limit",
                    modifier = Modifier.weight(1f),
                )
                Box {
                    TextButton(onClick = { limitMenuOpen = true }) {
                        Text(OfflineStoragePolicy.formatBytes(limitBytes))
                    }
                    DropdownMenu(
                        expanded = limitMenuOpen,
                        onDismissRequest = { limitMenuOpen = false },
                    ) {
                        OfflineStoragePolicy.LimitChoices.forEach { choice ->
                            DropdownMenuItem(
                                text = { Text(OfflineStoragePolicy.formatBytes(choice)) },
                                onClick = {
                                    downloads.setLimit(choice)
                                    limitMenuOpen = false
                                },
                            )
                        }
                    }
                }
            }

            Row(verticalAlignment = Alignment.CenterVertically) {
                Text(
                    text = "Download over Wi-Fi only",
                    modifier = Modifier.weight(1f),
                )
                Switch(
                    checked = snapshot.settings.wifiOnly,
                    onCheckedChange = downloads::setWifiOnly,
                )
            }

            if (usedBytes > limitBytes) {
                Text(
                    text = "Downloads use more than the limit. New downloads start after you remove episodes.",
                    color = MaterialTheme.colorScheme.error,
                    style = MaterialTheme.typography.bodySmall,
                )
            }

            message?.let {
                Text(
                    text = it,
                    color = MaterialTheme.colorScheme.error,
                    style = MaterialTheme.typography.bodySmall,
                )
            }

            HorizontalDivider()

            when {
                account == null || !account.signedIn -> Text(
                    text = "Sign in to Jularr to use this account's downloads.",
                    style = MaterialTheme.typography.bodyMedium,
                )

                visible.isEmpty() -> Text(
                    text = "No downloaded episodes. Use Download in the player to keep an episode on this device.",
                    style = MaterialTheme.typography.bodyMedium,
                )

                else -> LazyColumn(modifier = Modifier.fillMaxWidth()) {
                    items(visible, key = { it.episodeId }) { download ->
                        DownloadRow(
                            download = download,
                            wifiOnly = snapshot.settings.wifiOnly,
                            serverReachable = serverReachable,
                            onPlay = { onPlay(download.episodeId) },
                            onPause = { downloads.pause(download) },
                            onResume = { downloads.resume(download) },
                            onRetry = {
                                scope.launch {
                                    val result = downloads.enqueue(origin, download.episodeId, api)
                                    message = (result as? EnqueueResult.Rejected)?.message
                                }
                            },
                            onRemove = {
                                if (download.state == DownloadState.READY) {
                                    pendingRemoval = download
                                } else {
                                    downloads.remove(download)
                                }
                            },
                        )
                        HorizontalDivider()
                    }
                }
            }
        }
    }

    pendingRemoval?.let { download ->
        AlertDialog(
            onDismissRequest = { pendingRemoval = null },
            title = { Text("Remove download?") },
            text = {
                Text("${download.animeTitle} · E${download.episodeNumber} is deleted from this device. Your progress is kept.")
            },
            confirmButton = {
                TextButton(
                    onClick = {
                        downloads.remove(download)
                        pendingRemoval = null
                    },
                ) {
                    Text("Remove")
                }
            },
            dismissButton = {
                TextButton(onClick = { pendingRemoval = null }) {
                    Text("Keep")
                }
            },
        )
    }
}

@Composable
private fun DownloadRow(
    download: OfflineDownload,
    wifiOnly: Boolean,
    serverReachable: Boolean,
    onPlay: () -> Unit,
    onPause: () -> Unit,
    onResume: () -> Unit,
    onRetry: () -> Unit,
    onRemove: () -> Unit,
) {
    Column(
        modifier = Modifier
            .fillMaxWidth()
            .padding(vertical = 12.dp),
        verticalArrangement = Arrangement.spacedBy(4.dp),
    ) {
        Text(
            text = download.animeTitle,
            style = MaterialTheme.typography.titleMedium,
        )
        Text(
            text = "S${download.seasonNumber} · E${download.episodeNumber} · ${download.episodeTitle}",
            style = MaterialTheme.typography.bodyMedium,
        )
        Text(
            text = statusLine(download, wifiOnly),
            style = MaterialTheme.typography.bodySmall,
            color = if (download.state == DownloadState.FAILED) {
                MaterialTheme.colorScheme.error
            } else {
                MaterialTheme.colorScheme.onSurfaceVariant
            },
        )

        if (download.state == DownloadState.DOWNLOADING || download.state == DownloadState.PAUSED) {
            LinearProgressIndicator(
                progress = { download.progressPercent / 100f },
                modifier = Modifier.fillMaxWidth(),
            )
        }

        Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            when (download.state) {
                DownloadState.READY -> OutlinedButton(onClick = onPlay) { Text("Play") }
                DownloadState.QUEUED,
                DownloadState.DOWNLOADING,
                -> OutlinedButton(onClick = onPause) { Text("Pause") }
                DownloadState.PAUSED -> OutlinedButton(onClick = onResume) { Text("Resume") }
                DownloadState.FAILED -> OutlinedButton(onClick = onRetry, enabled = serverReachable) { Text("Retry") }
            }
            TextButton(onClick = onRemove) {
                Text(if (download.state == DownloadState.READY || download.state == DownloadState.FAILED) "Remove" else "Cancel")
            }
        }
    }
}

private fun statusLine(download: OfflineDownload, wifiOnly: Boolean): String {
    val size = OfflineStoragePolicy.formatBytes(download.sizeBytes)
    return when (download.state) {
        DownloadState.QUEUED -> if (wifiOnly) "Queued · $size · Wi-Fi only" else "Queued · $size"
        DownloadState.DOWNLOADING ->
            "Downloading ${download.progressPercent}% · " +
                "${OfflineStoragePolicy.formatBytes(download.downloadedBytes)} of $size"
        DownloadState.PAUSED -> "Paused at ${download.progressPercent}% · $size"
        DownloadState.READY -> if (download.watched) "Downloaded · $size · Watched" else "Downloaded · $size"
        DownloadState.FAILED -> download.failure ?: "Download failed"
    }
}
