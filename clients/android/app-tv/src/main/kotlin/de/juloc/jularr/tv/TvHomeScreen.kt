package de.juloc.jularr.tv

import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.interaction.MutableInteractionSource
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.BoxScope
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.LazyRow
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Info
import androidx.compose.material.icons.filled.PlayArrow
import androidx.compose.material3.Icon
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.focus.FocusRequester
import androidx.compose.ui.focus.focusRequester
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import coil3.compose.AsyncImage

/**
 * The home page of the television: the type capsule of the desktop and the shelves of the
 * profile in its order.
 */
@Composable
fun TvHomeScreen(controller: TvController) {
    val colors = LocalTvColors.current
    val type = LocalTvType.current
    LazyColumn(
        modifier = Modifier.fillMaxSize(),
        verticalArrangement = Arrangement.spacedBy(TvLayout.Gap),
    ) {
        // The row of media types, as on the desktop: every area and the areas of the instance.
        item {
            LazyRow(horizontalArrangement = Arrangement.spacedBy(TvLayout.Gap)) {
                item {
                    TvAreaCapsule(
                        controller = controller,
                        title = controller.texts["discover.all"],
                        chosen = controller.selectedArea == null && controller.selectedScope == null,
                        icon = null,
                    ) {
                        controller.selectScope(null)
                        controller.selectArea(null)
                    }
                }
                items(TvArea.entries.toList()) { area ->
                    TvAreaCapsule(
                        controller = controller,
                        title = controller.texts["discover.area." + area.key],
                        chosen = controller.selectedArea == area,
                        icon = area,
                    ) {
                        controller.selectScope(null)
                        controller.selectArea(area)
                    }
                }
            }
        }

        // The hero of the desktop stands above the shelves.
        val hero = controller.hero()
        if (hero != null) {
            item {
                TvHero(controller, hero)
            }
        }

        // The bar of the lists, the number of the titles and the order, as on the desktop.
        item {
            TvFilterBar(controller)
        }

        if (controller.shelves.isEmpty() && controller.recommendations.isEmpty()) {            item {
                Text(
                    text = controller.texts["tv.empty.home"],
                    fontSize = type.body,
                    color = colors.muted,
                )
            }
        } else {
            items(controller.shelves, key = { it.key }) { shelf ->
                TvShelfRow(controller, shelf)
            }
            // The recommendations of the instance, as on the desktop.
            items(controller.recommendations, key = { it.key }) { shelf ->
                TvShelfRow(controller, shelf)
            }
        }
    }
}

/**
 * One capsule of the media areas, as the desktop groups them: the chosen area stands out, the
 * others wait in the row.
 */
@Composable
private fun TvAreaCapsule(
    controller: TvController,
    title: String,
    chosen: Boolean,
    icon: TvArea?,
    onChoose: () -> Unit,
) {
    val colors = LocalTvColors.current
    val type = LocalTvType.current
    var focused by remember { mutableStateOf(false) }
    val requester = rememberTvFocusEntry(controller, TvFocusArea.Content, "capsule-$title")
    val shape = RoundedCornerShape(TvLayout.RailItemRadius)

    Row(
        modifier = Modifier
            .height(TvLayout.RailItemHeight)
            .clip(shape)
            .background(if (chosen) colors.accent else colors.surface, shape)
            .focusRequester(requester)
            .reportFocus {
                focused = it
                if (it) {
                    controller.focusMovedTo(TvFocusArea.Content)
                    controller.focusMovedTo(requester)
                } else if (controller.focusedEntry == requester) {
                    controller.focusMovedTo(null)
                }
            }
            .clickable(
                interactionSource = remember { MutableInteractionSource() },
                indication = null,
            ) { onChoose() }
            .then(
                if (focused) {
                    Modifier.tvFocusRing(true, colors.focus, shape)
                } else {
                    Modifier
                },
            )
            .padding(horizontal = 16.dp),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(7.dp),
    ) {
        icon?.let { area ->
            Icon(
                imageVector = area.icon(),
                contentDescription = null,
                tint = if (chosen) colors.accentContrast else colors.muted,
                modifier = Modifier.size(TvLayout.AvailabilitySymbol * 0.55f),
            )
        }
        Text(
            text = title,
            fontSize = type.body,
            fontWeight = if (chosen) FontWeight.Bold else FontWeight.Medium,
            color = when {
                chosen -> colors.accentContrast
                focused -> colors.accentText
                else -> colors.muted
            },
        )
    }
}

