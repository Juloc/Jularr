package de.juloc.jularr.tv

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Row
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.unit.dp
import androidx.tv.material3.Button
import androidx.tv.material3.MaterialTheme
import androidx.tv.material3.Text
import de.juloc.jularr.core.api.AppApiException
import de.juloc.jularr.core.api.TvServerDiscoveryClient
import de.juloc.jularr.core.model.DevicePairingStarted
import de.juloc.jularr.core.model.DiscoveredJularrServer
import de.juloc.jularr.core.model.InstanceCapabilities
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.delay
import kotlinx.coroutines.isActive

/**
 * Clean-start authentication gate for the original TV app. Reuses the original setup and
 * centered-panel components. It does not enter the legacy controller after pairing: that
 * controller still requests /api/client/v1 and must only be reconnected with the v1 catalog.
 *
 * [onAuthenticated] renders the live V1 catalog after device authentication.
 */
@Composable
fun TvV1EntryHost(
    settings: TvServerSettings,
    session: TvV1DeviceSession,
    onAuthenticated: @Composable (
        String, InstanceCapabilities, () -> Unit, () -> Unit, () -> Unit, () -> Unit, () -> Unit
    ) -> Unit,
) {
    val storedOrigin = remember { settings.origin }
    var origin by remember { mutableStateOf(storedOrigin) }
    var phase by remember { mutableStateOf(if (storedOrigin == null) TvV1EntryPhase.Setup else TvV1EntryPhase.Restoring) }
    var pairing by remember { mutableStateOf<DevicePairingStarted?>(null) }
    var capabilities by remember { mutableStateOf<InstanceCapabilities?>(null) }
    var error by remember { mutableStateOf<String?>(null) }
    var discovered by remember { mutableStateOf<List<DiscoveredJularrServer>>(emptyList()) }
    var discovering by remember { mutableStateOf(false) }
    var discoveryRevision by remember { mutableStateOf(0) }

    // LAN discovery is best-effort: manual URL setup stays available throughout.
    LaunchedEffect(phase, discoveryRevision) {
        if (phase != TvV1EntryPhase.Setup) return@LaunchedEffect
        discovering = true
        try {
            discovered = TvServerDiscoveryClient().discover()
        } catch (cancel: CancellationException) {
            throw cancel
        } catch (_: Exception) {
            discovered = emptyList()
        } finally {
            discovering = false
        }
    }

    LaunchedEffect(origin, phase) {
        if (origin == null) return@LaunchedEffect

        when (phase) {
            TvV1EntryPhase.Restoring -> {
                try {
                    session.selectServer(requireNotNull(origin))
                    val restored = session.restore()
                    if (restored == null) {
                        phase = TvV1EntryPhase.Pairing
                    } else {
                        capabilities = restored
                        phase = TvV1EntryPhase.Authenticated
                    }
                } catch (cancel: CancellationException) {
                    throw cancel
                } catch (failure: Exception) {
                    error = failure.tvV1FriendlyError()
                    phase = TvV1EntryPhase.Failed
                }
            }

            TvV1EntryPhase.Pairing -> {
                var replacingId: String? = null
                try {
                    while (isActive) {
                        pairing = null
                        val started = session.startPairing(replacingId)
                        pairing = started
                        replacingId = started.pairingId
                        try {
                            while (isActive) {
                                // The v1 pairing contract specifies five-second polling.
                                delay(5_000L)
                                val claimed = session.claimPairing(started.pairingId)
                                if (claimed.isPaired) {
                                    capabilities = requireNotNull(session.restore()) {
                                        "The paired device no longer has a valid session."
                                    }
                                    phase = TvV1EntryPhase.Authenticated
                                    return@LaunchedEffect
                                }
                            }
                        } catch (cancel: CancellationException) {
                            throw cancel
                        } catch (expired: AppApiException) {
                            if (expired.statusCode != 404) throw expired
                            // A replacement pairing invalidates the expired code server-side.
                        }
                    }
                } catch (cancel: CancellationException) {
                    throw cancel
                } catch (failure: Exception) {
                    error = failure.tvV1FriendlyError()
                    phase = TvV1EntryPhase.Failed
                }
            }

            else -> Unit
        }
    }

    when (phase) {
        TvV1EntryPhase.Setup -> TvSetupScreen(
            initialOrigin = origin.orEmpty(),
            error = error,
            busy = false,
            discovering = discovering,
            discovered = discovered,
            onDiscoverAgain = { discoveryRevision++ },
            onConnect = { entered ->
                // TvSetupScreen validates the address before it calls this callback.
                origin = TvServerOrigin.normalize(entered)
                settings.origin = origin
                pairing = null
                capabilities = null
                error = null
                phase = TvV1EntryPhase.Restoring
            },
        )

        TvV1EntryPhase.AccountSelection -> TvCenteredPanel(
            title = "Account auswählen",
            description = "Jede Anmeldung besitzt einen eigenen verschlüsselten Geräte-Token.",
        ) {
            session.accounts().forEach { account ->
                Button(onClick = {
                    session.selectAccount(account.id)
                    capabilities = null
                    error = null
                    phase = TvV1EntryPhase.Restoring
                }) {
                    Text(account.label + if (account.hasToken) "" else " · Kopplung erforderlich")
                }
            }
            Button(onClick = {
                session.beginNewAccount()
                capabilities = null
                pairing = null
                error = null
                phase = TvV1EntryPhase.Pairing
            }) { Text("Weiteren Account koppeln") }
            Button(onClick = {
                phase = TvV1EntryPhase.Restoring
            }) { Text("Abbrechen") }
        }

        TvV1EntryPhase.Restoring -> TvCenteredPanel(
            title = "Jularr TV",
            description = "Verbindung zur Jularr-Instanz wird geprüft.",
        ) {
            Text(origin.orEmpty(), style = MaterialTheme.typography.bodyMedium)
        }

        TvV1EntryPhase.Pairing -> TvCenteredPanel(
            title = "Gerät verbinden",
            description = origin.orEmpty(),
        ) {
            Text(
                text = pairing?.code ?: "Pairing-Code wird angefordert …",
                style = MaterialTheme.typography.displayMedium,
            )
            Text(
                text = "Öffne Jularr auf deinem Smartphone oder Computer, gib den Code " +
                    "unter Geräte koppeln ein und bestätige das Gerät.",
                style = MaterialTheme.typography.bodyLarge,
            )
            Text(
                text = "Die Verbindung wird alle fünf Sekunden geprüft. " +
                    "Ein abgelaufener Code wird automatisch erneuert.",
                style = MaterialTheme.typography.bodyMedium,
            )
            Button(onClick = {
                pairing = null
                error = null
                phase = TvV1EntryPhase.Setup
            }) {
                Text("Server wechseln")
            }
        }

        TvV1EntryPhase.Authenticated -> onAuthenticated(
            requireNotNull(origin),
            requireNotNull(capabilities),
            {
                // A revoked bearer cannot reuse an authenticated catalog screen.
                pairing = null
                capabilities = null
                phase = TvV1EntryPhase.Pairing
            },
            {
                // Changing profiles rotates the device bearer; reload permissions and
                // discard the previous profile's cached library/continue data.
                capabilities = null
                phase = TvV1EntryPhase.Restoring
            },
            {
                capabilities = null
                pairing = null
                phase = TvV1EntryPhase.Pairing
            },
            {
                capabilities = null
                pairing = null
                phase = TvV1EntryPhase.Setup
            },
            {
                // The current viewer's catalog is disposed while choosing another
                // device bearer. Do not touch other saved slots.
                capabilities = null
                phase = TvV1EntryPhase.AccountSelection
            },
        )

        TvV1EntryPhase.Failed -> TvCenteredPanel(
            title = "Verbindung fehlgeschlagen",
            description = origin.orEmpty(),
        ) {
            Text(error ?: "Unbekannter Verbindungsfehler", color = MaterialTheme.colorScheme.error)
            Row(horizontalArrangement = Arrangement.spacedBy(12.dp)) {
                Button(onClick = {
                    error = null
                    phase = TvV1EntryPhase.Restoring
                }) {
                    Text("Erneut versuchen")
                }
                Button(onClick = {
                    error = null
                    pairing = null
                    phase = TvV1EntryPhase.Setup
                }) {
                    Text("Server ändern")
                }
            }
        }
    }
}

private enum class TvV1EntryPhase { Setup, Restoring, Pairing, Authenticated, AccountSelection, Failed }

private fun Exception.tvV1FriendlyError(): String =
    when (this) {
        is AppApiException -> "Serverfehler $statusCode ($code)"
        else -> message ?: "Verbindung zur Jularr-Instanz fehlgeschlagen."
    }
