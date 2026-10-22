package de.juloc.jularr.tv

import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.platform.LocalContext
import de.juloc.jularr.core.update.AppVariant
import de.juloc.jularr.core.update.UpdateCheckResult
import de.juloc.jularr.core.update.UpdateDownloadResult
import de.juloc.jularr.core.update.UpdateInstall
import de.juloc.jularr.core.update.UpdateManager
import de.juloc.jularr.core.update.UpdatePreferences
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import java.io.File

/**
 * Runs the ORIGINAL TvProfileScreen/settings, including the original self-update actions.
 * A V1 device token does not imply a legacy ClientAccount: account remains null.
 */
@Composable
internal fun TvV1SettingsHost(
    serverOrigin: String,
    error: String?,
    onSwitchProfile: () -> Unit,
    onSignOut: () -> Unit,
    onChangeServer: () -> Unit,
) {
    val context = LocalContext.current
    val scope = rememberCoroutineScope()
    val updates = remember { UpdateManager() }
    val preferences = remember { UpdatePreferences(context.applicationContext) }
    var checksEnabled by remember { mutableStateOf(preferences.checksEnabled) }
    var updateState by remember { mutableStateOf<TvUpdateState>(TvUpdateState.Idle) }
    val installPermission = rememberLauncherForActivityResult(
        ActivityResultContracts.StartActivityForResult(),
    ) { /* Installation permission is checked again after the user returns. */ }

    fun checkUpdates() {
        scope.launch {
            updateState = TvUpdateState.Checking
            preferences.lastCheckedAtMillis = System.currentTimeMillis()
            updateState = try {
                when (val result = withContext(Dispatchers.IO) {
                    updates.checkForUpdate(BuildConfig.VERSION_NAME, AppVariant.Tv)
                }) {
                    UpdateCheckResult.UpToDate -> TvUpdateState.UpToDate
                    is UpdateCheckResult.Error -> TvUpdateState.Failed(result.message)
                    is UpdateCheckResult.UpdateAvailable -> TvUpdateState.Available(result)
                }
            } catch (failure: Exception) {
                TvUpdateState.Failed(failure.message ?: "Updateprüfung fehlgeschlagen.")
            }
        }
    }

    fun download(info: UpdateCheckResult.UpdateAvailable) {
        scope.launch {
            updateState = TvUpdateState.Downloading(0L, info.apkAsset.sizeBytes)
            val destination = File(context.cacheDir, "updates/${info.apkAsset.name}")
            updateState = try {
                when (val result = withContext(Dispatchers.IO) {
                    updates.downloadAndVerify(info, destination) { bytesRead, totalBytes ->
                        updateState = TvUpdateState.Downloading(bytesRead, totalBytes)
                    }
                }) {
                    is UpdateDownloadResult.Success -> TvUpdateState.ReadyToInstall(info, result.file)
                    UpdateDownloadResult.ChecksumMismatch -> TvUpdateState.Failed("Update-Checksumme stimmt nicht.")
                    is UpdateDownloadResult.Failed -> TvUpdateState.Failed(result.message)
                }
            } catch (failure: Exception) {
                TvUpdateState.Failed(failure.message ?: "Update-Download fehlgeschlagen.")
            }
        }
    }

    fun install() {
        val ready = updateState as? TvUpdateState.ReadyToInstall ?: return
        if (UpdateInstall.canInstallPackages(context)) {
            context.startActivity(UpdateInstall.installIntent(
                context, ready.apkFile, TvUpdateFileProviderAuthority,
            ))
        } else {
            installPermission.launch(UpdateInstall.manageUnknownAppSourcesIntent(context))
        }
    }

    TvProfileScreen(
        account = null,
        serverOrigin = serverOrigin,
        currentVersionName = BuildConfig.VERSION_NAME,
        updateState = updateState,
        updateChecksEnabled = checksEnabled,
        canInstallPackages = UpdateInstall.canInstallPackages(context),
        onToggleUpdateChecksEnabled = {
            checksEnabled = !checksEnabled
            preferences.checksEnabled = checksEnabled
        },
        onCheckForUpdatesNow = ::checkUpdates,
        onStartUpdateDownload = ::download,
        onInstallUpdate = ::install,
        onSignOut = onSignOut,
        onChangeServer = onChangeServer,
        onSwitchProfile = onSwitchProfile,
        error = error,
    )
}
