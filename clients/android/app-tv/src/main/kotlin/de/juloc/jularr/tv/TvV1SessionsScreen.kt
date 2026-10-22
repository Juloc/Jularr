package de.juloc.jularr.tv

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import androidx.tv.material3.Button
import androidx.tv.material3.MaterialTheme
import androidx.tv.material3.Text
import de.juloc.jularr.core.api.AppApiException
import de.juloc.jularr.core.api.profile.TvAccountSessionV1
import de.juloc.jularr.core.api.profile.TvV1ProfileClient
import kotlinx.coroutines.CancellationException

/**
 * The V1 /sessions endpoint lists sessions of the current account. It is NOT the
 * former account chooser and it does not supply other account credentials.
 */
@Composable
internal fun TvV1SessionsScreen(
    api: TvV1ProfileClient,
    onUnauthorized: () -> Unit,
    onBack: () -> Unit,
) {
    var sessions by remember(api) { mutableStateOf<List<TvAccountSessionV1>>(emptyList()) }
    var loading by remember(api) { mutableStateOf(true) }
    var error by remember(api) { mutableStateOf<String?>(null) }
    var reload by remember(api) { mutableIntStateOf(0) }

    LaunchedEffect(api, reload) {
        loading = true
        try {
            sessions = api.sessions()
            error = null
        } catch (cancel: CancellationException) {
            throw cancel
        } catch (failure: AppApiException) {
            if (failure.statusCode == 401) onUnauthorized()
            else error = "Sessions: ${failure.code} (HTTP ${failure.statusCode})"
        } catch (failure: Exception) {
            error = failure.message ?: "Die Sitzungen konnten nicht geladen werden."
        } finally {
            loading = false
        }
    }

    TvCenteredPanel(
        title = "Aktive Sitzungen",
        description = "Geräte und Browser dieses Jularr-Accounts. Dies ist keine Accountauswahl.",
    ) {
        if (loading) Text("Sitzungen werden geladen …")
        if (error != null) {
            Text(requireNotNull(error), color = MaterialTheme.colorScheme.error)
            Button(onClick = { reload++ }) { Text("Erneut versuchen") }
        }
        if (!loading && error == null) {
            if (sessions.isEmpty()) Text("Keine aktiven Sitzungen gemeldet.")
            LazyColumn(verticalArrangement = Arrangement.spacedBy(10.dp)) {
                items(sessions, key = { it.id }) { session ->
                    Column(
                        modifier = Modifier.fillMaxWidth(),
                        verticalArrangement = Arrangement.spacedBy(3.dp),
                    ) {
                        Text(
                            session.deviceName?.takeIf(String::isNotBlank)
                                ?: session.devicePlatform?.takeIf(String::isNotBlank)
                                ?: "Browser-Sitzung",
                            style = MaterialTheme.typography.titleMedium,
                        )
                        Text(
                            listOfNotNull(
                                session.devicePlatform,
                                if (session.isCurrent) "Dieses Gerät" else null,
                                "Qualitätsprofil ${session.playbackQuality}",
                            ).joinToString(" · "),
                            style = MaterialTheme.typography.bodySmall,
                        )
                    }
                }
            }
            Button(onClick = { reload++ }) { Text("Aktualisieren") }
        }
        Button(onClick = onBack) { Text("Zurück zu Einstellungen") }
    }
}
