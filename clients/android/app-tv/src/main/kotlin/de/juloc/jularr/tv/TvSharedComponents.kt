package de.juloc.jularr.tv

import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyRow
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.selection.selectable
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.BasicTextField
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.Sort
import androidx.compose.material.icons.filled.AccountCircle
import androidx.compose.material.icons.filled.FilterList
import androidx.compose.material.icons.filled.Headphones
import androidx.compose.material.icons.filled.MoreVert
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
import androidx.compose.ui.focus.onFocusChanged
import androidx.compose.ui.focus.FocusRequester
import androidx.compose.ui.focus.focusRequester
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.SolidColor
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.PasswordVisualTransformation
import androidx.compose.ui.text.input.VisualTransformation
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.tv.material3.Button
import androidx.tv.material3.Icon
import androidx.tv.material3.MaterialTheme
import androidx.tv.material3.Surface
import androidx.tv.material3.Text
import coil3.compose.AsyncImage
import coil3.network.NetworkHeaders
import coil3.network.httpHeaders
import coil3.request.ImageRequest
import de.juloc.jularr.core.model.AnimeSummary
import de.juloc.jularr.core.model.EpisodeSummary
import java.net.URI

@Composable
internal fun TvTopBar(
    selectedTab: String = "Library",
    currentTime: String = "19:41",
    onSelectTab: (String) -> Unit = {},
) {
    val focusColor = rememberTvFocusColor()
    val pillShape = RoundedCornerShape(20.dp)

    Row(
        modifier = Modifier
            .fillMaxWidth()
            .height(56.dp)
            .padding(horizontal = 4.dp),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.SpaceBetween,
    ) {
        // Left: Logo & Brand Name
        Row(
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.spacedBy(10.dp),
        ) {
            Box(
                modifier = Modifier
                    .size(34.dp)
                    .clip(RoundedCornerShape(10.dp))
                    .background(Color(0xFF5B46F6)),
                contentAlignment = Alignment.Center,
            ) {
                Icon(
                    imageVector = Icons.Filled.PlayArrow,
                    contentDescription = null,
                    tint = Color.White,
                    modifier = Modifier.size(20.dp),
                )
            }
            Text(
                text = "Jularr",
                style = TextStyle(
                    color = Color.White,
                    fontSize = 22.sp,
                    fontWeight = FontWeight.Bold,
                ),
            )
        }

        // Center: Library / Collections Pills with Focus Indicator
        Row(
            horizontalArrangement = Arrangement.spacedBy(10.dp),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            val tabs = listOf("Library", "Collections")
            for (tab in tabs) {
                val isSelected = tab == selectedTab
                var focused by remember(tab) { mutableStateOf(false) }

                Box(
                    modifier = Modifier
                        .clip(pillShape)
                        .background(if (isSelected) Color(0xFF5B46F6) else Color(0xFF1E2230))
                        .tvFocusIndication(focused, focusColor, pillShape)
                        .clickable { onSelectTab(tab) }
                        .reportFocus { focused = it }
                        .padding(horizontal = 22.dp, vertical = 8.dp),
                    contentAlignment = Alignment.Center,
                ) {
                    Text(
                        text = tab,
                        style = TextStyle(
                            color = if (isSelected) Color.White else Color.White.copy(alpha = 0.6f),
                            fontSize = 15.sp,
                            fontWeight = FontWeight.Medium,
                        ),
                    )
                }
            }
        }

        // Right: Time & Avatar
        Row(
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.spacedBy(14.dp),
        ) {
            Text(
                text = currentTime,
                style = TextStyle(
                    color = Color.White.copy(alpha = 0.85f),
                    fontSize = 16.sp,
                    fontWeight = FontWeight.Medium,
                ),
            )
            Box(
                modifier = Modifier
                    .size(36.dp)
                    .clip(CircleShape)
                    .background(Color(0xFF33384B)),
                contentAlignment = Alignment.Center,
            ) {
                Icon(
                    imageVector = Icons.Filled.AccountCircle,
                    contentDescription = "Profile",
                    tint = Color.White.copy(alpha = 0.9f),
                    modifier = Modifier.size(36.dp),
                )
            }
        }
    }
}

