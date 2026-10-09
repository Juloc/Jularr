package de.juloc.jularr.tv

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.LazyRow
import androidx.compose.foundation.lazy.items
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import androidx.tv.material3.Button
import androidx.tv.material3.MaterialTheme
import androidx.tv.material3.Surface
import androidx.tv.material3.Text
import de.juloc.jularr.core.model.WatchlistItem

/**
 * Watchlist is one of the four required sidebar destinations (#522). `GET
 * /api/client/v1/watchlist` (#533) backs it now: [TvAppController.selectSidebarRoute] loads
 * the signed-in profile's followed works the same way it loads Home's Continue Watching and
 * Activity's playback history, and this screen shows them as focusable cards. The
 * "not available" state ([supported] false) is shown only when the server's
 * `/capabilities` response lacks the `watchlist` flag (an older server), never just because
 * the list happens to be empty.
 */
@Composable
fun TvWatchlistScreen(
    entries: List<WatchlistItem>,
    page: Int,
    hasMore: Boolean,
    onPageChange: (Int) -> Unit,
    supported: Boolean,
    serverOrigin: String,
    requestHeaders: Map<String, String>,
    focusMemory: TvFocusMemory,
    onOpenAnime: (animeId: String) -> Unit,
) {
    when {
        !supported -> TvCenteredPanel(
            title = stringResource(R.string.tv_watchlist_title),
            description = stringResource(R.string.tv_watchlist_unavailable),
            content = {},
        )

        entries.isEmpty() -> TvCenteredPanel(
            title = stringResource(R.string.tv_watchlist_title),
            description = stringResource(R.string.tv_watchlist_empty),
            content = {},
        )

        else -> {
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
                            stringResource(R.string.tv_watchlist_title),
                            style = MaterialTheme.typography.headlineLarge,
                        )
                    }
                    item {
                        LazyRow(horizontalArrangement = Arrangement.spacedBy(18.dp)) {
                            items(items = entries, key = { it.id }) { entry ->
                                TvWatchlistCard(
                                    entry = entry,
                                    serverOrigin = serverOrigin,
                                    requestHeaders = requestHeaders,
                                    focusColor = focusColor,
                                    focusMemory = focusMemory,
                                    onClick = {
                                        if (entry.availability == "in_library") {
                                            onOpenAnime(entry.id)
                                        }
                                    },
                                )
                            }
                        }
                    }
                    if (page > 1 || hasMore) {
                        item {
                            Row(horizontalArrangement = Arrangement.spacedBy(16.dp)) {
                                if (page > 1) {
                                    Button(onClick = { onPageChange(page - 1) }) {
                                        Text(stringResource(R.string.tv_watchlist_previous_page))
                                    }
                                }
                                Text(stringResource(R.string.tv_watchlist_page, page))
                                if (hasMore) {
                                    Button(onClick = { onPageChange(page + 1) }) {
                                        Text(stringResource(R.string.tv_watchlist_next_page))
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }
    }
}

@Composable
private fun TvWatchlistCard(
    entry: WatchlistItem,
    serverOrigin: String,
    requestHeaders: Map<String, String>,
    focusColor: Color,
    focusMemory: TvFocusMemory,
    onClick: () -> Unit,
) {
    var focused by remember(entry.id) { mutableStateOf(false) }

    Button(
        onClick = onClick,
        modifier = Modifier
            .width(210.dp)
            .height(330.dp)
            .tvFocusIndication(focused, focusColor, MaterialTheme.shapes.small)
            .reportFocus { isFocused ->
                focused = isFocused
                if (isFocused) {
                    focusMemory.remember("watchlist", "item:${entry.id}")
                }
            },
    ) {
        Column(
            modifier = Modifier.fillMaxSize(),
            verticalArrangement = Arrangement.spacedBy(8.dp),
        ) {
            TvArtwork(
                url = entry.artworkUrl,
                serverOrigin = serverOrigin,
                requestHeaders = requestHeaders,
                contentDescription = entry.title,
                modifier = Modifier
                    .fillMaxWidth()
                    .height(225.dp)
                    .clip(MaterialTheme.shapes.small),
            )
            Text(
                text = entry.title,
                style = MaterialTheme.typography.titleMedium,
                maxLines = 2,
            )
            Row(horizontalArrangement = Arrangement.spacedBy(6.dp)) {
                TvInfoPill(mediaTypeLabel(entry.mediaType))
                if (entry.availability == "in_library") {
                    TvInfoPill(stringResource(R.string.tv_watchlist_in_library))
                }
            }
        }
    }
}

@Composable
private fun mediaTypeLabel(mediaType: String): String = when (mediaType) {
    "anime" -> stringResource(R.string.tv_watchlist_media_anime)
    "tv" -> stringResource(R.string.tv_watchlist_media_tv)
    "movie" -> stringResource(R.string.tv_watchlist_media_movie)
    "manga" -> stringResource(R.string.tv_watchlist_media_manga)
    "light-novel" -> stringResource(R.string.tv_watchlist_media_light_novel)
    "book" -> stringResource(R.string.tv_watchlist_media_book)
    else -> mediaType
}
