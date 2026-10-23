package de.juloc.jularr.tv

import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import androidx.tv.material3.MaterialTheme
import androidx.tv.material3.Surface
import androidx.tv.material3.Text
import de.juloc.jularr.core.model.ContinueWatchingItem
import de.juloc.jularr.core.model.PlaybackHistoryItem

/**
 * The user's own recent playback (#522 item 4). Backed by `GET /me/playback-history`
 * when the server advertises it (docs/ANDROID_CLIENTS.md §4, "Playback continuity
 * endpoints"); otherwise this screen still renders, showing Continue Watching instead of
 * an empty destination, with [usesContinueWatchingFallback] noting why.
 */
@Composable
fun TvActivityScreen(
    history: List<PlaybackHistoryItem>,
    continueWatchingFallback: List<ContinueWatchingItem>,
    usesContinueWatchingFallback: Boolean,
    serverOrigin: String,
    requestHeaders: Map<String, String>,
    focusMemory: TvFocusMemory,
    onOpenEpisode: (episodeId: String, animeId: String) -> Unit,
) {
    val focusColor = rememberTvFocusColor()

    Surface(modifier = Modifier.fillMaxSize()) {
        LazyColumn(
            modifier = Modifier
                .fillMaxSize()
                .padding(horizontal = 48.dp, vertical = 32.dp),
            verticalArrangement = Arrangement.spacedBy(18.dp),
        ) {
            item {
                Text(
                    stringResource(R.string.tv_activity_title),
                    style = MaterialTheme.typography.headlineLarge,
                )
            }

            if (usesContinueWatchingFallback) {
                item {
                    Text(
                        stringResource(R.string.tv_activity_unavailable_note),
                        style = MaterialTheme.typography.bodyMedium,
                        color = MaterialTheme.colorScheme.onSurface.copy(alpha = 0.72f),
                    )
                }

                if (continueWatchingFallback.isEmpty()) {
                    item {
                        Text(
                            stringResource(R.string.tv_activity_empty),
                            style = MaterialTheme.typography.bodyLarge,
                        )
                    }
                } else {
                    items(
                        items = continueWatchingFallback,
                        key = { "cw:${it.episodeId}" },
                    ) { row ->
                        TvActivityRow(
                            coverImageUrl = row.coverImageUrl,
                            serverOrigin = serverOrigin,
                            requestHeaders = requestHeaders,
                            title = row.animeTitle,
                            subtitle = stringResource(
                                R.string.tv_episode_season_and_number,
                                row.seasonNumber,
                                row.episodeNumber,
                            ),
                            trailing = stringResource(R.string.tv_activity_status_in_progress, row.percent),
                            focusKey = "cw:${row.episodeId}",
                            focusColor = focusColor,
                            focusMemory = focusMemory,
                            onClick = { onOpenEpisode(row.episodeId, row.animeId) },
                        )
                    }
                }
            } else if (history.isEmpty()) {
                item {
                    Text(
                        stringResource(R.string.tv_activity_empty),
                        style = MaterialTheme.typography.bodyLarge,
                    )
                }
            } else {
                items(items = history, key = { it.id }) { row ->
                    TvActivityRow(
                        coverImageUrl = null,
                        serverOrigin = serverOrigin,
                        requestHeaders = requestHeaders,
                        title = row.animeTitle,
                        subtitle = stringResource(
                            R.string.tv_episode_season_and_number,
                            row.seasonNumber,
                            row.episodeNumber,
                        ),
                        trailing = if (row.reachedEnd) {
                            stringResource(R.string.tv_activity_status_finished)
                        } else {
                            stringResource(R.string.tv_activity_last_played, row.lastPlayedAtUtc)
                        },
                        focusKey = row.id,
                        focusColor = focusColor,
                        focusMemory = focusMemory,
                        onClick = { onOpenEpisode(row.episodeId, row.animeId) },
                    )
                }
            }
        }
    }
}

@Composable
private fun TvActivityRow(
    coverImageUrl: String?,
    serverOrigin: String,
    requestHeaders: Map<String, String>,
    title: String,
    subtitle: String,
    trailing: String,
    focusKey: String,
    focusColor: androidx.compose.ui.graphics.Color,
    focusMemory: TvFocusMemory,
    onClick: () -> Unit,
) {
    var focused by remember(focusKey) { mutableStateOf(false) }

    Row(
        modifier = Modifier
            .fillMaxWidth()
            .background(MaterialTheme.colorScheme.surfaceVariant, MaterialTheme.shapes.medium)
            .tvFocusIndication(focused, focusColor, MaterialTheme.shapes.medium)
            .clickable(onClick = onClick)
            .reportFocus { isFocused ->
                focused = isFocused
                if (isFocused) {
                    focusMemory.remember("activity", focusKey)
                }
            }
            .padding(16.dp),
        horizontalArrangement = Arrangement.spacedBy(16.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        TvArtwork(
            url = coverImageUrl,
            serverOrigin = serverOrigin,
            requestHeaders = requestHeaders,
            contentDescription = title,
            modifier = Modifier
                .width(120.dp)
                .aspectRatio(16f / 9f)
                .clip(MaterialTheme.shapes.small),
        )
        Column(modifier = Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(4.dp)) {
            Text(title, style = MaterialTheme.typography.titleMedium, maxLines = 1)
            Text(subtitle, style = MaterialTheme.typography.bodyMedium)
        }
        Text(trailing, style = MaterialTheme.typography.bodyMedium)
    }
}
