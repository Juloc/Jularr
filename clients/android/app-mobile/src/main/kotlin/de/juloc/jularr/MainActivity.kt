package de.juloc.jularr

import android.content.Intent
import android.os.Bundle
import android.webkit.CookieManager
import android.webkit.WebView
import androidx.activity.ComponentActivity
import androidx.activity.compose.BackHandler
import androidx.activity.compose.setContent
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.safeDrawing
import androidx.compose.foundation.layout.windowInsetsPadding
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.unit.dp
import de.juloc.jularr.core.api.HttpJularrClientApi
import de.juloc.jularr.core.update.AppVariant
import de.juloc.jularr.core.update.UpdateCheckResult
import de.juloc.jularr.core.update.UpdateManager
import de.juloc.jularr.core.update.UpdatePreferences
import de.juloc.jularr.mobile.JularrWebShell
import de.juloc.jularr.mobile.MobileCompatibilityGate
import de.juloc.jularr.mobile.MobileCompatibilityState
import de.juloc.jularr.mobile.NativePlayerScreen
import de.juloc.jularr.mobile.ServerOrigin
import de.juloc.jularr.mobile.ServerSettings
import de.juloc.jularr.mobile.TtsSettingsScreen
import de.juloc.jularr.mobile.UpdatePromptCard
import de.juloc.jularr.mobile.UpdateScreen
import de.juloc.jularr.mobile.WebSession
import de.juloc.jularr.mobile.offline.OfflineDownloads
import de.juloc.jularr.mobile.offline.OfflineDownloadsScreen
import de.juloc.jularr.mobile.offline.OfflineNotifications
import de.juloc.jularr.mobile.offline.library.LibraryDownloads
import de.juloc.jularr.mobile.offline.library.LibraryDownloadsScreen
import de.juloc.jularr.mobile.offline.library.LibraryNotifications
import de.juloc.jularr.mobile.offline.library.LibrarySaveOfflineOverlay
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.delay
import kotlinx.coroutines.withContext

class MainActivity : ComponentActivity() {
    private var webView: WebView? = null
    private var activeOrigin: String? = null
    private var restoredWebState: Bundle? = null
    private var restoredWebOrigin: String? = null
    private val downloadsRequest = mutableIntStateOf(0)
    private val libraryDownloadsRequest = mutableIntStateOf(0)
    private val updateCheckRequest = mutableIntStateOf(0)

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)

        restoredWebState = savedInstanceState?.getBundle(KeyWebViewState)
        restoredWebOrigin = savedInstanceState?.getString(KeyWebViewOrigin)
        if (savedInstanceState == null) {
            handleIntent(intent)
        }

        setContent {
            MaterialTheme {
                JularrMobileApp(
                    restoredWebState = restoredWebState,
                    restoredWebOrigin = restoredWebOrigin,
                    downloadsRequest = downloadsRequest.intValue,
                    libraryDownloadsRequest = libraryDownloadsRequest.intValue,
                    updateCheckRequest = updateCheckRequest.intValue,
                    onWebViewChanged = { webView = it },
                    onOriginChanged = { activeOrigin = it },
                    onFinish = ::finish,
                )
            }
        }
    }

    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        handleIntent(intent)
    }

    private fun handleIntent(intent: Intent?) {
        if (intent?.action == OfflineNotifications.ActionOpenDownloads) {
            downloadsRequest.intValue += 1
        }
        if (intent?.action == LibraryNotifications.ActionOpenLibraryDownloads) {
            libraryDownloadsRequest.intValue += 1
        }
        if (intent?.action == ActionCheckUpdates) {
            updateCheckRequest.intValue += 1
        }
    }

    override fun onSaveInstanceState(outState: Bundle) {
        val state = Bundle()
        if (webView?.saveState(state) != null) {
            outState.putBundle(KeyWebViewState, state)
            activeOrigin?.let { outState.putString(KeyWebViewOrigin, it) }
        }
        super.onSaveInstanceState(outState)
    }

    private companion object {
        const val KeyWebViewState = "jularr.webview.state"
        const val KeyWebViewOrigin = "jularr.webview.origin"
        // Matches the "Check for updates" launcher shortcut in res/xml/shortcuts.xml (#490).
        const val ActionCheckUpdates = "de.juloc.jularr.action.CHECK_UPDATES"
    }
}