@Composable
internal fun TvSubFilterBar(
    onOpenFilter: () -> Unit = {},
    onOpenSort: () -> Unit = {},
) {
    val focusColor = rememberTvFocusColor()
    val buttonShape = RoundedCornerShape(12.dp)
    var filterFocused by remember { mutableStateOf(false) }
    var sortFocused by remember { mutableStateOf(false) }

    Row(
        horizontalArrangement = Arrangement.spacedBy(12.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Box(
            modifier = Modifier
                .clip(buttonShape)
                .background(if (filterFocused) Color(0xFF2A1F60) else Color(0xFF1E2230))
                .tvFocusIndication(filterFocused, focusColor, buttonShape)
                .clickable(onClick = onOpenFilter)
                .reportFocus { filterFocused = it }
                .padding(horizontal = 16.dp, vertical = 8.dp),
        ) {
            Row(
                verticalAlignment = Alignment.CenterVertically,
                horizontalArrangement = Arrangement.spacedBy(8.dp),
            ) {
                Icon(
                    imageVector = Icons.Filled.FilterList,
                    contentDescription = null,
                    tint = Color.White.copy(alpha = 0.85f),
                    modifier = Modifier.size(18.dp),
                )
                Text(
                    text = stringResource(R.string.tv_home_button_filter),
                    style = TextStyle(
                        color = Color.White.copy(alpha = 0.85f),
                        fontSize = 14.sp,
                        fontWeight = FontWeight.Medium,
                    ),
                )
            }
        }

        Box(
            modifier = Modifier
                .clip(buttonShape)
                .background(if (sortFocused) Color(0xFF2A1F60) else Color(0xFF1E2230))
                .tvFocusIndication(sortFocused, focusColor, buttonShape)
                .clickable(onClick = onOpenSort)
                .reportFocus { sortFocused = it }
                .padding(horizontal = 16.dp, vertical = 8.dp),
        ) {
            Row(
                verticalAlignment = Alignment.CenterVertically,
                horizontalArrangement = Arrangement.spacedBy(8.dp),
            ) {
                Icon(
                    imageVector = Icons.AutoMirrored.Filled.Sort,
                    contentDescription = null,
                    tint = Color.White.copy(alpha = 0.85f),
                    modifier = Modifier.size(18.dp),
                )
                Text(
                    text = stringResource(R.string.tv_home_button_sort),
                    style = TextStyle(
                        color = Color.White.copy(alpha = 0.85f),
                        fontSize = 14.sp,
                        fontWeight = FontWeight.Medium,
                    ),
                )
            }
        }
    }
}

@Composable
internal fun TvCenteredPanel(
    title: String,
    description: String,
    content: @Composable () -> Unit,
) {
    Surface(modifier = Modifier.fillMaxSize()) {
        Box(
            modifier = Modifier
                .fillMaxSize()
                .padding(48.dp),
            contentAlignment = Alignment.Center,
        ) {
            Column(
                modifier = Modifier.width(620.dp),
                verticalArrangement = Arrangement.spacedBy(18.dp),
            ) {
                Text(title, style = MaterialTheme.typography.headlineLarge)
                Text(description, style = MaterialTheme.typography.bodyLarge)
                content()
            }
        }
    }
}

@Composable
internal fun TvInput(
    value: String,
    onValueChange: (String) -> Unit,
    label: String,
    placeholder: String,
    password: Boolean = false,
    onFocusChanged: ((Boolean) -> Unit)? = null,
    focusRequester: FocusRequester? = null,
) {
    var focused by remember { mutableStateOf(false) }

    Column(verticalArrangement = Arrangement.spacedBy(7.dp)) {
        Text(label, style = MaterialTheme.typography.labelLarge)
        BasicTextField(
            value = value,
            onValueChange = onValueChange,
            singleLine = true,
            visualTransformation = if (password) {
                PasswordVisualTransformation()
            } else {
                VisualTransformation.None
            },
            textStyle = TextStyle(
                color = MaterialTheme.colorScheme.onSurface,
                fontSize = 20.sp,
            ),
            cursorBrush = SolidColor(MaterialTheme.colorScheme.primary),
            modifier = Modifier
                .fillMaxWidth()
                .then(if (focusRequester != null) Modifier.focusRequester(focusRequester) else Modifier)
                .onFocusChanged {
                    focused = it.isFocused
                    onFocusChanged?.invoke(it.isFocused)
                }
                .border(
                    width = if (focused) 3.dp else 1.dp,
                    color = if (focused) {
                        MaterialTheme.colorScheme.primary
                    } else {
                        MaterialTheme.colorScheme.onSurface.copy(alpha = 0.35f)
                    },
                    shape = MaterialTheme.shapes.small,
                )
                .background(
                    color = MaterialTheme.colorScheme.surfaceVariant,
                    shape = MaterialTheme.shapes.small,
                )
                .padding(horizontal = 16.dp, vertical = 14.dp),
            decorationBox = { inner ->
                if (value.isEmpty()) {
                    Text(
                        placeholder,
                        color = MaterialTheme.colorScheme.onSurface.copy(alpha = 0.5f),
                    )
                }
                inner()
            },
        )
    }
}

@Composable
internal fun TvFilterChipRow(
    selected: TvContentFilter,
    focusMemory: TvFocusMemory,
    screenKey: String,
    onSelect: (TvContentFilter) -> Unit,
) {
    val focusColor = rememberTvFocusColor()
    val context = LocalContext.current
    val accent = remember(context) { TvPlayerDesignLoader.load(context).accent }
    val chipShape = RoundedCornerShape(20.dp)

    LazyRow(
        horizontalArrangement = Arrangement.spacedBy(10.dp),
        modifier = Modifier.fillMaxWidth(),
    ) {
        items(TvContentFilter.visible) { filter ->
            val focusRequester = remember(filter) { FocusRequester() }
            LaunchedEffect(filter) {
                if (focusMemory.recall(screenKey) == "filter:${filter.name}") {
                    runCatching { focusRequester.requestFocus() }
                }
            }
            var focused by remember(filter) { mutableStateOf(false) }
            val isSelected = filter == selected

            Box(
                modifier = Modifier
                    .clip(chipShape)
                    .background(
                        color = if (focused) accent.copy(alpha = 0.35f) else if (isSelected) accent else Color(0xFF1E2230),
                    )
                    .tvFocusIndication(focused, focusColor, chipShape)
                    .focusRequester(focusRequester)
                    .selectable(
                        selected = isSelected,
                        onClick = { onSelect(filter) },
                    )
                    .reportFocus { hasFocus ->
                        focused = hasFocus
                        if (hasFocus) {
                            focusMemory.remember(screenKey, "filter:${filter.name}")
                        }
                    }
                    .padding(horizontal = 20.dp, vertical = 10.dp),
            ) {
                Text(
                    text = stringResource(filter.labelRes),
                    style = TextStyle(
                        color = if (isSelected || focused) Color.White else Color.White.copy(alpha = 0.65f),
                        fontSize = 14.sp,
                        fontWeight = FontWeight.Medium,
                    ),
                )
            }
        }
    }
}

@Composable
internal fun TvInfoPill(text: String) {
    Box(
        modifier = Modifier
            .background(
                color = MaterialTheme.colorScheme.surface,
                shape = MaterialTheme.shapes.small,
            )
            .padding(horizontal = 12.dp, vertical = 8.dp),
    ) {
        Text(
            text = text,
            style = MaterialTheme.typography.labelLarge,
        )
    }
}

internal fun formatEpisodePosition(valueMs: Long): String {
    val totalSeconds = valueMs.coerceAtLeast(0) / 1000
    val hours = totalSeconds / 3600
    val minutes = (totalSeconds % 3600) / 60
    val seconds = totalSeconds % 60
    return if (hours > 0) {
        "%d:%02d:%02d".format(hours, minutes, seconds)
    } else {
        "%d:%02d".format(minutes, seconds)
    }
}

@Composable
internal fun AnimeButton(
    anime: AnimeSummary,
    serverOrigin: String,
    requestHeaders: Map<String, String>,
    focused: Boolean,
    focusColor: Color,
    onFocusChanged: (Boolean) -> Unit,
    onClick: () -> Unit,
    focusRequester: FocusRequester? = null,
) {
    val cardShape = RoundedCornerShape(14.dp)
    val cardBackground = if (focused) focusColor.copy(alpha = 0.28f) else Color(0xFF121520)

    Box(
        modifier = Modifier
            .width(210.dp)
            .clip(cardShape)
            .background(cardBackground)
            .tvFocusIndication(focused, focusColor, cardShape)
            .then(if (focusRequester != null) Modifier.focusRequester(focusRequester) else Modifier)
            .clickable(onClick = onClick)
            .reportFocus(onFocusChanged),
    ) {
        Column(modifier = Modifier.fillMaxWidth()) {
            TvArtwork(
                url = anime.coverImageUrl,
                serverOrigin = serverOrigin,
                requestHeaders = requestHeaders,
                contentDescription = anime.title,
                modifier = Modifier
                    .fillMaxWidth()
                    .height(280.dp)
                    .clip(RoundedCornerShape(topStart = 14.dp, topEnd = 14.dp)),
            )

            Column(
                modifier = Modifier
                    .fillMaxWidth()
                    .padding(12.dp),
                verticalArrangement = Arrangement.spacedBy(6.dp),
            ) {
                Row(
                    modifier = Modifier.fillMaxWidth(),
                    horizontalArrangement = Arrangement.SpaceBetween,
                    verticalAlignment = Alignment.CenterVertically,
                ) {
                    Text(
                        text = anime.title,
                        style = TextStyle(
                            color = Color.White,
                            fontSize = 15.sp,
                            fontWeight = FontWeight.Bold,
                        ),
                        maxLines = 1,
                        overflow = TextOverflow.Ellipsis,
                        modifier = Modifier.weight(1f),
                    )
                    Icon(
                        imageVector = Icons.Filled.MoreVert,
                        contentDescription = "More",
                        tint = Color.White.copy(alpha = 0.5f),
                        modifier = Modifier.size(18.dp),
                    )
                }

                val subtitleText = buildString {
                    anime.seasonYear?.let { append(it).append(" · ") }
                    append("${anime.episodeCount} ep.")
                }
                Text(
                    text = subtitleText,
                    style = TextStyle(
                        color = Color.White.copy(alpha = 0.6f),
                        fontSize = 12.sp,
                    ),
                    maxLines = 1,
                )

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
                                .fillMaxWidth(0.72f)
                                .background(Color(0xFF7B61FF)),
                        )
                    }
                    Text(
                        text = "72%",
                        style = TextStyle(
                            color = Color.White.copy(alpha = 0.6f),
                            fontSize = 11.sp,
                        ),
                    )
                }

                Row(
                    verticalAlignment = Alignment.CenterVertically,
                    horizontalArrangement = Arrangement.spacedBy(6.dp),
                    modifier = Modifier.padding(top = 2.dp),
                ) {
                    Icon(
                        imageVector = Icons.Filled.Headphones,
                        contentDescription = null,
                        tint = Color.White.copy(alpha = 0.6f),
                        modifier = Modifier.size(14.dp),
                    )
                    Text(
                        text = "JP · CC EN +1",
                        style = TextStyle(
                            color = Color.White.copy(alpha = 0.6f),
                            fontSize = 11.sp,
                            fontWeight = FontWeight.Medium,
                        ),
                    )
                }
            }
        }
    }
}

