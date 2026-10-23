package de.juloc.jularr.tv

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import androidx.tv.material3.Button
import androidx.tv.material3.MaterialTheme
import androidx.tv.material3.Text
import de.juloc.jularr.core.model.DiscoveredJularrServer

/** Reused manual V1 server address form; the obsolete cookie login and episode UI were removed. */
@Composable
fun TvSetupScreen(
    initialOrigin: String,
    error: String?,
    busy: Boolean,
    discovering: Boolean = false,
    discovered: List<DiscoveredJularrServer> = emptyList(),
    onConnect: (String) -> Unit,
) {
    var origin by rememberSaveable(initialOrigin) { mutableStateOf(initialOrigin) }
    var localError by remember { mutableStateOf<String?>(null) }

    TvCenteredPanel(
        title = stringResource(R.string.tv_setup_title),
        description = stringResource(R.string.tv_setup_description),
    ) {
        if (discovering) {
            Text(
                text = stringResource(R.string.tv_setup_discovering),
                style = MaterialTheme.typography.bodyMedium,
            )
        } else if (discovered.isNotEmpty()) {
            Column(verticalArrangement = Arrangement.spacedBy(10.dp)) {
                Text(
                    text = stringResource(R.string.tv_setup_discovered_heading),
                    style = MaterialTheme.typography.titleMedium,
                )
                for (server in discovered) {
                    Row(
                        modifier = Modifier.fillMaxWidth(),
                        horizontalArrangement = Arrangement.spacedBy(12.dp),
                        verticalAlignment = Alignment.CenterVertically,
                    ) {
                        Column(modifier = Modifier.weight(1f)) {
                            Text(server.name, style = MaterialTheme.typography.bodyLarge)
                            Text(
                                text = server.origin,
                                style = MaterialTheme.typography.bodySmall,
                                color = MaterialTheme.colorScheme.onSurface.copy(alpha = 0.7f),
                            )
                        }
                        Button(enabled = !busy, onClick = { onConnect(server.origin) }) {
                            Text(stringResource(R.string.tv_setup_discovered_connect))
                        }
                    }
                }
            }
            Text(
                text = stringResource(R.string.tv_setup_manual_heading),
                style = MaterialTheme.typography.titleMedium,
            )
        }

        TvInput(
            value = origin,
            onValueChange = {
                origin = it
                localError = null
            },
            label = stringResource(R.string.tv_setup_label_server),
            placeholder = stringResource(R.string.tv_setup_placeholder_server),
        )

        (localError ?: error)?.let {
            Text(
                text = it,
                color = MaterialTheme.colorScheme.error,
                style = MaterialTheme.typography.bodyMedium,
            )
        }

        Button(
            enabled = !busy,
            onClick = {
                val normalized = runCatching { TvServerOrigin.normalize(origin) }
                if (normalized.isFailure) {
                    localError = normalized.exceptionOrNull()?.message
                } else {
                    localError = null
                    onConnect(normalized.getOrThrow())
                }
            },
        ) {
            Text(
                if (busy) {
                    stringResource(R.string.tv_setup_button_connecting)
                } else {
                    stringResource(R.string.tv_setup_button_continue)
                },
            )
        }
    }
}