private sealed interface ServerGateState {
    data object Checking : ServerGateState
    data object Compatible : ServerGateState
    data class UpdateRequired(val detail: String) : ServerGateState
    data class Unavailable(val detail: String) : ServerGateState
}

@Composable
private fun JularrMobileApp(
    restoredWebState: Bundle?,
    restoredWebOrigin: String?,
    downloadsRequest: Int,
    libraryDownloadsRequest: Int,
    updateCheckRequest: Int,
    onWebViewChanged: (WebView?) -> Unit,
    onOriginChanged: (String?) -> Unit,
    onFinish: () -> Unit,
) {
    val context = LocalContext.current
    val settings = remember { ServerSettings(context.applicationContext) }
    val offline = remember { OfflineDownloads.get(context.applicationContext) }
    val offlineSnapshot by offline.state.collectAsState()
    val library = remember { LibraryDownloads.get(context.applicationContext) }
    val librarySnapshot by library.state.collectAsState()
    val updatePreferences = remember { UpdatePreferences(context.applicationContext) }
    var originValue by rememberSaveable { mutableStateOf(settings.getOrigin()) }
    var activeEpisodeId by rememberSaveable { mutableStateOf<String?>(null) }
    var showDownloads by rememberSaveable { mutableStateOf(false) }
    var showLibraryDownloads by rememberSaveable { mutableStateOf(false) }
    var currentWorkId by remember { mutableStateOf<String?>(null) }
    var showTtsSettings by rememberSaveable { mutableStateOf(false) }
    var showUpdateScreen by rememberSaveable { mutableStateOf(false) }
    var updatePromptInfo by remember { mutableStateOf<UpdateCheckResult.UpdateAvailable?>(null) }
    var confirmServerChange by remember { mutableStateOf(false) }
    var accountCheck by remember { mutableIntStateOf(0) }
    var securityError by remember { mutableStateOf<String?>(null) }
    var gateRetry by remember { mutableStateOf(0) }
    var currentWebView by remember { mutableStateOf<WebView?>(null) }

    LaunchedEffect(downloadsRequest) {
        if (downloadsRequest > 0) {
            activeEpisodeId = null
            showDownloads = true
        }
    }

    LaunchedEffect(libraryDownloadsRequest) {
        if (libraryDownloadsRequest > 0) {
            activeEpisodeId = null
            showUpdateScreen = false
            showLibraryDownloads = true
        }
    }

    LaunchedEffect(updateCheckRequest) {
        if (updateCheckRequest > 0) {
            updatePromptInfo = null
            showUpdateScreen = true
        }
    }

    // Automatic check (#490): only when enabled and the last check is stale; never
    // blocks normal app startup, and a network/API failure here is silently ignored
    // (the user can always use the manual "Check for updates" shortcut/screen instead).
    LaunchedEffect(Unit) {
        if (!updatePreferences.checksEnabled ||
            !UpdatePreferences.isCheckDue(updatePreferences.lastCheckedAtMillis)
        ) {
            return@LaunchedEffect
        }

        updatePreferences.lastCheckedAtMillis = System.currentTimeMillis()
        val result = try {
            withContext(Dispatchers.IO) {
                UpdateManager().checkForUpdate(BuildConfig.VERSION_NAME, AppVariant.Mobile)
            }
        } catch (exception: Exception) {
            null
        }

        if (result is UpdateCheckResult.UpdateAvailable && updatePreferences.shouldPrompt(result.version)) {
            updatePromptInfo = result
        }
    }

    fun changeServer() {
        // Downloads belong to one account on one server; changing the server removes them.
        offline.clearAll()
        library.clearAll()
        CookieManager.getInstance().removeAllCookies(null)
        CookieManager.getInstance().flush()
        settings.clearOrigin()
        showDownloads = false
        showLibraryDownloads = false
        activeEpisodeId = null
        originValue = null
    }

    fun requestServerChange() {
        if (offlineSnapshot.downloads.isEmpty() && librarySnapshot.books.isEmpty()) {
            changeServer()
        } else {
            confirmServerChange = true
        }
    }

    if (confirmServerChange) {
        AlertDialog(
            onDismissRequest = { confirmServerChange = false },
            title = { Text("Change server?") },
            text = {
                Text(
                    "All ${offlineSnapshot.downloads.size} downloaded episodes and " +
                        "${librarySnapshot.books.size} offline books are removed from this device.",
                )
            },
            confirmButton = {
                TextButton(
                    onClick = {
                        confirmServerChange = false
                        changeServer()
                    },
                ) {
                    Text("Change server")
                }
            },
            dismissButton = {
                TextButton(onClick = { confirmServerChange = false }) {
                    Text("Cancel")
                }
            },
        )
    }

    val origin = remember(originValue) {
        originValue?.let { ServerOrigin.parse(it).getOrNull() }
    }

    if (origin == null) {
        onOriginChanged(null)
        ServerSetupScreen(
            initialValue = originValue.orEmpty(),
            onSave = { parsed ->
                settings.setOrigin(parsed)
                originValue = parsed.value
                activeEpisodeId = null
                gateRetry += 1
            },
        )
        return
    }

    onOriginChanged(origin.value)

    val webSession = remember(origin.value) { WebSession(origin) }
    val api = remember(origin.value) {
        HttpJularrClientApi(
            origin = origin.value,
            requestHeaders = webSession::requestHeaders,
            responseCookieSink = webSession::acceptResponseCookies,
        )
    }

    var gateState by remember(origin.value, gateRetry) {
        mutableStateOf<ServerGateState>(ServerGateState.Checking)
    }

    LaunchedEffect(origin.value, gateRetry) {
        gateState = ServerGateState.Checking
        gateState = try {
            val capabilities = withContext(Dispatchers.IO) {
                api.getCapabilities()
            }
            when (val compatibility = MobileCompatibilityGate.evaluate(capabilities)) {
                MobileCompatibilityState.Compatible -> ServerGateState.Compatible
                is MobileCompatibilityState.UpdateRequired ->
                    ServerGateState.UpdateRequired(compatibility.detail)
            }
        } catch (exception: Exception) {
            ServerGateState.Unavailable(
                exception.message ?: "The configured Jularr server could not be reached.",
            )
        }
    }

    // Account boundary for downloads: re-checked when the server becomes reachable and
    // after WebView page loads (sign-in, logout and account switches happen there).
    LaunchedEffect(origin.value, gateState, accountCheck) {
        if (gateState == ServerGateState.Compatible) {
            if (accountCheck > 0) {
                delay(500)
            }
            withContext(Dispatchers.IO) {
                offline.verifyAccount(origin.value, api)
                library.verifyAccount(origin.value, api)
            }
        }
    }

    val hasAccessibleDownloads = offlineSnapshot.accessibleDownloads(origin.value).isNotEmpty()
    val hasAccessibleLibraryBooks = librarySnapshot.accessibleBooks(origin.value).isNotEmpty()

    Box(modifier = Modifier.fillMaxSize()) {
        when (val gate = gateState) {
            ServerGateState.Checking -> FullscreenStatus(
                title = "Connecting to Jularr",
                detail = origin.value,
                progress = true,
            )
            is ServerGateState.UpdateRequired -> FullscreenStatus(
                title = "Update required",
                detail = gate.detail,
                progress = false,
                primaryLabel = "Check again",
                onPrimary = { gateRetry += 1 },
                secondaryLabel = "Change server",
                onSecondary = { requestServerChange() },
            )
            is ServerGateState.Unavailable -> FullscreenStatus(
                title = "Server unavailable",
                detail = gate.detail,
                progress = false,
                primaryLabel = "Try again",
                onPrimary = { gateRetry += 1 },
                secondaryLabel = "Change server",
                onSecondary = { requestServerChange() },
                tertiaryLabel = if (hasAccessibleDownloads) "Downloads" else null,
                onTertiary = { showDownloads = true },
                quaternaryLabel = if (hasAccessibleLibraryBooks) "Offline books" else null,
                onQuaternary = { showLibraryDownloads = true },
            )
            ServerGateState.Compatible -> {
                JularrWebShell(
                    origin = origin,
                    restoredState = restoredWebState.takeIf {
                        restoredWebOrigin == origin.value
                    },
                    modifier = Modifier
                        .fillMaxSize()
                        .windowInsetsPadding(WindowInsets.safeDrawing),
                    onWebViewChanged = {
                        currentWebView = it
                        onWebViewChanged(it)
                    },
                    onEpisodeRequested = { episodeId ->
                        activeEpisodeId = episodeId
                    },
                    onPageLoaded = { accountCheck += 1 },
                    onSecurityError = { securityError = it },
                    onLibraryWorkPageChanged = { currentWorkId = it },
                    resolveLibraryRequest = { path -> library.resolveLocalRequest(origin.value, path) },
                )

                if (currentWorkId != null && activeEpisodeId == null && !showDownloads && !showLibraryDownloads) {
                    LibrarySaveOfflineOverlay(
                        origin = origin.value,
                        workId = currentWorkId!!,
                        downloads = library,
                        api = api,
                        onOpenDownloads = { showLibraryDownloads = true },
                    )
                }

                BackHandler(
                    enabled = activeEpisodeId == null && !showDownloads && !showLibraryDownloads && !showUpdateScreen,
                ) {
                    when {
                        currentWebView?.canGoBack() == true -> currentWebView?.goBack()
                        else -> onFinish()
                    }
                }
            }
        }

        activeEpisodeId?.let { episodeId ->
            NativePlayerScreen(
                episodeId = episodeId,
                origin = origin,
                api = api,
                sessionHeaders = webSession::requestHeaders,
                onOpenDownloads = {
                    activeEpisodeId = null
                    showDownloads = true
                },
                onOpenTtsSettings = { showTtsSettings = true },
                onClose = {
                    activeEpisodeId = null
                },
            )
        }

        if (showTtsSettings) {
            TtsSettingsScreen(
                api = api,
                onClose = { showTtsSettings = false },
            )
        }

        if (showDownloads) {
            OfflineDownloadsScreen(
                origin = origin.value,
                downloads = offline,
                api = api,
                serverReachable = gateState == ServerGateState.Compatible,
                onPlay = { episodeId ->
                    showDownloads = false
                    activeEpisodeId = episodeId
                },
                onClose = { showDownloads = false },
            )
        }

        if (showLibraryDownloads) {
            LibraryDownloadsScreen(
                origin = origin.value,
                downloads = library,
                onClose = { showLibraryDownloads = false },
            )
        }

        if (showUpdateScreen) {
            UpdateScreen(
                initialResult = updatePromptInfo,
                onClose = { showUpdateScreen = false },
            )
        } else if (
            updatePromptInfo != null &&
            activeEpisodeId == null &&
            !showDownloads &&
            !showLibraryDownloads
        ) {
            UpdatePromptCard(
                info = updatePromptInfo!!,
                onUpdateNow = { showUpdateScreen = true },
                onLater = {
                    updatePreferences.dismissedVersion = updatePromptInfo?.version
                    updatePromptInfo = null
                },
                modifier = Modifier
                    .align(Alignment.BottomCenter)
                    .padding(16.dp),
            )
        }
    }

    securityError?.let { error ->
        AlertDialog(
            onDismissRequest = { securityError = null },
            title = { Text("Connection blocked") },
            text = { Text(error) },
            confirmButton = {
                TextButton(onClick = { securityError = null }) {
                    Text("OK")
                }
            },
        )
    }
}


