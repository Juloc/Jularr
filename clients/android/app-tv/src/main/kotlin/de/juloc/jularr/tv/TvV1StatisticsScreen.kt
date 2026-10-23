package de.juloc.jularr.tv

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import androidx.tv.material3.Button
import androidx.tv.material3.MaterialTheme
import androidx.tv.material3.Text
import de.juloc.jularr.core.api.AppApiException
import de.juloc.jularr.core.api.tvlegacy.TvV1LegacyFeaturesClient
import de.juloc.jularr.core.api.tvlegacy.TvV1ProfileStatistics
import kotlinx.coroutines.CancellationException

/**
 * These are actual clean-start aggregates, NOT the old chronological Activity history.
 * Never manufacture individual playback events from topWorks or Continue Watching.
 */
@Composable
internal fun TvV1StatisticsScreen(
    api: TvV1LegacyFeaturesClient,
    onOpenWork: (Long) -> Unit,
    onUnauthorized: () -> Unit,
    onBack: () -> Unit,
) {
    var days by remember(api) { mutableStateOf(30) }
    var refresh by remember(api) { mutableStateOf(0) }
    var statistics by remember(api) { mutableStateOf<TvV1ProfileStatistics?>(null) }
    var loading by remember(api) { mutableStateOf(true) }
    var error by remember(api) { mutableStateOf<String?>(null) }

    LaunchedEffect(api, days, refresh) {
        loading = true
        try {
            statistics = api.statistics(days)
            error = null
        } catch (cancel: CancellationException) {
            throw cancel
        } catch (failure: AppApiException) {
            if (failure.statusCode == 401) onUnauthorized()
            else error = "Statistik: ${failure.code} (HTTP ${failure.statusCode})"
        } catch (failure: Exception) {
            error = failure.message ?: "Die Statistik konnte nicht geladen werden."
        } finally {
            loading = false
        }
    }

    TvCenteredPanel(
        title = "Nutzungsstatistik",
        description = "Zusammengefasste Profilwerte – kein chronologischer Wiedergabeverlauf.",
    ) {
        Row(horizontalArrangement = Arrangement.spacedBy(10.dp)) {
            Button(onClick = { days = 30 }) { Text(if (days == 30) "✓ 30 Tage" else "30 Tage") }
            Button(onClick = { days = 90 }) { Text(if (days == 90) "✓ 90 Tage" else "90 Tage") }
            Button(onClick = { days = 0 }) { Text(if (days == 0) "✓ Gesamt" else "Gesamt") }
        }
        if (loading) Text("Statistik wird geladen …")
        if (error != null) {
            Text(requireNotNull(error), color = MaterialTheme.colorScheme.error)
            Button(onClick = { refresh++ }) { Text("Erneut versuchen") }
        }
        val value = if (!loading && error == null) statistics else null
        if (value != null) {
            Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
                Text("Sehzeit: ${tvV1WatchDuration(value.watchedMs)}",
                    style = MaterialTheme.typography.titleMedium)
                Text("Wiedergabe-Sessions: ${value.sessions}")
                Text("Spielzeit: ${tvV1WatchDuration(value.gamePlayedMs)}")
                value.completed.forEach {
                    Text("Abgeschlossene Medien (Typ ${it.mediaTypeId}): ${it.count}")
                }
            }
            if (value.topWorks.isNotEmpty()) {
                Text("Am häufigsten angesehen", style = MaterialTheme.typography.titleLarge)
                LazyColumn(verticalArrangement = Arrangement.spacedBy(8.dp)) {
                    items(value.topWorks, key = { it.workId }) { work ->
                        Button(
                            modifier = Modifier.fillMaxWidth(),
                            onClick = { onOpenWork(work.workId) },
                        ) {
                            Text("${work.title} · ${tvV1WatchDuration(work.durationMs)}")
                        }
                    }
                }
            }
        }
        Button(onClick = onBack) { Text("Zurück zu Einstellungen") }
    }
}

internal fun tvV1WatchDuration(valueMs: Long): String {
    val minutes = (valueMs.coerceAtLeast(0) / 60_000L)
    val hours = minutes / 60L
    val remainder = minutes % 60L
    return if (hours > 0L) "${hours} Std. ${remainder} Min."
        else "${remainder} Min."
}
