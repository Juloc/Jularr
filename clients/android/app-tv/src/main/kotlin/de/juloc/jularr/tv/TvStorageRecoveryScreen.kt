package de.juloc.jularr.tv

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.padding
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import androidx.tv.material3.Button
import androidx.tv.material3.MaterialTheme
import androidx.tv.material3.Surface
import androidx.tv.material3.Text

@Composable
fun TvStorageRecoveryScreen(
    decision: TvStorageDecision,
    busy: Boolean,
    onRetry: () -> Unit,
    onWake: () -> Unit,
    onBack: () -> Unit,
) {
    Surface(modifier = Modifier.fillMaxSize()) {
        Column(
            modifier = Modifier.padding(56.dp),
            verticalArrangement = Arrangement.spacedBy(20.dp),
            horizontalAlignment = Alignment.Start,
        ) {
            Text(
                text = "Media unavailable",
                style = MaterialTheme.typography.headlineLarge,
            )
            Text(
                text = decision.message,
                style = MaterialTheme.typography.bodyLarge,
            )

            Row(horizontalArrangement = Arrangement.spacedBy(12.dp)) {
                if (decision.primaryAction == TvStorageAction.RETRY) {
                    Button(
                        enabled = !busy,
                        onClick = onRetry,
                    ) {
                        Text(if (busy) "Checking…" else "Retry")
                    }
                }

                if (decision.canWake) {
                    Button(
                        enabled = !busy,
                        onClick = onWake,
                    ) {
                        Text("Wake NAS")
                    }
                }

                Button(
                    enabled = !busy,
                    onClick = onBack,
                ) {
                    Text("Back")
                }
            }
        }
    }
}