@Composable
internal fun EpisodeButton(
    episode: EpisodeSummary,
    onClick: () -> Unit,
) {
    Button(
        enabled = episode.hasMedia,
        onClick = onClick,
        modifier = Modifier
            .width(240.dp)
            .height(112.dp),
    ) {
        Column(verticalArrangement = Arrangement.spacedBy(5.dp)) {
            Row(
                modifier = Modifier.fillMaxWidth(),
                horizontalArrangement = Arrangement.SpaceBetween,
            ) {
                Text(
                    stringResource(R.string.tv_episode_number, episode.number),
                    style = MaterialTheme.typography.titleMedium,
                )
                if (episode.hasJapaneseLearningSubtitle) {
                    Text(
                        stringResource(R.string.tv_episode_japanese_label),
                        style = MaterialTheme.typography.labelMedium,
                    )
                }
            }
            Text(
                episode.title,
                maxLines = 2,
                style = MaterialTheme.typography.bodyMedium,
            )
            if (!episode.hasMedia) {
                Text(
                    stringResource(R.string.tv_episode_status_media_unavailable),
                    style = MaterialTheme.typography.bodySmall,
                )
            }
        }
    }
}

internal data class TvArtworkSource(
    val url: String,
    val authenticated: Boolean,
)

@Composable
internal fun TvArtwork(
    url: String?,
    serverOrigin: String,
    requestHeaders: Map<String, String>,
    contentDescription: String?,
    modifier: Modifier = Modifier,
    contentScale: ContentScale = ContentScale.Crop,
) {
    val context = LocalContext.current
    val source = remember(url, serverOrigin) {
        resolveArtworkSource(serverOrigin, url)
    }

    Box(
        modifier = modifier.background(MaterialTheme.colorScheme.surfaceVariant),
        contentAlignment = Alignment.Center,
    ) {
        if (source == null) {
            Text(
                text = contentDescription
                    ?.trim()
                    ?.take(1)
                    ?.uppercase()
                    .orEmpty(),
                style = MaterialTheme.typography.headlineLarge,
                color = MaterialTheme.colorScheme.onSurface.copy(alpha = 0.45f),
            )
            return@Box
        }

        val request = remember(source, requestHeaders) {
            val builder = ImageRequest.Builder(context)
                .data(source.url)

            if (source.authenticated && requestHeaders.isNotEmpty()) {
                val headers = NetworkHeaders.Builder().also { network ->
                    requestHeaders.forEach { (name, value) ->
                        network.set(name, value)
                    }
                }.build()
                builder.httpHeaders(headers)
            }

            builder.build()
        }

        AsyncImage(
            model = request,
            contentDescription = contentDescription,
            modifier = Modifier.fillMaxSize(),
            contentScale = contentScale,
        )
    }
}

internal fun resolveArtworkSource(
    serverOrigin: String,
    rawUrl: String?,
): TvArtworkSource? {
    val value = rawUrl?.trim()?.takeIf { it.isNotEmpty() } ?: return null
    return runCatching {
        val candidate = URI(value)
        val base = serverOrigin
            .trim()
            .takeIf { it.isNotEmpty() }
            ?.let { URI(it.trimEnd('/') + "/") }

        val resolved = when {
            candidate.isAbsolute -> candidate
            base != null -> base.resolve(candidate)
            else -> return null
        }

        TvArtworkSource(
            url = resolved.toString(),
            authenticated = base != null && sameOrigin(base, resolved),
        )
    }.getOrNull()
}

private fun sameOrigin(
    left: URI,
    right: URI,
): Boolean =
    left.scheme.equals(right.scheme, ignoreCase = true) &&
        left.host.equals(right.host, ignoreCase = true) &&
        effectivePort(left) == effectivePort(right)

private fun effectivePort(uri: URI): Int =
    when {
        uri.port >= 0 -> uri.port
        uri.scheme.equals("https", ignoreCase = true) -> 443
        uri.scheme.equals("http", ignoreCase = true) -> 80
        else -> -1
    }