/**
 * The bar under the hero: the lists of the instance, the number of the titles and the order.
 */
@Composable
private fun TvFilterBar(controller: TvController) {
    val colors = LocalTvColors.current
    val type = LocalTvType.current
    val titles = controller.shelves.sumOf { it.cards.size } + controller.recommendations.sumOf { it.cards.size }
    Row(
        modifier = Modifier.fillMaxWidth().padding(vertical = 4.dp),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(6.dp),
    ) {
        TvFeedButton(controller, controller.texts["discover.all"], null)
        listOf(1, 2, 3, 4).forEach { feed ->
            TvFeedButton(controller, controller.texts["discover.feed." + feedName(feed)], feed)
        }
        Spacer(Modifier.weight(1f))
        Text(
            text = controller.texts["discover.count"] + " " + titles,
            fontSize = type.cardMeta,
            color = colors.muted,
        )
        // The filter of the discover with the granular types and the language.
        TvFilterButton(controller)
    }
}

/** The button that opens the filter of the discover. */
@Composable
private fun TvFilterButton(controller: TvController) {
    val colors = LocalTvColors.current
    val type = LocalTvType.current
    var focused by remember { mutableStateOf(false) }
    val shape = RoundedCornerShape(999.dp)
    Text(
        text = controller.texts["discover.filter"],
        fontSize = type.cardMeta,
        fontWeight = FontWeight.SemiBold,
        color = if (focused) colors.accentText else colors.muted,
        modifier = Modifier
            .clip(shape)
            .background(colors.surface, shape)
            .reportFocus { focused = it }
            .clickable(
                interactionSource = remember { MutableInteractionSource() },
                indication = null,
            ) { controller.openFilter() }
            .then(
                if (focused) {
                    Modifier.tvFocusRing(true, colors.focus, shape)
                } else {
                    Modifier
                },
            )
            .padding(horizontal = 14.dp, vertical = 8.dp),
    )
}

/** One list of the instance in the bar. */
@Composable
private fun TvFeedButton(controller: TvController, title: String, feedTypeId: Int?) {
    val colors = LocalTvColors.current
    val type = LocalTvType.current
    var focused by remember { mutableStateOf(false) }
    val chosen = controller.selectedFeed == feedTypeId
    val shape = RoundedCornerShape(TvLayout.RailItemRadius)
    Text(
        text = title,
        fontSize = type.cardMeta,
        fontWeight = if (chosen) FontWeight.Bold else FontWeight.Medium,
        color = when {
            chosen -> colors.accentText
            focused -> colors.text
            else -> colors.muted
        },
        modifier = Modifier
            .height(TvLayout.RailItemHeight * 0.8f)
            .clip(shape)
            .background(if (chosen) colors.field else colors.surface, shape)
            .reportFocus { focused = it }
            .clickable(
                interactionSource = remember { MutableInteractionSource() },
                indication = null,
            ) { controller.selectFeed(feedTypeId) }
            .padding(horizontal = 14.dp, vertical = 8.dp),
    )
}

/**
 * The hero of the desktop: the wide picture of the title, its name, its year and its actions.
 */
