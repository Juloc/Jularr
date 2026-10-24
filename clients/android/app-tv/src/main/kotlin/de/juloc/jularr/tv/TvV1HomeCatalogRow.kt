package de.juloc.jularr.tv

import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.Column
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
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.tv.material3.MaterialTheme
import androidx.tv.material3.Text
import de.juloc.jularr.core.api.catalog.TvDiscoverCatalogItemV1

/**
 * Catalog-backed video scopes (for example Anime) use provider namespaces and
 * external IDs. These are distinct from TMDB IDs and Jularr WorkIds.
 */
internal fun tvV1HomeCatalogFocusKey(
    feed: TvV1HomeVideoFeed,
    item: TvDiscoverCatalogItemV1,
) = "catalog:${feed.key}:${item.namespace}:${item.externalId}"

@Composable
internal fun TvV1HomeCatalogRow(
    feed: TvV1HomeVideoFeed,
    serverOrigin: String,
    requestHeaders: Map<String, String>,
    focusMemory: TvFocusMemory,
    restoringItem: String?,
    onOpen: (TvV1HomeVideoFeed, TvDiscoverCatalogItemV1) -> Unit,
) {
    val focusColor = rememberTvFocusColor()
    val state = rememberLazyListState()
    val restoreIndex = feed.catalogItems.indexOfFirst {
        tvV1HomeCatalogFocusKey(feed, it) == restoringItem
    }
    LaunchedEffect(restoreIndex) {
        if (restoreIndex >= 0) state.scrollToItem(restoreIndex)
    }
    LazyRow(
        state = state,
        horizontalArrangement = Arrangement.spacedBy(18.dp),
    ) {
        items(feed.catalogItems, key = { "${it.namespace}:${it.externalId}" }) { item ->
            val id = tvV1HomeCatalogFocusKey(feed, item)
            val requester = remember(id) { FocusRequester() }
            var focused by remember(id) { mutableStateOf(false) }
            LaunchedEffect(id, restoringItem) {
                if (restoringItem == id) runCatching { requester.requestFocus() }
            }
            val shape = RoundedCornerShape(14.dp)
            Column(
                modifier = Modifier.width(174.dp).clip(shape)
                    .tvFocusIndication(focused, focusColor, shape)
                    .focusRequester(requester)
                    .clickable { onOpen(feed, item) }
                    .reportFocus {
                        focused = it
                        if (it) focusMemory.remember("home", id)
                    }
                    .padding(4.dp),
                verticalArrangement = Arrangement.spacedBy(8.dp),
            ) {
                TvArtwork(
                    url = item.posterUrl,
                    serverOrigin = serverOrigin,
                    requestHeaders = requestHeaders,
                    contentDescription = item.title,
                    modifier = Modifier.fillMaxWidth().aspectRatio(2f / 3f),
                )
                Text(
                    item.title,
                    style = MaterialTheme.typography.titleSmall,
                    maxLines = 2,
                    overflow = TextOverflow.Ellipsis,
                )
                Text(
                    listOfNotNull(item.releaseYear?.toString(), item.author).joinToString(" · "),
                    style = MaterialTheme.typography.bodySmall,
                    maxLines = 1,
                )
            }
        }
    }
}
