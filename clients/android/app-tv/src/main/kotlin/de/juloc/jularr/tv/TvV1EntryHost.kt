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
import de.juloc.jularr.core.model.DevicePairingStarted
import de.juloc.jularr.core.model.InstanceCapabilities
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.delay
import kotlinx.coroutines.isActive

/**
 * Clean-start authentication gate for the original TV app. Reuses the original setup and
 * centered-panel components. It does not enter the legacy controller after pairing: that
 * controller still requests /api/client/v1 and must only be reconnected with the v1 catalog.
 *
 * [onAuthenticated] is the explicit future integration point for the original TV screens.
 */
@Composable
fun TvV1EntryHost(
    settings: TvServerSettings,
    session: TvV1DeviceSession,
    onAuthenticated: @Composable (String, InstanceCapabilities) -> Unit = { origin, _ ->
        TvV1AuthenticatedPendingCatalog(origin)
    },
) {
    val storedOrigin = remember { settings.origin }
    var origin by remember { mutableStateOf(storedOrigin) }
    var phase by remember { mutableStateOf(if (storedOrigin == null) TvV1EntryPhase.Setup else TvV1EntryPhase.Restoring) }
    var pairing by remember { mutableStateOf<DevicePairingStarted?>(null) }
    var capabilities by remember { mutableStateOf<InstanceCapabilities?>(null) }
    var error by remember { mutableStateOf<String?>(null) }

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

        TvV1EntryPhase.Authenticated -> onAuthenticated(requireNotNull(origin), requireNotNull(capabilities))

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

/** Never call the old /api/client/v1 controller while the media adapter is not installed. */
@Composable
private fun TvV1AuthenticatedPendingCatalog(origin: String) {
    TvCenteredPanel(
        title = "Gerät verbunden",
        description = origin,
    ) {
        Text(
            "Die Anmeldung über /api/v1 funktioniert. " +
                "Die vorhandene TV-Oberfläche wird mit der Medien-API verbunden.",
            style = MaterialTheme.typography.bodyLarge,
        )
    }
}

private enum class TvV1EntryPhase { Setup, Restoring, Pairing, Authenticated, Failed }

private fun Exception.tvV1FriendlyError(): String =
    when (this) {
        is AppApiException -> "Serverfehler $statusCode ($code)"
        else -> message ?: "Verbindung zur Jularr-Instanz fehlgeschlagen."
    }
