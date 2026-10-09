package de.juloc.jularr.tv

import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.lazy.grid.GridCells
import androidx.compose.foundation.lazy.grid.LazyVerticalGrid
import androidx.compose.foundation.lazy.grid.items
import androidx.compose.foundation.lazy.grid.rememberLazyGridState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.focus.FocusRequester
import androidx.compose.ui.focus.focusRequester
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.tv.material3.MaterialTheme
import androidx.tv.material3.Text
import de.juloc.jularr.core.model.AnimeSummary
import de.juloc.jularr.core.model.ClientLibrary

@Composable
fun TvSearchScreen(
    library: ClientLibrary,
    query: String,
    selectedFilter: TvContentFilter,
    serverOrigin: String,
    requestHeaders: Map<String, String>,
    focusMemory: TvFocusMemory,
    onQueryChange: (String) -> Unit,
    onFilterChange: (TvContentFilter) -> Unit,
    onAnime: (AnimeSummary) -> Unit,
) {
    val results = remember(library, query, selectedFilter) {
        TvSearchFilter.matches(library.anime, query, selectedFilter)
    }
    val focusColor = rememberTvFocusColor()
    val searchFocus = remember { FocusRequester() }
    val gridState = rememberLazyGridState()
    val lastFocus = remember { focusMemory.recall("search") }
    val restoreIndex = results.indexOfFirst { lastFocus == "anime:${it.id}" }

    LaunchedEffect(lastFocus) {
        if (restoreIndex < 0) runCatching { searchFocus.requestFocus() }
    }
    LaunchedEffect(restoreIndex) {
        if (restoreIndex >= 0) gridState.scrollToItem(restoreIndex)
    }

    Column(
        modifier = Modifier
            .fillMaxSize()
            .background(MaterialTheme.colorScheme.background)
            .padding(horizontal = 30.dp, vertical = 22.dp),
        verticalArrangement = Arrangement.spacedBy(16.dp),
    ) {
        Box(
            modifier = Modifier.fillMaxWidth().height(145.dp)
                .clip(RoundedCornerShape(16.dp)),
        ) {
            results.firstOrNull { !it.bannerImageUrl.isNullOrBlank() }?.let { highlighted ->
                TvArtwork(
                    url = highlighted.bannerImageUrl,
                    serverOrigin = serverOrigin,
                    requestHeaders = requestHeaders,
                    contentDescription = highlighted.title,
                    modifier = Modifier.fillMaxSize(),
                )
            }
            Box(
                modifier = Modifier.fillMaxSize().background(
                    Brush.horizontalGradient(
                        0.0f to MaterialTheme.colorScheme.background,
                        0.55f to MaterialTheme.colorScheme.background.copy(alpha = 0.8f),
                        1f to MaterialTheme.colorScheme.background.copy(alpha = 0.1f),
                    ),
                ),
            )
            Column(
                modifier = Modifier.align(Alignment.CenterStart).padding(16.dp),
                verticalArrangement = Arrangement.spacedBy(6.dp),
            ) {
                Text(
                    stringResource(R.string.tv_search_heading),
                    style = MaterialTheme.typography.displaySmall,
                    color = MaterialTheme.colorScheme.onSurface,
                )
                Text(
                    stringResource(R.string.tv_search_description),
                    style = MaterialTheme.typography.titleMedium,
                    color = MaterialTheme.colorScheme.onSurface.copy(alpha = 0.75f),
                )
            }
        }

        TvInput(
            value = query,
            onValueChange = onQueryChange,
            label = stringResource(R.string.tv_search_placeholder),
            placeholder = stringResource(R.string.tv_search_extended_placeholder),
            focusRequester = searchFocus,
            onFocusChanged = { if (it) focusMemory.remember("search", "search-field") },
        )
        TvFilterChipRow(
            selected = selectedFilter,
            focusMemory = focusMemory,
            screenKey = "search",
            onSelect = onFilterChange,
        )

        Text(
            stringResource(
                R.string.tv_search_results_count,
                results.size,
            ),
            color = MaterialTheme.colorScheme.onSurface,
            style = MaterialTheme.typography.titleLarge,
        )

        if (results.isEmpty()) {
            Text(
                if (query.isBlank()) stringResource(R.string.tv_search_empty_library)
                else stringResource(R.string.tv_search_no_results, query),
                style = MaterialTheme.typography.bodyLarge,
                color = MaterialTheme.colorScheme.onSurface.copy(alpha = 0.75f),
            )
        } else {
            LazyVerticalGrid(
                columns = GridCells.Adaptive(minSize = 230.dp),
                state = gridState,
                modifier = Modifier.fillMaxWidth().weight(1f),
                horizontalArrangement = Arrangement.spacedBy(14.dp),
                verticalArrangement = Arrangement.spacedBy(16.dp),
            ) {
                items(results, key = { it.id }) { anime ->
                    val itemFocus = remember(anime.id) { FocusRequester() }
                    LaunchedEffect(anime.id, lastFocus) {
                        if (lastFocus == "anime:${anime.id}") {
                            runCatching { itemFocus.requestFocus() }
                        }
                    }
                    var focused by remember(anime.id) { mutableStateOf(false) }
                    val shape = RoundedCornerShape(12.dp)
                    Column(
                        modifier = Modifier
                            .fillMaxWidth()
                            .clip(shape)
                            .background(MaterialTheme.colorScheme.surface)
                            .tvFocusIndication(focused, focusColor, shape)
                            .focusRequester(itemFocus)
                            .clickable { onAnime(anime) }
                            .reportFocus {
                                focused = it
                                if (it) focusMemory.remember("search", "anime:${anime.id}")
                            },
                    ) {
                        Box(Modifier.fillMaxWidth().aspectRatio(16f / 9f)) {
                            TvArtwork(
                                url = anime.bannerImageUrl ?: anime.coverImageUrl,
                                serverOrigin = serverOrigin,
                                requestHeaders = requestHeaders,
                                contentDescription = anime.title,
                                modifier = Modifier.fillMaxSize(),
                            )
                        }
                        Column(
                            modifier = Modifier.fillMaxWidth().padding(10.dp),
                            verticalArrangement = Arrangement.spacedBy(4.dp),
                        ) {
                            Text(
                                anime.title,
                                style = MaterialTheme.typography.titleMedium,
                                maxLines = 1,
                                overflow = TextOverflow.Ellipsis,
                            )
                            val description = listOfNotNull(
                                anime.seasonYear?.toString(),
                                anime.format,
                            ).joinToString(" · ")
                            if (description.isNotBlank()) {
                                Text(
                                    description,
                                    style = MaterialTheme.typography.bodySmall,
                                    color = MaterialTheme.colorScheme.onSurface.copy(alpha = 0.7f),
                                    maxLines = 1,
                                )
                            }
                        }
                    }
                }
            }
        }
    }
}