@Composable
private fun ServerSetupScreen(
    initialValue: String,
    onSave: (ServerOrigin) -> Unit,
) {
    var value by rememberSaveable(initialValue) { mutableStateOf(initialValue) }
    var error by remember { mutableStateOf<String?>(null) }

    Surface(modifier = Modifier.fillMaxSize()) {
        Column(
            modifier = Modifier.padding(24.dp),
            verticalArrangement = Arrangement.spacedBy(16.dp),
        ) {
            Text(
                text = "Connect Jularr",
                style = MaterialTheme.typography.headlineMedium,
            )
            Text(
                text = "Enter the origin of your Jularr server. The normal Library, Learn, Novels and Settings pages stay in the secure app WebView; episode playback opens natively.",
                style = MaterialTheme.typography.bodyMedium,
            )
            OutlinedTextField(
                value = value,
                onValueChange = {
                    value = it
                    error = null
                },
                modifier = Modifier.fillMaxWidth(),
                singleLine = true,
                label = { Text("Jularr server") },
                placeholder = { Text("https://jularr.example") },
                isError = error != null,
                supportingText = error?.let { message ->
                    { Text(message) }
                },
            )
            if (value.trim().startsWith("http://", ignoreCase = true)) {
                Text(
                    text = "HTTP is unencrypted. Prefer HTTPS for any server reachable outside a trusted LAN.",
                    color = MaterialTheme.colorScheme.error,
                    style = MaterialTheme.typography.bodySmall,
                )
            }
            Button(
                onClick = {
                    ServerOrigin.parse(value)
                        .onSuccess(onSave)
                        .onFailure {
                            error = it.message ?: "Enter a valid Jularr server origin."
                        }
                },
            ) {
                Text("Connect")
            }
        }
    }
}