@Composable
private fun TvHero(controller: TvController, card: TvCard) {
    val colors = LocalTvColors.current
    val type = LocalTvType.current
    val shape = RoundedCornerShape(TvLayout.HeroRadius)
    Box(
        modifier = Modifier
            .fillMaxWidth()
            .height(TvLayout.HeroHeight)
            .clip(shape)
            .background(colors.field),
    ) {
        if (card.backdropUrl != null) {
            AsyncImage(
                model = card.backdropUrl,
                contentDescription = card.title,
                contentScale = ContentScale.Crop,
                modifier = Modifier.fillMaxWidth().height(TvLayout.HeroHeight),
            )
        }
        // The text of the desktop stands on the picture, in the light of the picture.
        Column(
            modifier = Modifier
                .align(Alignment.CenterStart)
                .padding(horizontal = TvLayout.ContentPadding * 2),
            verticalArrangement = Arrangement.spacedBy(6.dp),
        ) {
            Text(
                text = card.title,
                fontSize = type.heroTitle,
                fontWeight = FontWeight.Bold,
                color = Color.White,
                maxLines = 2,
                overflow = TextOverflow.Ellipsis,
            )
            card.year?.let { year ->
                Text(text = year.toString(), fontSize = type.body, color = Color.White.copy(alpha = 0.8f))
            }
            card.overview?.let { overview ->
                Text(
                    text = overview,
                    fontSize = type.body,
                    color = Color.White.copy(alpha = 0.85f),
                    maxLines = 2,
                    overflow = TextOverflow.Ellipsis,
                    modifier = Modifier.width(TvLayout.CardWidth * 3),
                )
            }
        }

        // The folgen des Hero, as the desktop shows them.
        if (controller.heroDeck.size > 1) {
            Row(
                modifier = Modifier
                    .align(Alignment.BottomCenter)
                    .padding(bottom = 12.dp),
                horizontalArrangement = Arrangement.spacedBy(5.dp),
            ) {
                controller.heroDeck.indices.forEach { index ->
                    val current = index == controller.heroIndex
                    Box(
                        modifier = Modifier
                            .width(if (current) 34.dp else 6.dp)
                            .height(if (current) 5.dp else 6.dp)
                            .clip(RoundedCornerShape(999.dp))
                            .background(if (current) colors.accent else colors.border),
                    )
                }
            }
        }
    }
}

/** One shelf of the home page: its heading and its cards. */
@Composable
fun TvShelfRow(controller: TvController, shelf: TvShelf) {
    val colors = LocalTvColors.current
    val type = LocalTvType.current
    Column {
        Row(verticalAlignment = Alignment.CenterVertically) {
            Text(
                text = shelf.title,
                fontSize = type.shelfHeading,
                fontWeight = FontWeight.Bold,
                color = colors.text,
            )
            // The desktop names the number of the titles in the shelf.
            Text(
                text = "  ${shelf.cards.size}",
                fontSize = type.cardMeta,
                color = colors.muted,
                modifier = Modifier.padding(bottom = 6.dp),
            )
        }
        LazyRow(horizontalArrangement = Arrangement.spacedBy(TvLayout.Gap)) {
            items(shelf.cards, key = { it.id }) { card ->
                TvCardView(card, controller, "shelf-${shelf.key}")
            }
        }
    }
}

/**
 * One card of the desktop: the poster, the title and the line below it. The focused card
 * shows its ring.
 */
