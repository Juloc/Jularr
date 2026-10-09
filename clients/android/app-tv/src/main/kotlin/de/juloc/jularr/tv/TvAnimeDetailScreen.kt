package de.juloc.jularr.tv

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
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyRow
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.lazy.grid.GridCells
import androidx.compose.foundation.lazy.grid.GridItemSpan
import androidx.compose.foundation.lazy.grid.LazyVerticalGrid
import androidx.compose.foundation.lazy.grid.items as gridItems
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.PlayArrow
import androidx.compose.runtime.Composable
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
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.tv.material3.Button
import androidx.tv.material3.Icon
import androidx.tv.material3.MaterialTheme
import androidx.tv.material3.Text
import de.juloc.jularr.core.model.AnimeDetail
import de.juloc.jularr.core.model.ContinueWatchingItem
import de.juloc.jularr.core.model.EpisodeSummary

internal fun currentAnimeResume(
    anime: AnimeDetail,
    items: List<ContinueWatchingItem>,
): ContinueWatchingItem? =
    items.firstOrNull { entry ->
        entry.animeId == anime.id && entry.percent in 1..99 &&
            anime.seasons.any { season ->
                season.episodes.any { it.id == entry.episodeId && it.hasMedia }
            }
    }

@Composable
fun TvAnimeScreen(
    anime: AnimeDetail,
    continueWatching: List<ContinueWatchingItem>,
    focusMemory: TvFocusMemory,
    serverOrigin: String,
    requestHeaders: Map<String, String>,
    onEpisode: (EpisodeSummary) -> Unit,
    onPlayEpisode: (EpisodeSummary) -> Unit,
    onBack: () -> Unit,
) {
    val accent = remember(LocalContext.current) {
        TvPlayerDesignLoader.load(LocalContext.current).accent
    }
    val focusColor = rememberTvFocusColor()
    val chapters = anime.seasons.sortedBy { it.number }
    var selectedSeason by remember(anime.id) {
        mutableStateOf(
            focusMemory.recall("anime-season:${anime.id}")?.toIntOrNull()
                ?.takeIf { value -> chapters.any { it.number == value } }
                ?: chapters.firstOrNull()?.number,
        )
    }
    val episodes = chapters.firstOrNull { it.number == selectedSeason }?.episodes
        ?.sortedBy { it.number }.orEmpty()
    val firstPlayable = chapters.asSequence()
        .flatMap { it.episodes.asSequence() }
        .firstOrNull { it.hasMedia }
    val resume = remember(anime, continueWatching) { currentAnimeResume(anime, continueWatching) }
    val resumeEpisode = chapters.asSequence()
        .flatMap { it.episodes.asSequence() }
        .firstOrNull { it.id == resume?.episodeId }
    val activeEpisode = resumeEpisode ?: firstPlayable
    val restoredEpisode = remember(anime.id) { focusMemory.recall("anime:${anime.id}") }

    LazyVerticalGrid(
        modifier = Modifier.fillMaxSize().background(MaterialTheme.colorScheme.background),
        columns = GridCells.Adaptive(minSize = 290.dp),
        verticalArrangement = Arrangement.spacedBy(16.dp),
        horizontalArrangement = Arrangement.spacedBy(16.dp),
        contentPadding = androidx.compose.foundation.layout.PaddingValues(
            start = 26.dp, end = 26.dp, top = 20.dp, bottom = 28.dp,
        ),
    ) {
        item(span = { GridItemSpan(maxLineSpan) }, key = "hero") {
            Box(
                modifier = Modifier.fillMaxWidth().height(375.dp)
                    .clip(RoundedCornerShape(18.dp)),
            ) {
                TvArtwork(
                    url = anime.bannerImageUrl ?: anime.coverImageUrl,
                    serverOrigin = serverOrigin,
                    requestHeaders = requestHeaders,
                    contentDescription = anime.title,
                    modifier = Modifier.fillMaxSize(),
                )
                Box(
                    Modifier.fillMaxSize().background(
                        Brush.horizontalGradient(
                            0.0f to Color.Black.copy(alpha = 0.89f),
                            0.55f to Color.Black.copy(alpha = 0.65f),
                            1f to Color.Transparent,
                        ),
                    ),
                )
                Column(
                    modifier = Modifier.align(Alignment.CenterStart)
                        .fillMaxWidth(0.68f).padding(25.dp),
                    verticalArrangement = Arrangement.spacedBy(10.dp),
                ) {
                    Text(
                        anime.title,
                        style = MaterialTheme.typography.displayMedium,
                        color = Color.White,
                        maxLines = 2,
                        overflow = TextOverflow.Ellipsis,
                    )
                    anime.nativeTitle?.takeIf { it.isNotBlank() && it != anime.title }?.let {
                        Text(
                            it,
                            style = MaterialTheme.typography.titleMedium,
                            color = Color.White.copy(alpha = 0.83f),
                            maxLines = 1,
                        )
                    }
                    Text(
                        listOfNotNull(
                            anime.seasonYear?.toString(),
                            anime.format,
                            chapters.size.takeIf { it > 0 }?.let {
                                stringResource(R.string.tv_anime_seasons_count, it)
                            },
                        ).joinToString(" · "),
                        color = Color.White.copy(alpha = 0.85f),
                        style = MaterialTheme.typography.bodyLarge,
                    )
                    anime.description?.takeIf { it.isNotBlank() }?.let {
                        Text(
                            it,
                            color = Color.White.copy(alpha = 0.90f),
                            style = MaterialTheme.typography.bodyMedium,
                            maxLines = 4,
                            overflow = TextOverflow.Ellipsis,
                        )
                    }
                    Row(
                        horizontalArrangement = Arrangement.spacedBy(12.dp),
                        verticalAlignment = Alignment.CenterVertically,
                    ) {
                        if (activeEpisode != null) {
                            Button(onClick = { onPlayEpisode(activeEpisode) }) {
                                Icon(Icons.Filled.PlayArrow, contentDescription = null)
                                Text(
                                    " " + stringResource(
                                        if (resume != null) R.string.tv_anime_continue
                                        else R.string.tv_anime_play,
                                    ),
                                )
                            }
                            if (resume != null) {
                                Text(
                                    stringResource(
                                        R.string.tv_anime_resume_info,
                                        resume.seasonNumber,
                                        resume.episodeNumber,
                                        resume.percent,
                                    ),
                                    color = Color.White,
                                    style = MaterialTheme.typography.labelMedium,
                                )
                            }
                        }
                        Button(onClick = onBack) {
                            Text(stringResource(R.string.tv_action_back))
                        }
                    }
                }
            }
        }

        if (chapters.isNotEmpty()) {
            item(span = { GridItemSpan(maxLineSpan) }, key = "seasons") {
                LazyRow(horizontalArrangement = Arrangement.spacedBy(12.dp)) {
                    items(chapters, key = { it.number }) { season ->
                        val selected = season.number == selectedSeason
                        val shape = RoundedCornerShape(15.dp)
                        var focused by remember(season.number) { mutableStateOf(false) }
                        Box(
                            modifier = Modifier
                                .clip(shape)
                                .background(
                                    if (selected) accent.copy(alpha = 0.62f)
                                    else MaterialTheme.colorScheme.surfaceVariant,
                                )
                                .border(
                                    if (focused) 2.dp else 1.dp,
                                    if (focused) focusColor
                                    else MaterialTheme.colorScheme.onSurface.copy(alpha = 0.16f),
                                    shape,
                                )
                                .clickable {
                                    selectedSeason = season.number
                                    focusMemory.remember("anime-season:${anime.id}", season.number.toString())
                                }
                                .reportFocus { focused = it }
                                .padding(horizontal = 20.dp, vertical = 13.dp),
                        ) {
                            Text(
                                if (season.number == 0) stringResource(R.string.tv_anime_specials)
                                else stringResource(R.string.tv_anime_season_header, season.number),
                                style = MaterialTheme.typography.titleMedium,
                            )
                        }
                    }
                }
            }
            if (episodes.isEmpty()) {
                item(span = { GridItemSpan(maxLineSpan) }) {
                    Text(stringResource(R.string.tv_anime_no_episodes))
                }
            }
            gridItems(episodes, key = { it.id }) { episode ->
                val progress = continueWatching.firstOrNull {
                    it.episodeId == episode.id && it.percent in 1..99
                }
                val focusRequester = remember(episode.id) { FocusRequester() }
                var focused by remember(episode.id) { mutableStateOf(false) }
                val shape = RoundedCornerShape(14.dp)
                Column(
                    modifier = Modifier
                        .fillMaxWidth()
                        .clip(shape)
                        .background(MaterialTheme.colorScheme.surface)
                        .border(
                            if (focused) 2.dp else 1.dp,
                            if (focused) focusColor
                            else MaterialTheme.colorScheme.onSurface.copy(alpha = 0.11f),
                            shape,
                        )
                        .focusRequester(focusRequester)
                        .clickable { onEpisode(episode) }
                        .reportFocus {
                            focused = it
                            if (it) focusMemory.remember("anime:${anime.id}", "episode:${episode.id}")
                        },
                ) {
                    Box(
                        Modifier.fillMaxWidth().aspectRatio(16f / 8f)
                            .background(
                                Brush.linearGradient(
                                    listOf(
                                        MaterialTheme.colorScheme.surfaceVariant,
                                        accent.copy(alpha = 0.18f),
                                    ),
                                ),
                            ),
                        contentAlignment = Alignment.Center,
                    ) {
                        Text(
                            stringResource(
                                R.string.tv_episode_season_and_number,
                                episode.seasonNumber,
                                episode.number,
                            ),
                            style = MaterialTheme.typography.headlineMedium,
                            color = MaterialTheme.colorScheme.onSurface,
                        )
                    }
                    Column(
                        modifier = Modifier.fillMaxWidth().padding(12.dp),
                        verticalArrangement = Arrangement.spacedBy(7.dp),
                    ) {
                        Text(
                            episode.title,
                            style = MaterialTheme.typography.titleMedium,
                            maxLines = 2,
                            overflow = TextOverflow.Ellipsis,
                        )
                        Text(
                            stringResource(
                                if (episode.hasMedia) R.string.tv_anime_media_indexed
                                else R.string.tv_episode_status_media_unavailable,
                            ),
                            style = MaterialTheme.typography.bodySmall,
                            color = MaterialTheme.colorScheme.onSurface.copy(alpha = 0.72f),
                        )
                        if (progress != null) {
                            Row(
                                modifier = Modifier.fillMaxWidth(),
                                horizontalArrangement = Arrangement.SpaceBetween,
                            ) {
                                Text(
                                    stringResource(R.string.tv_anime_progress),
                                    style = MaterialTheme.typography.labelSmall,
                                )
                                Text(
                                    "${progress.percent}%",
                                    style = MaterialTheme.typography.labelSmall,
                                )
                            }
                            Box(
                                Modifier.fillMaxWidth().height(5.dp)
                                    .clip(RoundedCornerShape(3.dp))
                                    .background(MaterialTheme.colorScheme.surfaceVariant),
                            ) {
                                Box(
                                    Modifier.fillMaxWidth(progress.percent / 100f)
                                        .height(5.dp).background(accent),
                                )
                            }
                        }
                    }
                }
                androidx.compose.runtime.LaunchedEffect(restoredEpisode, episode.id) {
                    if (restoredEpisode == "episode:${episode.id}") {
                        runCatching { focusRequester.requestFocus() }
                    }
                }
            }
        }
    }
}
