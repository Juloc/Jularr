package de.juloc.jularr.tv

import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.LazyRow
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.lazy.rememberLazyListState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.PlayArrow
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
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.tv.material3.Button
import androidx.tv.material3.Icon
import androidx.tv.material3.MaterialTheme
import androidx.tv.material3.Surface
import androidx.tv.material3.SurfaceDefaults
import androidx.tv.material3.Text
import de.juloc.jularr.core.model.AnimeSummary
import de.juloc.jularr.core.model.ClientLibrary
import de.juloc.jularr.core.model.ContinueWatchingItem

@Composable
fun TvHomeScreen(
    library: ClientLibrary,
    continueWatching: List<ContinueWatchingItem>,
    serverOrigin: String,
    requestHeaders: Map<String, String>,
    error: String?,
    focusMemory: TvFocusMemory,
    onOpenSearch: () -> Unit,
    onAnime: (AnimeSummary) -> Unit,
    onContinueWatching: (ContinueWatchingItem) -> Unit,
) {
    var filter by remember {
        mutableStateOf(
            TvContentFilter.entries.firstOrNull {
                it.name == focusMemory.recall("home-filter")
            } ?: TvContentFilter.ALL,
        )
    }
    val restoringItem = remember { focusMemory.recall("home") }
    val listState = rememberLazyListState()
    val focusColor = rememberTvFocusColor()
    val selected = remember(library, filter) {
        library.anime.filter { filter.includes(it.format) }
    }
    val featured = selected.firstOrNull { !it.bannerImageUrl.isNullOrBlank() }
        ?: selected.firstOrNull()
    val formats = remember(library) { library.anime.associateBy { it.id } }
    val inProgress = remember(library, continueWatching, filter) {
        continueWatching.filter { item ->
            formats[item.animeId]?.let { filter.includes(it.format) } ?: (filter == TvContentFilter.ALL)
        }
    }
    val movies = selected.filter { it.format.equals("MOVIE", ignoreCase = true) }
    val series = selected.filterNot { it.format.equals("MOVIE", ignoreCase = true) }
    val rows = buildList {
        add("filters")
        if (featured != null) add("hero")
        if (error != null) add("error")
        if (inProgress.isNotEmpty()) addAll(listOf("continue-title", "continue-row"))
        if (series.isNotEmpty()) addAll(listOf("series-title", "series-row"))
        if (movies.isNotEmpty()) addAll(listOf("movies-title", "movies-row"))
        if (selected.isEmpty()) add("empty")
        add("search")
    }
    LaunchedEffect(restoringItem) {
        val targetRow = when {
            restoringItem == "hero:${featured?.id}" -> "hero"
            restoringItem?.startsWith("continue:") == true -> "continue-row"
            restoringItem?.startsWith("anime:") == true -> {
                val id = restoringItem.removePrefix("anime:")
                if (movies.any { it.id == id }) "movies-row"
                else if (series.any { it.id == id }) "series-row"
                else null
            }
            else -> null
        }
        val index = rows.indexOf(targetRow)
        if (index >= 0) listState.scrollToItem(index)
    }

    Surface(
        modifier = Modifier.fillMaxSize(),
        colors = SurfaceDefaults.colors(containerColor = Color(0xFF0B0D14)),
    ) {
        LazyColumn(
            state = listState,
            modifier = Modifier
                .fillMaxSize()
                .padding(horizontal = 32.dp, vertical = 20.dp),
            verticalArrangement = Arrangement.spacedBy(20.dp),
        ) {
            item {
                TvFilterChipRow(
                    selected = filter,
                    focusMemory = focusMemory,
                    screenKey = "home",
                    onSelect = {
                        filter = it
                        focusMemory.remember("home-filter", it.name)
                    },
                )
            }

            if (featured != null) {
                item(key = "hero:${featured.id}") {
                    val heroFocus = remember(featured.id) { FocusRequester() }
                    LaunchedEffect(restoringItem, featured.id) {
                        if (restoringItem == "hero:${featured.id}") {
                            runCatching { heroFocus.requestFocus() }
                        }
                    }
                    var focused by remember(featured.id) { mutableStateOf(false) }
                    val shape = RoundedCornerShape(20.dp)
                    Box(
                        modifier = Modifier
                            .fillMaxWidth()
                            .height(340.dp)
                            .clip(shape)
                            .tvFocusIndication(focused, focusColor, shape)
                            .focusRequester(heroFocus)
                            .clickable { onAnime(featured) }
                            .reportFocus {
                                focused = it
                                if (it) focusMemory.remember("home", "hero:${featured.id}")
                            },
                    ) {
                        TvArtwork(
                            url = featured.bannerImageUrl ?: featured.coverImageUrl,
                            serverOrigin = serverOrigin,
                            requestHeaders = requestHeaders,
                            contentDescription = featured.title,
                            modifier = Modifier.fillMaxSize(),
                        )
                        Box(
                            modifier = Modifier
                                .fillMaxSize()
                                .background(
                                    Brush.horizontalGradient(
                                        0.0f to Color.Black.copy(alpha = 0.85f),
                                        0.65f to Color.Black.copy(alpha = 0.22f),
                                        1.0f to Color.Transparent,
                                    ),
                                ),
                        )
                        Column(
                            modifier = Modifier
                                .align(Alignment.BottomStart)
                                .fillMaxWidth(0.72f)
                                .padding(28.dp),
                            verticalArrangement = Arrangement.spacedBy(12.dp),
                        ) {
                            Text(
                                text = featured.title,
                                color = Color.White,
                                style = MaterialTheme.typography.headlineLarge,
                                maxLines = 2,
                                overflow = TextOverflow.Ellipsis,
                            )
                            Text(
                                text = listOfNotNull(
                                    featured.seasonYear?.toString(),
                                    featured.format,
                                    stringResource(R.string.tv_anime_episode_count, featured.episodeCount),
                                ).joinToString(" · "),
                                color = Color.White.copy(alpha = 0.8f),
                                style = MaterialTheme.typography.bodyLarge,
                            )
                            Row(verticalAlignment = Alignment.CenterVertically) {
                                Icon(Icons.Filled.PlayArrow, contentDescription = null, tint = Color.White)
                                Text(
                                    " " + stringResource(R.string.tv_home_hero_details),
                                    color = Color.White,
                                    style = MaterialTheme.typography.titleMedium,
                                )
                            }
                        }
                    }
                }
            }

            error?.let { message ->
                item { Text(message, color = MaterialTheme.colorScheme.error) }
            }

            if (inProgress.isNotEmpty()) {
                item(key = "continue-title") {
                    Text(stringResource(R.string.tv_home_row_continue_watching), style = MaterialTheme.typography.titleLarge)
                }
                item(key = "continue-row") {
                    LazyRow(horizontalArrangement = Arrangement.spacedBy(16.dp)) {
                        items(items = inProgress, key = { it.episodeId }) { entry ->
                            ContinueWatchingCard(
                                item = entry,
                                serverOrigin = serverOrigin,
                                requestHeaders = requestHeaders,
                                onClick = { onContinueWatching(entry) },
                            )
                        }
                    }
                }
            }

            if (series.isNotEmpty()) {
                item(key = "series-title") { Text(stringResource(R.string.tv_home_row_series), style = MaterialTheme.typography.titleLarge) }
                item(key = "series-row") {
                    LazyRow(horizontalArrangement = Arrangement.spacedBy(18.dp)) {
                        items(items = series, key = { it.id }) { anime ->
                            var focused by remember(anime.id) { mutableStateOf(false) }
                            AnimeButton(
                                anime = anime,
                                serverOrigin = serverOrigin,
                                requestHeaders = requestHeaders,
                                focused = focused,
                                focusColor = focusColor,
                                onFocusChanged = {
                                    focused = it
                                    if (it) focusMemory.remember("home", "anime:${anime.id}")
                                },
                                onClick = { onAnime(anime) },
                            )
                        }
                    }
                }
            }

            if (movies.isNotEmpty()) {
                item(key = "movies-title") { Text(stringResource(R.string.tv_home_row_movies), style = MaterialTheme.typography.titleLarge) }
                item(key = "movies-row") {
                    LazyRow(horizontalArrangement = Arrangement.spacedBy(18.dp)) {
                        items(items = movies, key = { it.id }) { anime ->
                            var focused by remember(anime.id) { mutableStateOf(false) }
                            AnimeButton(
                                anime = anime,
                                serverOrigin = serverOrigin,
                                requestHeaders = requestHeaders,
                                focused = focused,
                                focusColor = focusColor,
                                onFocusChanged = {
                                    focused = it
                                    if (it) focusMemory.remember("home", "anime:${anime.id}")
                                },
                                onClick = { onAnime(anime) },
                            )
                        }
                    }
                }
            }

            if (selected.isEmpty()) {
                item {
                    Text(
                        if (library.anime.isEmpty()) stringResource(R.string.tv_home_empty)
                        else stringResource(R.string.tv_home_no_media_of_type),
                        style = MaterialTheme.typography.bodyLarge,
                    )
                }
            }

            item {
                Button(onClick = onOpenSearch) { Text(stringResource(R.string.tv_home_browse)) }
            }
        }
    }
}

