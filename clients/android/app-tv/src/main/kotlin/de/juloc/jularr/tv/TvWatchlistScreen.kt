package de.juloc.jularr.tv

import androidx.activity.compose.BackHandler
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyRow
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.lazy.grid.GridCells
import androidx.compose.foundation.lazy.grid.LazyVerticalGrid
import androidx.compose.foundation.lazy.grid.items as gridItems
import androidx.compose.foundation.lazy.grid.rememberLazyGridState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.Sort
import androidx.compose.material.icons.filled.CheckCircle
import androidx.compose.material.icons.filled.KeyboardArrowDown
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
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.tv.material3.Button
import androidx.tv.material3.Icon
import androidx.tv.material3.MaterialTheme
import androidx.tv.material3.Surface
import androidx.tv.material3.Text
import de.juloc.jularr.core.model.ContinueWatchingItem
import de.juloc.jularr.core.model.WatchlistItem
import java.util.Locale

internal enum class TvWatchlistCategory(val labelRes: Int) {
    ALL(R.string.tv_home_filter_all),
    ANIME(R.string.tv_home_filter_anime),
    SERIES(R.string.tv_home_filter_series),
    MOVIES(R.string.tv_home_filter_movies),
    BOOKS(R.string.tv_watchlist_filter_books);

    fun includes(mediaType: String): Boolean = when (this) {
        ALL -> true
        ANIME -> mediaType.equals("anime", ignoreCase = true)
        SERIES -> mediaType.equals("tv", ignoreCase = true)
        MOVIES -> mediaType.equals("movie", ignoreCase = true)
        BOOKS -> mediaType.equals("book", ignoreCase = true) ||
            mediaType.equals("light-novel", ignoreCase = true)
    }
}

internal enum class TvWatchlistSort(val labelRes: Int) {
    NEWEST(R.string.tv_watchlist_sort_newest),
    OLDEST(R.string.tv_watchlist_sort_oldest),
    TITLE_ASC(R.string.tv_watchlist_sort_title_asc),
    TITLE_DESC(R.string.tv_watchlist_sort_title_desc),
}

internal fun watchlistVisibleItems(
    entries: List<WatchlistItem>,
    category: TvWatchlistCategory,
    sort: TvWatchlistSort,
): List<WatchlistItem> {
    val filtered = entries.filter { category.includes(it.mediaType) }
    return when (sort) {
        TvWatchlistSort.NEWEST -> filtered.sortedWith(
            compareByDescending<WatchlistItem> { it.addedAtUtc }
                .thenBy { it.title.lowercase(Locale.ROOT) },
        )
        TvWatchlistSort.OLDEST -> filtered.sortedWith(
            compareBy<WatchlistItem> { it.addedAtUtc == null }
                .thenBy { it.addedAtUtc }
                .thenBy { it.title.lowercase(Locale.ROOT) },
        )
        TvWatchlistSort.TITLE_ASC -> filtered.sortedWith(
            compareBy<WatchlistItem> { it.title.lowercase(Locale.ROOT) }.thenBy { it.id },
        )
        TvWatchlistSort.TITLE_DESC -> filtered.sortedWith(
            compareByDescending<WatchlistItem> { it.title.lowercase(Locale.ROOT) }.thenBy { it.id },
        )
    }
}

internal fun watchlistStatusLabel(status: String?): Int? = when (status?.trim()?.uppercase(Locale.ROOT)) {
    "RELEASING", "AIRING", "CURRENTLY_RELEASING" -> R.string.tv_watchlist_status_airing
    "RETURNING_SERIES", "CONTINUING", "IN_PRODUCTION" -> R.string.tv_watchlist_status_continuing
    "FINISHED", "ENDED" -> R.string.tv_watchlist_status_ended
    "NOT_YET_RELEASED", "NOT_YET_AIRED", "UPCOMING" -> R.string.tv_watchlist_status_upcoming
    "HIATUS" -> R.string.tv_watchlist_status_hiatus
    "CANCELLED", "CANCELED" -> R.string.tv_watchlist_status_cancelled
    else -> null
}

