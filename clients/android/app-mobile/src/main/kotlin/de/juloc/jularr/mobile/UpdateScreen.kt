package de.juloc.jularr.mobile

import androidx.activity.compose.BackHandler
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.material3.Button
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import de.juloc.jularr.BuildConfig
import de.juloc.jularr.R
import de.juloc.jularr.core.update.AppVariant
import de.juloc.jularr.core.update.UpdateCheckResult
import de.juloc.jularr.core.update.UpdateDownloadResult
import de.juloc.jularr.core.update.UpdateInstall
import de.juloc.jularr.core.update.UpdateManager
import de.juloc.jularr.core.update.UpdatePreferences
import de.juloc.jularr.mobile.offline.OfflineStoragePolicy
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import java.io.File

/** FileProvider authority declared for app-mobile in AndroidManifest.xml. */
internal const val UpdateFileProviderAuthority = "de.juloc.jularr.fileprovider"

internal sealed interface UpdateScreenState {
    data object Checking : UpdateScreenState
    data object UpToDate : UpdateScreenState
    data class Available(val info: UpdateCheckResult.UpdateAvailable) : UpdateScreenState
    data class Downloading(val bytesRead: Long, val totalBytes: Long) : UpdateScreenState
    data class ReadyToInstall(val info: UpdateCheckResult.UpdateAvailable, val apkFile: File) : UpdateScreenState
    data class Failed(val message: String) : UpdateScreenState
}

/**
 * Native "Check for updates" screen (#490): queries the public, unauthenticated GitHub
 * Releases API for the configured Jularr repository, downloads and checksum-verifies
 * the phone APK asset, then hands it to Android's own package installer. This is a
 * device-only concern (like [TtsSettingsScreen]'s offline model manager), so it lives
 * natively instead of inside the WebView shell.
 */
