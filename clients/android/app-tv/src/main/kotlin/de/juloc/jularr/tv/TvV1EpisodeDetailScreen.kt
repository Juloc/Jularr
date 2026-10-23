package de.juloc.jularr.tv

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.unit.dp
import androidx.compose.runtime.Composable
import androidx.tv.material3.Button
import androidx.tv.material3.MaterialTheme
import androidx.tv.material3.Surface
import androidx.tv.material3.Text
import de.juloc.jularr.core.api.catalog.TvWorkEpisodeV1
import de.juloc.jularr.core.model.AnimeDetail

/**
 * V1 representation of the ORIGINAL TvEpisodeScreen layout.
 * Learning coverage is intentionally absent: V1 does not supply the legacy cue DTOs.
 */
@Composable
internal fun TvV1EpisodeDetailScreen(
    work: AnimeDetail,
    episode: TvWorkEpisodeV1,
    serverOrigin: String,
    requestHeaders: Map<String, String>,
    onPlay: () -> Unit,
    onBack: () -> Unit,
) {
    val fraction = tvV1EpisodeProgressFraction(episode.resumePositionMs, episode.durationMs)
    Surface(modifier = Modifier.fillMaxSize()) {
        Column(
            modifier = Modifier.fillMaxSize().padding(horizontal = 52.dp, vertical = 34.dp),
            verticalArrangement = Arrangement.spacedBy(22.dp),
        ) {
            Row(
                modifier = Modifier.fillMaxWidth(),
                horizontalArrangement = Arrangement.spacedBy(18.dp),
                verticalAlignment = Alignment.CenterVertically,
            ) {
                Button(onClick = onBack) { Text("Zurück") }
                Column(verticalArrangement = Arrangement.spacedBy(3.dp)) {
                    Text(work.title, style = MaterialTheme.typography.titleLarge)
                    Text(
                        tvV1EpisodeLabel(episode),
                        style = MaterialTheme.typography.bodyMedium,
                    )
                }
            }
            Row(
                modifier = Modifier.fillMaxWidth(),
                horizontalArrangement = Arrangement.spacedBy(28.dp),
                verticalAlignment = Alignment.Top,
            ) {
                TvArtwork(
                    url = episode.imageUrl ?: work.coverImageUrl,
                    serverOrigin = serverOrigin,
                    requestHeaders = requestHeaders,
                    contentDescription = episode.title ?: tvV1EpisodeLabel(episode),
                    modifier = Modifier.width(180.dp).aspectRatio(2f / 3f)
                        .clip(MaterialTheme.shapes.medium),
                )
                Column(
                    modifier = Modifier.weight(1f)
                        .background(
                            MaterialTheme.colorScheme.surfaceVariant,
                            MaterialTheme.shapes.medium,
                        )
                        .padding(28.dp),
                    verticalArrangement = Arrangement.spacedBy(16.dp),
                ) {
                    Text(
                        episode.title ?: tvV1EpisodeLabel(episode),
                        style = MaterialTheme.typography.headlineLarge,
                    )
                    Text(
                        listOfNotNull(
                            if (episode.mediaAssetId == null) "Keine lokale Mediendatei" else "Mediendatei vorhanden",
                            if (episode.isCompleted) "Gesehen" else null,
                            episode.runtimeMinutes?.let { "${it} Minuten" },
                        ).joinToString(" · "),
                        style = MaterialTheme.typography.bodyMedium,
                    )
                    episode.overview?.takeIf { it.isNotBlank() }?.let {
                        Text(it, style = MaterialTheme.typography.bodyLarge)
                    }
                    if (fraction != null && !episode.isCompleted && fraction > 0f) {
                        Text(
                            "Fortsetzen bei ${formatTvChapterTime(requireNotNull(episode.resumePositionMs))}",
                            style = MaterialTheme.typography.bodyLarge,
                        )
                        Box(Modifier.fillMaxWidth().height(6.dp)
                            .background(MaterialTheme.colorScheme.onSurface.copy(alpha = 0.2f))) {
                            Box(Modifier.fillMaxWidth(fraction).height(6.dp)
                                .background(MaterialTheme.colorScheme.primary))
                        }
                    }
                    Row(horizontalArrangement = Arrangement.spacedBy(14.dp)) {
                        Button(
                            enabled = episode.playableMediaAssetId() != null,
                            onClick = onPlay,
                        ) {
                            Text(
                                if ((episode.resumePositionMs ?: 0) > 0 && !episode.isCompleted)
                                    "Fortsetzen" else "Abspielen",
                            )
                        }
                        Button(onClick = onBack) { Text("Zurück zu Episoden") }
                    }
                }
            }
        }
    }
}

internal fun tvV1EpisodeLabel(episode: TvWorkEpisodeV1): String =
    "S${episode.seasonNumber} E${episode.number.stripTrailingZeros().toPlainString()}"

internal fun tvV1EpisodeProgressFraction(positionMs: Long?, durationMs: Long?): Float? {
    if (positionMs == null || durationMs == null || durationMs <= 0) return null
    return (positionMs.toDouble() / durationMs.toDouble()).coerceIn(0.0, 1.0).toFloat()
}