@Composable
private fun FullscreenStatus(
    title: String,
    detail: String,
    progress: Boolean,
    primaryLabel: String? = null,
    onPrimary: (() -> Unit)? = null,
    secondaryLabel: String? = null,
    onSecondary: (() -> Unit)? = null,
    tertiaryLabel: String? = null,
    onTertiary: (() -> Unit)? = null,
    quaternaryLabel: String? = null,
    onQuaternary: (() -> Unit)? = null,
) {
    Surface(modifier = Modifier.fillMaxSize()) {
        Column(
            modifier = Modifier
                .fillMaxSize()
                .padding(24.dp),
            horizontalAlignment = Alignment.CenterHorizontally,
            verticalArrangement = Arrangement.Center,
        ) {
            if (progress) {
                CircularProgressIndicator()
            }
            Text(
                text = title,
                style = MaterialTheme.typography.headlineSmall,
                modifier = Modifier.padding(top = 16.dp),
            )
            Text(
                text = detail,
                style = MaterialTheme.typography.bodyMedium,
                modifier = Modifier.padding(top = 8.dp),
            )
            if (primaryLabel != null && onPrimary != null) {
                Row(
                    modifier = Modifier.padding(top = 16.dp),
                    horizontalArrangement = Arrangement.spacedBy(8.dp),
                ) {
                    Button(onClick = onPrimary) {
                        Text(primaryLabel)
                    }
                    if (secondaryLabel != null && onSecondary != null) {
                        TextButton(onClick = onSecondary) {
                            Text(secondaryLabel)
                        }
                    }
                }
                if (tertiaryLabel != null && onTertiary != null) {
                    TextButton(
                        onClick = onTertiary,
                        modifier = Modifier.padding(top = 8.dp),
                    ) {
                        Text(tertiaryLabel)
                    }
                }
                if (quaternaryLabel != null && onQuaternary != null) {
                    TextButton(
                        onClick = onQuaternary,
                        modifier = Modifier.padding(top = 8.dp),
                    ) {
                        Text(quaternaryLabel)
                    }
                }
            }
        }
    }
}
