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
import androidx.compose.runtime.mutableIntStateOf
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
import de.juloc.jularr.core.api.catalog.TvContinueItemV1
import de.juloc.jularr.core.api.catalog.TvDiscoverCatalogItemV1
import de.juloc.jularr.core.api.catalog.TvDiscoverItemV1
import de.juloc.jularr.core.model.AnimeSummary
import de.juloc.jularr.core.model.ClientLibrary
import de.juloc.jularr.core.model.ContinueWatchingItem
import kotlinx.coroutines.delay

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
    v1ContinueWatching: List<TvContinueItemV1> = emptyList(),
    onContinueWork: (TvContinueItemV1) -> Unit = {},
    showV1Continue: Boolean = true,
    shelfOrder: List<Int> = listOf(1, 4, 5),
    onLoadMoreVideos: (() -> Unit)? = null,
    discoverFeeds: List<TvV1HomeVideoFeed> = emptyList(),
    onDiscoverItem: (TvDiscoverItemV1) -> Unit = {},
    onDiscoverCatalogItem: (TvV1HomeVideoFeed, TvDiscoverCatalogItemV1) -> Unit = { _, _ -> },
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
    val recentlyAdded = selected.take(20)
    val visibleDiscoverFeeds = remember(discoverFeeds, filter) {
        discoverFeeds.filter { feed ->
            when (filter) {
                TvContentFilter.ALL -> true
                TvContentFilter.ANIME -> feed.mediaSubtypeId == 1
                TvContentFilter.SERIES -> feed.mediaTypeId == 2
                TvContentFilter.MOVIES -> feed.mediaTypeId == 1
            }
        }
    }
    val featured = selected.firstOrNull { !it.bannerImageUrl.isNullOrBlank() }
        ?: selected.firstOrNull()
    val heroCandidates = remember(visibleDiscoverFeeds) {
        tvV1HomeHeroCandidates(visibleDiscoverFeeds)
    }
    var heroIndex by remember(heroCandidates) { mutableIntStateOf(0) }
    var heroFocused by remember { mutableStateOf(false) }
    val selectedHero = heroCandidates.getOrNull(heroIndex)
    // The original TV hero remains a single focus surface; switching is paused
    // while the remote is focused on the hero, avoiding unexpected activations.
    LaunchedEffect(heroCandidates, heroFocused) {
        while (heroCandidates.size > 1 && !heroFocused) {
            delay(9_000L)
            heroIndex = (heroIndex + 1) % heroCandidates.size
        }
    }
    val formats = remember(library) { library.anime.associateBy { it.id } }
    val inProgress = remember(library, continueWatching, filter) {
        continueWatching.filter { item ->
            formats[item.animeId]?.let { filter.includes(it.format) } ?: (filter == TvContentFilter.ALL)
        }
    }
    val movies = selected.filter { it.format.equals("MOVIE", ignoreCase = true) || it.format.equals("ANIME_MOVIE", ignoreCase = true) }
    val series = selected.filterNot { it.format.equals("MOVIE", ignoreCase = true) || it.format.equals("ANIME_MOVIE", ignoreCase = true) }
    val rows = buildList {
        add("filters")
        if (featured != null || selectedHero != null) add("hero")
        if (error != null) add("error")
        shelfOrder.forEach { shelf ->
            when (shelf) {
                1 -> if (showV1Continue && v1ContinueWatching.isNotEmpty())
                    addAll(listOf("v1-continue-title", "v1-continue-row"))
                4 -> if (recentlyAdded.isNotEmpty()) addAll(listOf("recent-title", "recent-row"))
                5 -> visibleDiscoverFeeds.forEach { feed -> add("discover:${feed.key}") }
            }
        }
        if (inProgress.isNotEmpty()) addAll(listOf("continue-title", "continue-row"))
        if (series.isNotEmpty()) addAll(listOf("series-title", "series-row"))
        if (movies.isNotEmpty()) addAll(listOf("movies-title", "movies-row"))
        if (selected.isEmpty() && selectedHero == null) add("empty")
        add("search")
    }
    LaunchedEffect(restoringItem, rows) {
        val targetRow = when {
            restoringItem == "hero" || restoringItem?.startsWith("hero:") == true -> "hero"
            restoringItem?.startsWith("v1continue:") == true -> "v1-continue-row"
            restoringItem?.startsWith("continue:") == true -> "continue-row"
            restoringItem?.startsWith("discover:") == true -> visibleDiscoverFeeds
                .firstOrNull { restoringItem.startsWith("discover:${it.key}:") }
                ?.let { "discover:${it.key}" }
            restoringItem?.startsWith("catalog:") == true -> visibleDiscoverFeeds
                .firstOrNull { restoringItem.startsWith("catalog:${it.key}:") }
                ?.let { "discover:${it.key}" }
            restoringItem?.startsWith("anime:") == true -> {
                val id = restoringItem.removePrefix("anime:")
                if (movies.any { it.id == id }) "movies-row"
                else if (series.any { it.id == id }) "series-row"
                else null
            }
            else -> null
        }
        val index = targetRow?.let(rows::indexOf) ?: -1
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

            if (featured != null || selectedHero != null) {
                item(key = "hero") {
                    val heroFocus = remember { FocusRequester() }
                    LaunchedEffect(restoringItem) {
                        if (restoringItem == "hero" || restoringItem?.startsWith("hero:") == true) {
                            runCatching { heroFocus.requestFocus() }
                        }
                    }
                    val shape = RoundedCornerShape(20.dp)
                    val image = selectedHero?.imageUrl ?: featured?.bannerImageUrl ?: featured?.coverImageUrl
                    val title = selectedHero?.title ?: featured?.title.orEmpty()
                    val context = if (selectedHero != null) {
                        listOfNotNull(selectedHero.releaseYear?.toString(), selectedHero.subtitle)
                            .joinToString(" · ")
                    } else {
                        listOfNotNull(
                            featured?.seasonYear?.toString(), featured?.format,
                            featured?.episodeCount?.takeIf { it > 0 }?.let {
                                stringResource(R.string.tv_anime_episode_count, it)
                            },
                        ).joinToString(" · ")
                    }
                    Box(
                        modifier = Modifier
                            .fillMaxWidth()
                            .height(340.dp)
                            .clip(shape)
                            .tvFocusIndication(heroFocused, focusColor, shape)
                            .focusRequester(heroFocus)
                            .clickable {
                                val active = selectedHero
                                if (active != null) {
                                    active.discoverItem?.let(onDiscoverItem)
                                        ?: active.catalogItem?.let { cat ->
                                            active.catalogFeed?.let { feed ->
                                                onDiscoverCatalogItem(feed, cat)
                                            }
                                        }
                                } else featured?.let(onAnime)
                            }
                            .reportFocus {
                                heroFocused = it
                                if (it) focusMemory.remember("home", "hero")
                            },
                    ) {
                        TvArtwork(
                            url = image,
                            serverOrigin = serverOrigin,
                            requestHeaders = requestHeaders,
                            contentDescription = title,
                            modifier = Modifier.fillMaxSize(),
                        )
                        Box(
                            modifier = Modifier.fillMaxSize().background(
                                Brush.horizontalGradient(
                                    0.0f to Color.Black.copy(alpha = 0.85f),
                                    0.65f to Color.Black.copy(alpha = 0.22f),
                                    1.0f to Color.Transparent,
                                ),
                            ),
                        )
                        Column(
                            modifier = Modifier.align(Alignment.BottomStart)
                                .fillMaxWidth(0.72f).padding(28.dp),
                            verticalArrangement = Arrangement.spacedBy(12.dp),
                        ) {
                            Text(
                                text = title, color = Color.White,
                                style = MaterialTheme.typography.headlineLarge,
                                maxLines = 2, overflow = TextOverflow.Ellipsis,
                            )
                            Text(
                                text = context, color = Color.White.copy(alpha = 0.8f),
                                style = MaterialTheme.typography.bodyLarge,
                            )
                            selectedHero?.overview?.takeIf(String::isNotBlank)?.let { overview ->
                                Text(
                                    text = overview, color = Color.White.copy(alpha = 0.88f),
                                    style = MaterialTheme.typography.bodyMedium,
                                    maxLines = 3, overflow = TextOverflow.Ellipsis,
                                )
                            }
                            Row(verticalAlignment = Alignment.CenterVertically) {
                                Icon(Icons.Filled.PlayArrow, contentDescription = null, tint = Color.White)
                                Text(
                                    " " + stringResource(R.string.tv_home_hero_details),
                                    color = Color.White, style = MaterialTheme.typography.titleMedium,
                                )
                                if (heroCandidates.size > 1) {
                                    Text(
                                        "   ${heroIndex + 1} / ${heroCandidates.size}",
                                        color = Color.White.copy(alpha = 0.7f),
                                        style = MaterialTheme.typography.bodySmall,
                                    )
                                }
                            }
                        }
                    }
                }
            }

            error?.let { message ->
                item { Text(message, color = MaterialTheme.colorScheme.error) }
            }

            shelfOrder.forEach { shelf ->
                when (shelf) {
                    1 -> {
                        if (showV1Continue && v1ContinueWatching.isNotEmpty()) {
                            item(key = "v1-continue-title") {
                                Text(stringResource(R.string.tv_home_row_continue_watching), style = MaterialTheme.typography.titleLarge)
                            }
                            item(key = "v1-continue-row") {
                                val rowState = rememberLazyListState()
                                val restoreIndex = v1ContinueWatching.indexOfFirst {
                                    restoringItem == "v1continue:${it.mediaAssetId}"
                                }
                                LaunchedEffect(restoreIndex) {
                                    if (restoreIndex >= 0) rowState.scrollToItem(restoreIndex)
                                }
                                LazyRow(
                                    state = rowState,
                                    horizontalArrangement = Arrangement.spacedBy(16.dp),
                                ) {
                                    items(v1ContinueWatching, key = { it.mediaAssetId }) { entry ->
                                        val itemFocus = remember(entry.mediaAssetId) { FocusRequester() }
                                        LaunchedEffect(restoringItem, entry.mediaAssetId) {
                                            if (restoringItem == "v1continue:${entry.mediaAssetId}") {
                                                runCatching { itemFocus.requestFocus() }
                                            }
                                        }
                                        TvV1ContinueWatchingCard(
                                            item = entry,
                                            serverOrigin = serverOrigin,
                                            requestHeaders = requestHeaders,
                                            focusRequester = itemFocus,
                                            onFocused = {
                                                focusMemory.remember("home", "v1continue:${entry.mediaAssetId}")
                                            },
                                            onClick = { onContinueWork(entry) },
                                        )
                                    }
                                }
                            }
                        }


                    }
                    4 -> if (recentlyAdded.isNotEmpty()) {
                        item(key = "recent-title") {
                            Text("Zuletzt hinzugefügt", style = MaterialTheme.typography.titleLarge)
                        }
                        item(key = "recent-row") {
                            TvHomeAnimeRow(
                                entries = recentlyAdded,
                                restoringItem = restoringItem,
                                serverOrigin = serverOrigin,
                                requestHeaders = requestHeaders,
                                focusMemory = focusMemory,
                                focusColor = focusColor,
                                onAnime = onAnime,
                            )
                        }
                    }
                    5 -> visibleDiscoverFeeds.forEach { feed ->
                        item(key = "discover:${feed.key}") {
                            Column(verticalArrangement = Arrangement.spacedBy(12.dp)) {
                                Text(
                                    tvV1HomeFeedTitle(feed),
                                    style = MaterialTheme.typography.titleLarge,
                                )
                                if (feed.isCatalog) {
                                    TvV1HomeCatalogRow(
                                        feed = feed,
                                        serverOrigin = serverOrigin,
                                        requestHeaders = requestHeaders,
                                        focusMemory = focusMemory,
                                        restoringItem = restoringItem,
                                        onOpen = onDiscoverCatalogItem,
                                    )
                                } else {
                                    TvV1HomeDiscoverRow(
                                        feed = feed,
                                        serverOrigin = serverOrigin,
                                        requestHeaders = requestHeaders,
                                        focusMemory = focusMemory,
                                        restoringItem = restoringItem,
                                        onOpen = onDiscoverItem,
                                    )
                                }
                            }
                        }
                    }
                }
            }

            if (inProgress.isNotEmpty()) {
                item(key = "continue-title") {
                    Text(stringResource(R.string.tv_home_row_continue_watching), style = MaterialTheme.typography.titleLarge)
                }
                item(key = "continue-row") {
                    val rowState = rememberLazyListState()
                    val restoreIndex = inProgress.indexOfFirst { restoringItem == "continue:${it.episodeId}" }
                    LaunchedEffect(restoreIndex) {
                        if (restoreIndex >= 0) rowState.scrollToItem(restoreIndex)
                    }
                    LazyRow(
                        state = rowState,
                        horizontalArrangement = Arrangement.spacedBy(16.dp),
                    ) {
                        items(items = inProgress, key = { it.episodeId }) { entry ->
                            val itemFocus = remember(entry.episodeId) { FocusRequester() }
                            LaunchedEffect(restoringItem, entry.episodeId) {
                                if (restoringItem == "continue:${entry.episodeId}") {
                                    runCatching { itemFocus.requestFocus() }
                                }
                            }
                            ContinueWatchingCard(
                                item = entry,
                                serverOrigin = serverOrigin,
                                requestHeaders = requestHeaders,
                                focusRequester = itemFocus,
                                onFocused = { focusMemory.remember("home", "continue:${entry.episodeId}") },
                                onClick = { onContinueWatching(entry) },
                            )
                        }
                    }
                }
            }

            if (series.isNotEmpty()) {
                item(key = "series-title") { Text(stringResource(R.string.tv_home_row_series), style = MaterialTheme.typography.titleLarge) }
                item(key = "series-row") {
                    TvHomeAnimeRow(
                        entries = series,
                        restoringItem = restoringItem,
                        serverOrigin = serverOrigin,
                        requestHeaders = requestHeaders,
                        focusMemory = focusMemory,
                        focusColor = focusColor,
                        onAnime = onAnime,
                        onLoadMore = onLoadMoreVideos,
                    )
                }
            }

            if (movies.isNotEmpty()) {
                item(key = "movies-title") { Text(stringResource(R.string.tv_home_row_movies), style = MaterialTheme.typography.titleLarge) }
                item(key = "movies-row") {
                    TvHomeAnimeRow(
                        entries = movies,
                        restoringItem = restoringItem,
                        serverOrigin = serverOrigin,
                        requestHeaders = requestHeaders,
                        focusMemory = focusMemory,
                        focusColor = focusColor,
                        onAnime = onAnime,
                        onLoadMore = onLoadMoreVideos,
                    )
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
private fun TvHomeAnimeRow(
    entries: List<AnimeSummary>,
    restoringItem: String?,
    serverOrigin: String,
    requestHeaders: Map<String, String>,
    focusMemory: TvFocusMemory,
    focusColor: Color,
    onAnime: (AnimeSummary) -> Unit,
    onLoadMore: (() -> Unit)? = null,
) {
    val rowState = rememberLazyListState()
    val restoreIndex = entries.indexOfFirst { restoringItem == "anime:${it.id}" }
    LaunchedEffect(restoreIndex) {
        if (restoreIndex >= 0) rowState.scrollToItem(restoreIndex)
    }
    LazyRow(
        state = rowState,
        horizontalArrangement = Arrangement.spacedBy(18.dp),
    ) {
        items(items = entries, key = { it.id }) { anime ->
            val itemFocus = remember(anime.id) { FocusRequester() }
            LaunchedEffect(restoringItem, anime.id) {
                if (restoringItem == "anime:${anime.id}") {
                    runCatching { itemFocus.requestFocus() }
                }
            }
            var focused by remember(anime.id) { mutableStateOf(false) }
            AnimeButton(
                anime = anime,
                serverOrigin = serverOrigin,
                requestHeaders = requestHeaders,
                focused = focused,
                focusColor = focusColor,
                focusRequester = itemFocus,
                onFocusChanged = {
                    focused = it
                    if (it) focusMemory.remember("home", "anime:${anime.id}")
                },
                onClick = { onAnime(anime) },
            )
        }
        if (onLoadMore != null) {
            item(key = "load-more") {
                Button(onClick = onLoadMore) { Text("Weitere Medien laden") }
            }
        }
    }
}

@Composable
private fun ContinueWatchingCard(
    item: ContinueWatchingItem,
    serverOrigin: String,
    requestHeaders: Map<String, String>,
    focusRequester: FocusRequester,
    onFocused: () -> Unit,
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
            .focusRequester(focusRequester)
            .clickable(onClick = onClick)
            .reportFocus {
                focused = it
                if (it) onFocused()
            },
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