@Composable
private fun ContinueWatchingCard(
    item: ContinueWatchingItem,
    serverOrigin: String,
    requestHeaders: Map<String, String>,
    onClick: () -> Unit,
) {
    val focusColor = rememberTvFocusColor()
    var focused by remember(item.episodeId) { mutableStateOf(false) }
    val cardShape = RoundedCornerShape(14.dp)
    val cardBackground = if (focused) focusColor.copy(alpha = 0.28f) else Color(0xFF121520)

    Box(
        modifier = Modifier
            .width(280.dp)
            .clip(cardShape)
            .background(cardBackground)
            .tvFocusIndication(focused, focusColor, cardShape)
            .clickable(onClick = onClick)
            .reportFocus { focused = it },
    ) {
        Column(modifier = Modifier.fillMaxWidth()) {
            TvArtwork(
                url = item.coverImageUrl,
                serverOrigin = serverOrigin,
                requestHeaders = requestHeaders,
                contentDescription = item.animeTitle,
                modifier = Modifier
                    .fillMaxWidth()
                    .aspectRatio(16f / 9f)
                    .clip(RoundedCornerShape(topStart = 14.dp, topEnd = 14.dp)),
            )

            Column(
                modifier = Modifier
                    .fillMaxWidth()
                    .padding(12.dp),
                verticalArrangement = Arrangement.spacedBy(6.dp),
            ) {
                Text(
                    text = item.animeTitle,
                    style = TextStyle(
                        color = Color.White,
                        fontSize = 15.sp,
                        fontWeight = FontWeight.Bold,
                    ),
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis,
                )
                Text(
                    text = stringResource(
                        R.string.tv_episode_season_and_number,
                        item.seasonNumber,
                        item.episodeNumber,
                    ) + " · ${formatEpisodePosition(item.resumePositionMs)}",
                    style = TextStyle(
                        color = Color.White.copy(alpha = 0.65f),
                        fontSize = 12.sp,
                    ),
                    maxLines = 1,
                )

                val progressFraction = (item.percent / 100f).coerceIn(0f, 1f)
                Row(
                    modifier = Modifier.fillMaxWidth(),
                    verticalAlignment = Alignment.CenterVertically,
                    horizontalArrangement = Arrangement.spacedBy(8.dp),
                ) {
                    Box(
                        modifier = Modifier
                            .weight(1f)
                            .height(4.dp)
                            .clip(RoundedCornerShape(2.dp))
                            .background(Color(0xFF2A2E3D)),
                    ) {
                        Box(
                            modifier = Modifier
                                .fillMaxHeight()
                                .fillMaxWidth(progressFraction)
                                .background(focusColor),
                        )
                    }
                    Text(
                        text = "${item.percent}%",
                        style = TextStyle(
                            color = Color.White.copy(alpha = 0.65f),
                            fontSize = 11.sp,
                        ),
                    )
                }
            }
        }
    }
}