@Composable
fun TvCardView(card: TvCard, controller: TvController, rowKey: String) {
    val colors = LocalTvColors.current
    val type = LocalTvType.current
    var focused by remember { mutableStateOf(false) }
    var actionFocused by remember { mutableStateOf(false) }
    val requester = rememberTvFocusEntry(controller, TvFocusArea.Content, "card-$rowKey-${card.id}")
    val shape = RoundedCornerShape(TvLayout.CardRadius)
    val showActions = focused || actionFocused

    Column(
        modifier = Modifier
            .width(TvLayout.CardWidth)
            .focusRequester(requester)
            .reportFocus {
                focused = it
                if (it) {
                    controller.focusMovedTo(TvFocusArea.Content)
                    controller.focusMovedTo(requester)
                } else {
                    actionFocused = false
                    if (controller.focusedEntry == requester) {
                        controller.focusMovedTo(null)
                    }
                }
            }
            .clickable(
                interactionSource = remember { MutableInteractionSource() },
                indication = null,
            ) { controller.openCard(card) }
            .then(
                if (focused) {
                    Modifier.tvFocusRing(true, colors.focus, shape)
                } else {
                    Modifier
                },
            ),
    ) {
        Box(
            modifier = Modifier
                .fillMaxWidth()
                .height(TvLayout.CardHeight)
                .clip(shape)
                .background(colors.field),
        ) {
            if (card.posterUrl != null) {
                AsyncImage(
                    model = card.posterUrl,
                    contentDescription = card.title,
                    contentScale = ContentScale.Crop,
                    modifier = Modifier.fillMaxWidth().height(TvLayout.CardHeight),
                )
            }
            // The mark of the availability sits in the corner of the cover, as on the desktop.
            availabilityColor(controller, card.availabilityStateId)?.let { mark ->
                Box(
                    modifier = Modifier
                        .align(Alignment.TopStart)
                        .padding(8.dp)
                        .size(TvLayout.AvailabilitySymbol * 0.35f)
                        .clip(RoundedCornerShape(6.dp))
                        .background(mark),
                )
            }
            // The mark of the state of the title sits on the cover, as on the desktop.
            card.stateMark(controller.texts)?.let { mark ->
                Text(
                    text = mark,
                    fontSize = type.cardMeta,
                    color = colors.text,
                    modifier = Modifier
                        .align(Alignment.TopStart)
                        .padding(8.dp)
                        .clip(RoundedCornerShape(999.dp))
                        .background(colors.background.copy(alpha = 0.7f))
                        .padding(horizontal = 8.dp, vertical = 3.dp),
                )
            }
            // The action of the title stands in the middle of the cover, as on the desktop.
            TvCardAction(
                controller = controller,
                card = card,
                rowKey = rowKey,
                colors = colors,
                type = type,
            )
            // The progress of the desktop sits at the lower edge of the cover.
            card.progress?.let { progress ->
                Box(
                    modifier = Modifier
                        .align(Alignment.BottomStart)
                        .fillMaxWidth()
                        .height(4.dp)
                        .background(colors.field),
                ) {
                    Box(
                        modifier = Modifier
                            .fillMaxWidth(progress)
                            .height(4.dp)
                            .background(colors.accent),
                    )
                }
            }
        }
        Spacer(Modifier.height(6.dp))
        Text(
            text = card.title,
            fontSize = type.cardTitle,
            fontWeight = FontWeight.SemiBold,
            color = colors.text,
            maxLines = 2,
            overflow = TextOverflow.Ellipsis,
            lineHeight = type.cardTitle * 1.3f,
        )
        card.meta?.let { meta ->
            Text(
                text = meta,
                fontSize = type.cardMeta,
                color = colors.muted,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
            )
        }
    }
}

/**
 * The action of a title in the middle of its cover: the title continues or plays, and the
 * details open beside it.
 */
@Composable
private fun BoxScope.TvCardAction(
    card: TvCard,
    controller: TvController,
    rowKey: String,
    colors: TvColors,
    type: TvType,
) {
    var focused by remember { mutableStateOf(false) }
    val requester = rememberTvFocusEntry(controller, TvFocusArea.Content, "action-$rowKey-${card.id}")
    val shape = RoundedCornerShape(999.dp)
    val playable = card.mediaAssetId != null
    Box(
        modifier = Modifier
            .align(Alignment.Center)
            .size(TvLayout.AvailabilitySymbol)
            .clip(shape)
            .background(if (focused) colors.accent else colors.background.copy(alpha = 0.72f))
            .border(1.dp, colors.border, shape)
            .focusRequester(requester)
            .reportFocus {
                focused = it
                if (it) {
                    controller.focusMovedTo(TvFocusArea.Content)
                    controller.focusMovedTo(requester)
                } else if (controller.focusedEntry == requester) {
                    controller.focusMovedTo(null)
                }
            }
            .clickable(
                interactionSource = remember { MutableInteractionSource() },
                indication = null,
            ) { controller.openCard(card) },
        contentAlignment = Alignment.Center,
    ) {
        Icon(
            imageVector = if (playable) Icons.Filled.PlayArrow else Icons.Filled.Info,
            contentDescription = controller.texts[if (playable) "media.continue" else "media.details"],
            tint = if (focused) colors.accentContrast else colors.text,
            modifier = Modifier.size(TvLayout.AvailabilitySymbol * 0.6f),
        )
    }
}