@Composable
fun TvWatchlistScreen(
    entries: List<WatchlistItem>,
    supported: Boolean,
    continueWatching: List<ContinueWatchingItem>,
    serverOrigin: String,
    requestHeaders: Map<String, String>,
    focusMemory: TvFocusMemory,
    onOpenAnime: (animeId: String) -> Unit,
) {
    if (!supported) {
        TvCenteredPanel(
            title = stringResource(R.string.tv_watchlist_title),
            description = stringResource(R.string.tv_watchlist_unavailable),
            content = {},
        )
        return
    }

    val design = TvPlayerDesignLoader.load(androidx.compose.ui.platform.LocalContext.current)
    val focusColor = rememberTvFocusColor()
    var category by remember {
        mutableStateOf(
            TvWatchlistCategory.entries.firstOrNull {
                it.name == focusMemory.recall("watchlist-filter")
            } ?: TvWatchlistCategory.ALL,
        )
    }
    var sort by remember {
        mutableStateOf(
            TvWatchlistSort.entries.firstOrNull {
                it.name == focusMemory.recall("watchlist-sort")
            } ?: TvWatchlistSort.NEWEST,
        )
    }
    var sortOpen by remember { mutableStateOf(false) }
    var sortPreviouslyOpened by remember { mutableStateOf(false) }
    var infoEntry by remember { mutableStateOf<WatchlistItem?>(null) }
    val sortFocus = remember { FocusRequester() }
    val optionFocus = remember { FocusRequester() }
    val restoreId = remember { focusMemory.recall("watchlist") }
    val gridState = rememberLazyGridState()
    val visible = remember(entries, category, sort) {
        watchlistVisibleItems(entries, category, sort)
    }
    val progressByAnime = remember(continueWatching) {
        continueWatching.associateBy { it.animeId }
    }
    val restoreIndex = visible.indexOfFirst { "item:${it.id}" == restoreId }

    LaunchedEffect(restoreIndex, category, sort) {
        if (restoreIndex >= 0) gridState.scrollToItem(restoreIndex)
        else gridState.scrollToItem(0)
    }
    LaunchedEffect(sortOpen) {
        if (sortOpen) {
            sortPreviouslyOpened = true
            runCatching { optionFocus.requestFocus() }
        } else if (sortPreviouslyOpened) {
            runCatching { sortFocus.requestFocus() }
        }
    }
    BackHandler(enabled = sortOpen || infoEntry != null) {
        if (infoEntry != null) infoEntry = null else sortOpen = false
    }

    Surface(modifier = Modifier.fillMaxSize()) {
        Box(Modifier.fillMaxSize().background(Color(0xFF0B0D16))) {
            Column(
                modifier = Modifier.fillMaxSize().padding(horizontal = 30.dp, vertical = 24.dp),
                verticalArrangement = Arrangement.spacedBy(18.dp),
            ) {
                Row(
                    modifier = Modifier.fillMaxWidth(),
                    verticalAlignment = Alignment.CenterVertically,
                    horizontalArrangement = Arrangement.spacedBy(16.dp),
                ) {
                    Text(
                        text = stringResource(R.string.tv_watchlist_title),
                        style = MaterialTheme.typography.displaySmall,
                        color = Color.White,
                        modifier = Modifier.weight(1f),
                    )
                    Button(
                        onClick = { sortOpen = true },
                        modifier = Modifier.focusRequester(sortFocus),
                    ) {
                        Icon(
                            Icons.AutoMirrored.Filled.Sort,
                            contentDescription = null,
                            modifier = Modifier.size(19.dp),
                        )
                        Text("  " + stringResource(sort.labelRes))
                        Icon(Icons.Filled.KeyboardArrowDown, contentDescription = null)
                    }
                }

                LazyRow(horizontalArrangement = Arrangement.spacedBy(10.dp)) {
                    items(TvWatchlistCategory.entries.toList(), key = { it.name }) { option ->
                        var focused by remember(option) { mutableStateOf(false) }
                        val shape = RoundedCornerShape(24.dp)
                        Box(
                            modifier = Modifier
                                .clip(shape)
                                .background(
                                    if (category == option) design.accent
                                    else if (focused) design.accent.copy(alpha = 0.28f)
                                    else MaterialTheme.colorScheme.surfaceVariant,
                                )
                                .tvFocusIndication(focused, focusColor, shape)
                                .clickable {
                                    category = option
                                    focusMemory.remember("watchlist-filter", option.name)
                                }
                                .reportFocus { focused = it }
                                .padding(horizontal = 21.dp, vertical = 10.dp),
                        ) {
                            Text(
                                stringResource(option.labelRes),
                                color = Color.White,
                                style = MaterialTheme.typography.titleSmall,
                            )
                        }
                    }
                }

                if (entries.isEmpty()) {
                    TvCenteredPanel(
                        title = stringResource(R.string.tv_watchlist_title),
                        description = stringResource(R.string.tv_watchlist_empty),
                        content = {},
                    )
                } else if (visible.isEmpty()) {
                    TvCenteredPanel(
                        title = stringResource(R.string.tv_watchlist_title),
                        description = stringResource(R.string.tv_watchlist_filter_empty),
                        content = {},
                    )
                } else {
                    LazyVerticalGrid(
                        columns = GridCells.Adaptive(minSize = 230.dp),
                        state = gridState,
                        modifier = Modifier.weight(1f).fillMaxWidth(),
                        horizontalArrangement = Arrangement.spacedBy(12.dp),
                        verticalArrangement = Arrangement.spacedBy(14.dp),
                    ) {
                        gridItems(visible, key = { it.id }) { entry ->
                            val requester = remember(entry.id) { FocusRequester() }
                            LaunchedEffect(entry.id, restoreId, category, sort) {
                                if ("item:${entry.id}" == restoreId) {
                                    runCatching { requester.requestFocus() }
                                }
                            }
                            TvWatchlistCard(
                                entry = entry,
                                progress = entry.localMediaId
                                    ?.takeIf { entry.mediaType.equals("anime", ignoreCase = true) }
                                    ?.let(progressByAnime::get),
                                serverOrigin = serverOrigin,
                                requestHeaders = requestHeaders,
                                focusColor = focusColor,
                                accent = design.accent,
                                focusRequester = requester,
                                onFocused = { focusMemory.remember("watchlist", "item:${entry.id}") },
                                onClick = {
                                    if (entry.mediaType.equals("anime", ignoreCase = true) &&
                                        entry.localMediaId != null
                                    ) {
                                        onOpenAnime(entry.localMediaId)
                                    } else {
                                        infoEntry = entry
                                    }
                                },
                            )
                        }
                    }
                }
            }

            if (sortOpen) {
                Box(
                    Modifier.fillMaxSize().padding(top = 84.dp, end = 30.dp),
                    contentAlignment = Alignment.TopEnd,
                ) {
                    Column(
                        modifier = Modifier
                            .width(290.dp)
                            .border(1.dp, focusColor.copy(alpha = 0.25f), RoundedCornerShape(16.dp))
                            .clip(RoundedCornerShape(16.dp))
                            .background(MaterialTheme.colorScheme.surface)
                            .padding(12.dp),
                        verticalArrangement = Arrangement.spacedBy(6.dp),
                    ) {
                        TvWatchlistSort.entries.forEach { option ->
                            Button(
                                modifier = Modifier.fillMaxWidth().then(
                                    if (option == sort) Modifier.focusRequester(optionFocus) else Modifier,
                                ),
                                onClick = {
                                    sort = option
                                    focusMemory.remember("watchlist-sort", option.name)
                                    sortOpen = false
                                },
                            ) {
                                Text(stringResource(option.labelRes))
                            }
                        }
                    }
                }
            }

            infoEntry?.let { item ->
                Box(
                    modifier = Modifier.fillMaxSize()
                        .background(Color.Black.copy(alpha = 0.55f)),
                    contentAlignment = Alignment.Center,
                ) {
                    Column(
                        modifier = Modifier.width(430.dp)
                            .clip(RoundedCornerShape(18.dp))
                            .background(MaterialTheme.colorScheme.surface)
                            .padding(28.dp),
                        verticalArrangement = Arrangement.spacedBy(16.dp),
                    ) {
                        Text(item.title, style = MaterialTheme.typography.headlineSmall)
                        Text(
                            stringResource(
                                if (item.availability == "in_library")
                                    R.string.tv_watchlist_unsupported_media
                                else R.string.tv_watchlist_not_in_library,
                            ),
                            style = MaterialTheme.typography.bodyLarge,
                        )
                        Button(onClick = { infoEntry = null }) {
                            Text(stringResource(R.string.tv_action_back))
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
    progress: ContinueWatchingItem?,
    serverOrigin: String,
    requestHeaders: Map<String, String>,
    focusColor: Color,
    accent: Color,
    focusRequester: FocusRequester,
    onFocused: () -> Unit,
    onClick: () -> Unit,
) {
    var focused by remember(entry.id) { mutableStateOf(false) }
    val shape = RoundedCornerShape(15.dp)
    val status = watchlistStatusLabel(entry.status)

    Column(
        modifier = Modifier
            .fillMaxWidth()
            .height(310.dp)
            .clip(shape)
            .background(Color(0xFF101522))
            .tvFocusIndication(focused, focusColor, shape)
            .focusRequester(focusRequester)
            .clickable(onClick = onClick)
            .reportFocus {
                focused = it
                if (it) onFocused()
            },
    ) {
        Box(
            modifier = Modifier.fillMaxWidth().aspectRatio(16f / 9f),
        ) {
            TvArtwork(
                url = entry.artworkUrl,
                serverOrigin = serverOrigin,
                requestHeaders = requestHeaders,
                contentDescription = entry.title,
                modifier = Modifier.fillMaxSize(),
            )
            if (status != null) {
                Text(
                    text = stringResource(status),
                    modifier = Modifier.align(Alignment.TopStart)
                        .padding(9.dp)
                        .clip(RoundedCornerShape(8.dp))
                        .background(accent.copy(alpha = 0.93f))
                        .padding(horizontal = 10.dp, vertical = 5.dp),
                    color = Color.White,
                    style = MaterialTheme.typography.labelMedium,
                )
            }
            if (entry.availability == "in_library") {
                Icon(
                    Icons.Filled.CheckCircle,
                    contentDescription = stringResource(R.string.tv_watchlist_in_library),
                    modifier = Modifier.align(Alignment.TopEnd).padding(9.dp).size(26.dp),
                    tint = Color(0xFF1CCA8A),
                )
            }
        }

        Column(
            modifier = Modifier.fillMaxWidth().weight(1f).padding(12.dp),
            verticalArrangement = Arrangement.spacedBy(7.dp),
        ) {
            Text(
                entry.title,
                style = MaterialTheme.typography.titleMedium,
                color = Color.White,
                fontWeight = FontWeight.SemiBold,
                maxLines = 2,
                overflow = TextOverflow.Ellipsis,
            )
            Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                Text(
                    mediaTypeLabel(entry.mediaType),
                    modifier = Modifier
                        .clip(CircleShape)
                        .background(Color(0xFF252B40))
                        .padding(horizontal = 10.dp, vertical = 5.dp),
                    style = MaterialTheme.typography.labelMedium,
                    color = Color.White,
                )
                entry.format?.takeIf { it.isNotBlank() }?.let { format ->
                    Text(
                        format,
                        modifier = Modifier
                            .clip(CircleShape)
                            .background(Color(0xFF252B40))
                            .padding(horizontal = 10.dp, vertical = 5.dp),
                        style = MaterialTheme.typography.labelMedium,
                        color = Color.White,
                    )
                }
            }
            Spacer(Modifier.weight(1f))
            if (progress != null) {
                Row(
                    modifier = Modifier.fillMaxWidth(),
                    horizontalArrangement = Arrangement.SpaceBetween,
                ) {
                    Text(
                        stringResource(
                            R.string.tv_watchlist_episode_number,
                            progress.seasonNumber,
                            progress.episodeNumber,
                        ),
                        color = Color.White.copy(alpha = 0.8f),
                        style = MaterialTheme.typography.labelMedium,
                    )
                    Text(
                        "${progress.percent.coerceIn(0, 100)}%",
                        color = Color.White.copy(alpha = 0.8f),
                        style = MaterialTheme.typography.labelMedium,
                    )
                }
                Box(
                    modifier = Modifier.fillMaxWidth().height(5.dp)
                        .clip(CircleShape)
                        .background(Color(0xFF2D334C)),
                ) {
                    Box(
                        Modifier.fillMaxWidth((progress.percent / 100f).coerceIn(0f, 1f))
                            .height(5.dp).background(accent),
                    )
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
