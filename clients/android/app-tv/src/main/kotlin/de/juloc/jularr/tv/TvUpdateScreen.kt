package de.juloc.jularr.tv

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import androidx.tv.material3.Button
import androidx.tv.material3.MaterialTheme
import androidx.tv.material3.Text
import de.juloc.jularr.core.update.UpdateCheckResult
import java.io.File

/** FileProvider authority declared for app-tv in AndroidManifest.xml. */
internal const val TvUpdateFileProviderAuthority = "de.juloc.jularr.tv.fileprovider"

/**
 * Local UI state for the TV self-update flow (#490), layered on top of
 * [UpdateCheckResult]/[de.juloc.jularr.core.update.UpdateDownloadResult] from the
 * shared `core-update` module (the same one app-mobile drives).
 */
sealed interface TvUpdateState {
    data object Idle : TvUpdateState
    data object Checking : TvUpdateState
    data object UpToDate : TvUpdateState
    data class Available(val info: UpdateCheckResult.UpdateAvailable) : TvUpdateState
    data class Downloading(val bytesRead: Long, val totalBytes: Long) : TvUpdateState
    data class ReadyToInstall(val info: UpdateCheckResult.UpdateAvailable, val apkFile: File) : TvUpdateState
    data class Failed(val message: String) : TvUpdateState
}

/**
 * The Profile/Settings destination's Updates panel: current version, the automatic-check
 * toggle, a manual check, and (when relevant) download progress / install. Every action
 * is a single focusable [Button] so the whole flow works with D-pad + OK only.
 */
@Composable
internal fun TvUpdateSection(
    currentVersionName: String,
    state: TvUpdateState,
    checksEnabled: Boolean,
    canInstallPackages: Boolean,
    onToggleChecksEnabled: () -> Unit,
    onCheckNow: () -> Unit,
    onStartDownload: (UpdateCheckResult.UpdateAvailable) -> Unit,
    onInstall: () -> Unit,
) {
    Column(verticalArrangement = Arrangement.spacedBy(10.dp)) {
        Text(stringResource(R.string.tv_update_section_title), style = MaterialTheme.typography.titleLarge)
        Text(
            stringResource(R.string.tv_update_current_version, currentVersionName),
            style = MaterialTheme.typography.bodyMedium,
        )

        Row(horizontalArrangement = Arrangement.spacedBy(12.dp)) {
            Button(onClick = onToggleChecksEnabled) {
                Text(
                    stringResource(R.string.tv_update_toggle_automatic_checks) +
                        if (checksEnabled) " ✓" else "",
                )
            }
            Button(onClick = onCheckNow) {
                Text(stringResource(R.string.tv_update_action_check_now))
            }
        }

        when (state) {
            TvUpdateState.Idle -> Unit

            TvUpdateState.Checking -> Text(stringResource(R.string.tv_update_status_checking))

            TvUpdateState.UpToDate -> Text(stringResource(R.string.tv_update_status_up_to_date))

            is TvUpdateState.Available -> Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
                Text(
                    stringResource(R.string.tv_update_available_title, state.info.version),
                    style = MaterialTheme.typography.titleMedium,
                )
                state.info.apkAsset.sizeBytes.takeIf { it > 0 }?.let { size ->
                    Text(stringResource(R.string.tv_update_available_size, formatMegabytes(size)))
                }
                Button(onClick = { onStartDownload(state.info) }) {
                    Text(stringResource(R.string.tv_update_action_update_now))
                }
            }

            is TvUpdateState.Downloading -> Text(
                if (state.totalBytes > 0) {
                    stringResource(
                        R.string.tv_update_downloading,
                        ((state.bytesRead * 100) / state.totalBytes).toInt(),
                    )
                } else {
                    stringResource(R.string.tv_update_downloading_indeterminate)
                },
            )

            is TvUpdateState.ReadyToInstall -> Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
                Text(stringResource(R.string.tv_update_ready_title), style = MaterialTheme.typography.titleMedium)
                Text(stringResource(R.string.tv_update_ready_body, state.info.version))
                if (!canInstallPackages) {
                    Text(
                        stringResource(R.string.tv_update_permission_required),
                        color = MaterialTheme.colorScheme.error,
                    )
                }
                Button(onClick = onInstall) {
                    Text(stringResource(R.string.tv_update_action_install))
                }
            }

            is TvUpdateState.Failed -> Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
                Text(
                    stringResource(R.string.tv_update_status_error, state.message),
                    color = MaterialTheme.colorScheme.error,
                )
                Button(onClick = onCheckNow) {
                    Text(stringResource(R.string.tv_update_action_retry))
                }
            }
        }
    }
}

/**
 * Small, focusable, Back-safe overlay shown on Home/Watchlist/Activity/Profile when an
 * automatic app-start check finds an update the user has not already dismissed. Never
 * shown over Setup/Login/Player so it cannot interrupt those flows.
 */
@Composable
internal fun TvUpdatePromptOverlay(
    info: UpdateCheckResult.UpdateAvailable,
    onUpdateNow: () -> Unit,
    onLater: () -> Unit,
    modifier: Modifier = Modifier,
) {
    Column(
        modifier = modifier
            .fillMaxWidth(0.42f)
            .background(MaterialTheme.colorScheme.surface.copy(alpha = 0.98f))
            .padding(24.dp),
        verticalArrangement = Arrangement.spacedBy(12.dp),
    ) {
        Text(
            stringResource(R.string.tv_update_available_title, info.version),
            style = MaterialTheme.typography.titleLarge,
        )
        Row(horizontalArrangement = Arrangement.spacedBy(12.dp)) {
            Button(onClick = onUpdateNow) {
                Text(stringResource(R.string.tv_update_action_update_now))
            }
            Button(onClick = onLater) {
                Text(stringResource(R.string.tv_update_action_later))
            }
        }
    }
}

private fun formatMegabytes(bytes: Long): String = "%.1f MB".format(bytes / (1024.0 * 1024.0))