/**
 * The actions of the focused card on the cover: the title continues or plays, and the details
 * open beside them.
 */
@Composable
private fun BoxScope.TvCardActions(
    card: TvCard,
    controller: TvController,
    rowKey: String,
    colors: TvColors,
    type: TvType,
) {
    Row(
        modifier = Modifier
            .align(Alignment.BottomStart)
            .fillMaxWidth()
            .background(colors.background.copy(alpha = 0.82f))
            .padding(horizontal = 6.dp, vertical = 6.dp),
        horizontalArrangement = Arrangement.spacedBy(4.dp),
    ) {
        val playable = card.mediaAssetId != null
        TvCardAction(
            controller = controller,
            rowKey = rowKey,
            title = controller.texts[if (playable) "media.continue" else "media.details"],
            colors = colors,
            type = type,
            onClick = { controller.openCard(card) },
        )
    }
}

/** One action of the card. */
@Composable
private fun TvCardAction(
    controller: TvController,
    rowKey: String,
    title: String,
    colors: TvColors,
    type: TvType,
    onClick: () -> Unit,
) {
    var focused by remember { mutableStateOf(false) }
    val requester = rememberTvFocusEntry(controller, TvFocusArea.Content, "label-$rowKey-$title")
    val shape = RoundedCornerShape(TvLayout.RailItemRadius)
    Text(
        text = title,
        fontSize = type.cardMeta,
        fontWeight = FontWeight.SemiBold,
        color = if (focused) colors.accentText else colors.text,
        maxLines = 1,
        modifier = Modifier
            .focusRequester(requester)
            .reportFocus {
                focused = it
                if (it) {
                    controller.focusMovedTo(TvFocusArea.Content)
                    controller.focusMovedTo(requester)
                } else if (controller.focusedEntry == requester) {
                    controller.focusMovedTo(null)
                }
            }
            .clip(shape)
            .background(if (focused) colors.field else colors.surface, shape)
            .clickable(
                interactionSource = remember { MutableInteractionSource() },
                indication = null,
            ) { onClick() }
            .padding(horizontal = 10.dp, vertical = 6.dp),
    )
}

/**
 * The color of the availability of a title, as the shared tokens know it.
 */
@Composable
internal fun availabilityColor(controller: TvController, stateId: Int?): Color? {
    val name = availabilityStateName(stateId) ?: return null
    val tokens = controller.designTokens ?: return null
    return tokens.statusColor(name, controller.dark)?.let { Color(android.graphics.Color.parseColor(it)) }
}

/** A list of cards, as the watchlist and the library show it. */
@Composable
fun TvCardGrid(title: String, cards: List<TvCard>, empty: String, controller: TvController) {
    val colors = LocalTvColors.current
    val type = LocalTvType.current
    LazyColumn(
        modifier = Modifier.fillMaxSize(),
        verticalArrangement = Arrangement.spacedBy(TvLayout.Gap),
    ) {
        item {
            Text(
                text = title,
                fontSize = type.shelfHeading,
                fontWeight = FontWeight.Bold,
                color = colors.text,
            )
        }
        if (cards.isEmpty()) {
            item {
                Text(text = empty, fontSize = type.body, color = colors.muted)
            }
        } else {
            items(cards.chunked(5).withIndex().toList(), key = { it.index }) { (index, row) ->
                LazyRow(horizontalArrangement = Arrangement.spacedBy(TvLayout.Gap)) {
                    items(row, key = { it.id }) { card ->
                        TvCardView(card, controller, "grid-$index")
                    }
                }
            }
        }
    }
}
