package de.juloc.jularr.tv

import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyRow
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.lazy.rememberLazyListState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.focus.FocusRequester
import androidx.compose.ui.focus.focusRequester
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.tv.material3.MaterialTheme
import androidx.tv.material3.Text
import de.juloc.jularr.core.api.catalog.TvDiscoverItemV1
import de.juloc.jularr.core.api.catalog.TvDiscoverScopeV1
import de.juloc.jularr.core.api.catalog.TvDiscoverShelfV1

/** Canonical provider scope/ID data; a TMDB ID is NEVER a WorkId. */
data class TvV1HomeVideoFeed(
    val scopeSlug: String,
    val mediaTypeId: Int,
    val mediaSubtypeId: Int?,
    val feedTypeId: Int,
    val items: List<TvDiscoverItemV1>,
) {
    val key: String get() = "$scopeSlug:$feedTypeId"
}

internal fun tvV1HomeVideoFeeds(
    scopes: List<TvDiscoverScopeV1>,
    shelves: List<TvDiscoverShelfV1>,
): List<TvV1HomeVideoFeed> =
    scopes.filter { (it.mediaTypeId == 1 || it.mediaTypeId == 2) && !it.isCatalog }
        .flatMap { scope ->
            scope.feeds.filter { it in 1..4 }.distinct().sorted().mapNotNull { feed ->
                val matches = shelves.firstOrNull {
                    it.scopeSlug == scope.slug && it.discoverFeedTypeId == feed
                }?.items.orEmpty().filter {
                    it.mediaTypeId == scope.mediaTypeId &&
                        (it.workId != null || it.tmdbId != null)
                }
                if (matches.isEmpty()) null else TvV1HomeVideoFeed(
                    scope.slug, scope.mediaTypeId, scope.mediaSubtypeId, feed,
                    matches.distinctBy { Triple(it.mediaTypeId, it.workId, it.tmdbId) },
                )
            }
        }

internal fun tvV1HomeFeedTitle(feed: TvV1HomeVideoFeed): String {
    val category = when {
        feed.mediaSubtypeId == 1 -> "Anime"
        feed.mediaTypeId == 1 -> "Filme"
        else -> "Serien"
    }
    val ranking = when (feed.feedTypeId) {
        1 -> "Trending"
        2 -> "Top"
        3 -> "Neu"
        4 -> "Demnächst"
        else -> error("Unsupported feed type")
    }
    return "$category · $ranking"
}

@Composable
internal fun TvV1HomeDiscoverRow(
    feed: TvV1HomeVideoFeed,
    serverOrigin: String,
    requestHeaders: Map<String, String>,
    focusMemory: TvFocusMemory,
    restoringItem: String?,
    onOpen: (TvDiscoverItemV1) -> Unit,
) {
    val color = rememberTvFocusColor()
    val rowState = rememberLazyListState()
    val selected = feed.items.indexOfFirst {
        restoringItem == "discover:${feed.key}:${it.mediaTypeId}:${it.workId ?: "tmdb-${it.tmdbId}"}"
    }
    LaunchedEffect(selected) {
        if (selected >= 0) rowState.scrollToItem(selected)
    }
    LazyRow(
        state = rowState,
        horizontalArrangement = Arrangement.spacedBy(18.dp),
    ) {
        items(feed.items, key = {
            "${it.mediaTypeId}:${it.workId ?: "tmdb-${it.tmdbId}"}"
        }) { item ->
            val id = "discover:${feed.key}:${item.mediaTypeId}:${item.workId ?: "tmdb-${item.tmdbId}"}"
            val requester = remember(id) { FocusRequester() }
            var focused by remember(id) { mutableStateOf(false) }
            LaunchedEffect(id, restoringItem) {
                if (restoringItem == id) runCatching { requester.requestFocus() }
            }
            val shape = RoundedCornerShape(14.dp)
            Column(
                modifier = Modifier.width(174.dp)
                    .clip(shape)
                    .tvFocusIndication(focused, color, shape)
                    .focusRequester(requester)
                    .clickable { onOpen(item) }
                    .reportFocus {
                        focused = it
                        if (it) focusMemory.remember("home", id)
                    }
                    .padding(4.dp),
                verticalArrangement = Arrangement.spacedBy(8.dp),
            ) {
                Box(Modifier.fillMaxWidth().aspectRatio(2f / 3f)) {
                    TvArtwork(
                        url = item.posterUrl ?: item.backdropUrl,
                        serverOrigin = serverOrigin,
                        requestHeaders = requestHeaders,
                        contentDescription = item.title,
                        modifier = Modifier.fillMaxWidth().aspectRatio(2f / 3f),
                    )
                }
                Text(
                    item.title,
                    style = MaterialTheme.typography.titleSmall,
                    maxLines = 2,
                    overflow = TextOverflow.Ellipsis,
                )
                Text(
                    listOfNotNull(item.releaseYear?.toString(), if (item.workId != null) "In Bibliothek" else null).joinToString(" · "),
                    style = MaterialTheme.typography.bodySmall,
                    color = Color.LightGray,
                    maxLines = 1,
                )
            }
        }
    }
}