@Composable
fun UpdateScreen(
    initialResult: UpdateCheckResult?,
    onClose: () -> Unit,
) {
    val context = LocalContext.current
    val scope = rememberCoroutineScope()
    val manager = remember { UpdateManager() }
    val preferences = remember { UpdatePreferences(context.applicationContext) }
    val checksumMismatchMessage = stringResource(R.string.update_failed_checksum)

    var checksEnabled by remember { mutableStateOf(preferences.checksEnabled) }
    var state by remember {
        mutableStateOf<UpdateScreenState>(initialResult.toScreenState() ?: UpdateScreenState.Checking)
    }

    fun checkNow() {
        scope.launch {
            state = UpdateScreenState.Checking
            preferences.lastCheckedAtMillis = System.currentTimeMillis()
            val result = withContext(Dispatchers.IO) {
                manager.checkForUpdate(BuildConfig.VERSION_NAME, AppVariant.Mobile)
            }
            state = result.toScreenState() ?: UpdateScreenState.UpToDate
        }
    }

    LaunchedEffect(Unit) {
        if (initialResult == null) {
            checkNow()
        }
    }

    fun installFile(apkFile: File) {
        context.startActivity(UpdateInstall.installIntent(context, apkFile, UpdateFileProviderAuthority))
    }

    val requestInstallPermission = rememberLauncherForActivityResult(
        contract = ActivityResultContracts.StartActivityForResult(),
    ) {
        // Returning from "allow unknown sources" does not itself confirm the grant; the
        // user taps Install again below, which re-checks canInstallPackages().
    }

    fun startInstall(ready: UpdateScreenState.ReadyToInstall) {
        if (UpdateInstall.canInstallPackages(context)) {
            installFile(ready.apkFile)
        } else {
            requestInstallPermission.launch(UpdateInstall.manageUnknownAppSourcesIntent(context))
        }
    }

    fun startDownload(info: UpdateCheckResult.UpdateAvailable) {
        scope.launch {
            state = UpdateScreenState.Downloading(0L, info.apkAsset.sizeBytes)
            val destination = File(context.cacheDir, "updates/${info.apkAsset.name}")
            val result = withContext(Dispatchers.IO) {
                manager.downloadAndVerify(info, destination) { bytesRead, totalBytes ->
                    state = UpdateScreenState.Downloading(bytesRead, totalBytes)
                }
            }
            state = when (result) {
                is UpdateDownloadResult.Success -> UpdateScreenState.ReadyToInstall(info, result.file)
                UpdateDownloadResult.ChecksumMismatch ->
                    UpdateScreenState.Failed(checksumMismatchMessage)
                is UpdateDownloadResult.Failed -> UpdateScreenState.Failed(result.message)
            }
        }
    }

    BackHandler(onBack = onClose)

    Surface(modifier = Modifier.fillMaxSize()) {
        Column(modifier = Modifier.padding(20.dp)) {
            Row(
                modifier = Modifier.fillMaxWidth(),
                horizontalArrangement = Arrangement.SpaceBetween,
            ) {
                Text(stringResource(R.string.update_screen_title), style = MaterialTheme.typography.headlineSmall)
                TextButton(onClick = onClose) { Text(stringResource(R.string.update_action_close)) }
            }

            Text(
                stringResource(R.string.update_current_version, BuildConfig.VERSION_NAME),
                style = MaterialTheme.typography.bodyMedium,
                modifier = Modifier.padding(top = 8.dp),
            )

            Row(
                modifier = Modifier
                    .fillMaxWidth()
                    .padding(top = 16.dp),
                horizontalArrangement = Arrangement.SpaceBetween,
            ) {
                Text(stringResource(R.string.update_toggle_automatic_checks), style = MaterialTheme.typography.bodyMedium)
                Switch(
                    checked = checksEnabled,
                    onCheckedChange = {
                        checksEnabled = it
                        preferences.checksEnabled = it
                    },
                )
            }

            when (val current = state) {
                UpdateScreenState.Checking -> {
                    Row(
                        modifier = Modifier.padding(top = 20.dp),
                        verticalAlignment = Alignment.CenterVertically,
                        horizontalArrangement = Arrangement.spacedBy(12.dp),
                    ) {
                        CircularProgressIndicator(modifier = Modifier.size(20.dp))
                        Text(stringResource(R.string.update_status_checking))
                    }
                }

                UpdateScreenState.UpToDate -> {
                    Text(
                        stringResource(R.string.update_status_up_to_date),
                        modifier = Modifier.padding(top = 20.dp),
                    )
                    Button(onClick = ::checkNow, modifier = Modifier.padding(top = 12.dp)) {
                        Text(stringResource(R.string.update_action_check_now))
                    }
                }

                is UpdateScreenState.Available -> {
                    Text(
                        stringResource(R.string.update_available_title, current.info.version),
                        style = MaterialTheme.typography.titleMedium,
                        modifier = Modifier.padding(top = 20.dp),
                    )
                    current.info.apkAsset.sizeBytes.takeIf { it > 0 }?.let { size ->
                        Text(
                            stringResource(R.string.update_available_size, OfflineStoragePolicy.formatBytes(size)),
                            style = MaterialTheme.typography.bodySmall,
                        )
                    }
                    current.info.release.body?.takeIf { it.isNotBlank() }?.let { notes ->
                        Text(
                            stringResource(R.string.update_notes_label),
                            style = MaterialTheme.typography.labelLarge,
                            modifier = Modifier.padding(top = 12.dp),
                        )
                        Text(notes, style = MaterialTheme.typography.bodySmall)
                    }
                    Row(
                        modifier = Modifier.padding(top = 16.dp),
                        horizontalArrangement = Arrangement.spacedBy(8.dp),
                    ) {
                        Button(onClick = { startDownload(current.info) }) {
                            Text(stringResource(R.string.update_action_update_now))
                        }
                    }
                }

                is UpdateScreenState.Downloading -> {
                    Text(
                        if (current.totalBytes > 0) {
                            stringResource(
                                R.string.update_downloading,
                                ((current.bytesRead * 100) / current.totalBytes).toInt(),
                            )
                        } else {
                            stringResource(R.string.update_downloading_indeterminate)
                        },
                        modifier = Modifier.padding(top = 20.dp),
                    )
                    if (current.totalBytes > 0) {
                        LinearProgressIndicator(
                            progress = { (current.bytesRead.toFloat() / current.totalBytes).coerceIn(0f, 1f) },
                            modifier = Modifier
                                .fillMaxWidth()
                                .padding(top = 8.dp),
                        )
                    } else {
                        LinearProgressIndicator(modifier = Modifier.fillMaxWidth().padding(top = 8.dp))
                    }
                }

                is UpdateScreenState.ReadyToInstall -> {
                    Text(
                        stringResource(R.string.update_ready_title),
                        style = MaterialTheme.typography.titleMedium,
                        modifier = Modifier.padding(top = 20.dp),
                    )
                    Text(
                        stringResource(R.string.update_ready_body, current.info.version),
                        style = MaterialTheme.typography.bodyMedium,
                    )
                    if (!UpdateInstall.canInstallPackages(context)) {
                        Text(
                            stringResource(R.string.update_permission_required),
                            style = MaterialTheme.typography.bodySmall,
                            color = MaterialTheme.colorScheme.error,
                            modifier = Modifier.padding(top = 8.dp),
                        )
                    }
                    Button(
                        onClick = { startInstall(current) },
                        modifier = Modifier.padding(top = 12.dp),
                    ) {
                        Text(stringResource(R.string.update_action_install))
                    }
                }

                is UpdateScreenState.Failed -> {
                    Text(
                        stringResource(R.string.update_status_error, current.message),
                        color = MaterialTheme.colorScheme.error,
                        modifier = Modifier.padding(top = 20.dp),
                    )
                    Button(onClick = ::checkNow, modifier = Modifier.padding(top = 12.dp)) {
                        Text(stringResource(R.string.update_action_retry))
                    }
                }
            }
        }
    }
}

/**
 * Small, non-blocking prompt shown over the WebView shell when an automatic app-start
 * check finds an update the user has not already dismissed. "Later" never nags again
 * for that exact version (see [UpdatePreferences.dismissedVersion]).
 */
@Composable
fun UpdatePromptCard(
    info: UpdateCheckResult.UpdateAvailable,
    onUpdateNow: () -> Unit,
    onLater: () -> Unit,
    modifier: Modifier = Modifier,
) {
    Surface(
        modifier = modifier,
        tonalElevation = 6.dp,
        shape = MaterialTheme.shapes.medium,
    ) {
        Column(modifier = Modifier.padding(16.dp)) {
            Text(
                stringResource(R.string.update_available_title, info.version),
                style = MaterialTheme.typography.titleMedium,
            )
            Row(
                modifier = Modifier
                    .fillMaxWidth()
                    .padding(top = 12.dp),
                horizontalArrangement = Arrangement.End,
            ) {
                TextButton(onClick = onLater) { Text(stringResource(R.string.update_action_later)) }
                Button(onClick = onUpdateNow, modifier = Modifier.padding(start = 8.dp)) {
                    Text(stringResource(R.string.update_action_update_now))
                }
            }
        }
    }
}

private fun UpdateCheckResult?.toScreenState(): UpdateScreenState? = when (this) {
    null -> null
    UpdateCheckResult.UpToDate -> UpdateScreenState.UpToDate
    is UpdateCheckResult.Error -> UpdateScreenState.Failed(message)
    is UpdateCheckResult.UpdateAvailable -> UpdateScreenState.Available(this)
}
