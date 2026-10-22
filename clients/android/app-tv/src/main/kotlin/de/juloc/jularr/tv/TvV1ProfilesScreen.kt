package de.juloc.jularr.tv

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.ui.unit.dp
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.tv.material3.Button
import androidx.tv.material3.MaterialTheme
import androidx.tv.material3.Text
import de.juloc.jularr.core.api.AppApiException
import de.juloc.jularr.core.api.profile.TvSelectableProfileV1
import de.juloc.jularr.core.api.profile.TvV1ProfileClient
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.launch

/**
 * Profile selection uses the original centered-TV panel and remote-friendly TV text input.
 * It does not invent a username or wrap a V1 token as a legacy account cookie.
 */
@Composable
internal fun TvV1ProfilesScreen(
    api: TvV1ProfileClient,
    onSwitched: () -> Unit,
    onBack: () -> Unit,
    onUnauthorized: () -> Unit,
) {
    val scope = rememberCoroutineScope()
    var profiles by remember(api) { mutableStateOf<List<TvSelectableProfileV1>>(emptyList()) }
    var loading by remember(api) { mutableStateOf(true) }
    var busy by remember(api) { mutableStateOf(false) }
    var selected by remember(api) { mutableStateOf<TvSelectableProfileV1?>(null) }
    var pin by remember(api) { mutableStateOf("") }
    var error by remember(api) { mutableStateOf<String?>(null) }
    var refresh by remember(api) { mutableStateOf(0) }

    LaunchedEffect(api, refresh) {
        loading = true
        try {
            profiles = api.profiles()
            error = null
        } catch (cancel: CancellationException) {
            throw cancel
        } catch (failure: Exception) {
            if (failure is AppApiException && failure.statusCode == 401) onUnauthorized()
            else error = failure.v1ProfilesError()
        } finally {
            loading = false
        }
    }

    fun choose(profile: TvSelectableProfileV1, suppliedPin: String?) {
        if (busy) return
        busy = true
        error = null
        scope.launch {
            try {
                api.selectProfile(profile.id, suppliedPin)
                pin = ""
                onSwitched() // Parent reloads capabilities and discards cached profile media.
            } catch (cancel: CancellationException) {
                throw cancel
            } catch (failure: Exception) {
                if (failure is AppApiException && failure.statusCode == 401) onUnauthorized()
                else error = failure.v1ProfilesError()
            } finally {
                busy = false
            }
        }
    }

    TvCenteredPanel(
        title = if (selected == null) "Profil wählen" else requireNotNull(selected).displayName,
        description = if (selected == null) "Wähle ein Jularr-Profil für diesen Fernseher."
            else "Dieses Profil ist mit einer PIN geschützt.",
    ) {
        if (error != null) Text(requireNotNull(error), color = MaterialTheme.colorScheme.error)
        if (loading || busy) {
            Text(if (loading) "Profile werden geladen …" else "Profil wird gewechselt …")
        } else if (selected != null) {
            val current = requireNotNull(selected)
            TvInput(
                value = pin,
                onValueChange = { value ->
                    pin = value.filter(Char::isDigit).take(current.pinLength ?: 20)
                    error = null
                },
                label = "Profil-PIN",
                placeholder = "PIN eingeben",
                password = true,
            )
            Button(
                enabled = !busy && pin.isNotBlank() &&
                    (current.pinLength == null || pin.length == current.pinLength),
                onClick = { choose(current, pin) },
            ) { Text("Profil öffnen") }
            Button(onClick = {
                selected = null
                pin = ""
                error = null
            }) { Text("Andere Profile") }
        } else {
            LazyColumn(verticalArrangement = Arrangement.spacedBy(10.dp)) {
                items(profiles, key = { it.id }) { profile ->
                    Button(
                        enabled = !busy,
                        onClick = {
                            if (profile.isLocked) {
                                selected = profile
                                pin = ""
                            } else choose(profile, null)
                        },
                    ) {
                        Text(profile.displayName + if (profile.isLocked) " · PIN" else "")
                    }
                }
            }
            if (profiles.isEmpty()) Text("Keine zugänglichen Profile gefunden.")
            Button(onClick = { refresh += 1 }) { Text("Aktualisieren") }
            Button(onClick = onBack) { Text("Zurück") }
        }
    }
}

private fun Exception.v1ProfilesError(): String =
    if (this is AppApiException) "Profil-API: $code (HTTP $statusCode)"
    else message ?: "Profile konnten nicht geladen werden."
